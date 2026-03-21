using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using XASlave.Data;

namespace XASlave.Services;

public sealed class XagmanPeerService : IDisposable
{
    public const int DefaultHubPort = 45215;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IPluginLog log;
    private readonly string localInstanceId;
    private readonly int hubPort;
    private readonly Action<IReadOnlyList<XagmanPeerPresence>> peersUpdated;
    private readonly object syncRoot = new();
    private readonly object hubLock = new();
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly SemaphoreSlim outboundWriteLock = new(1, 1);
    private readonly Dictionary<string, HubClientSession> hubSessions = new(StringComparer.OrdinalIgnoreCase);

    private TcpListener? hubListener;
    private Task? hubAcceptTask;
    private Task? outboundClientTask;
    private StreamWriter? outboundWriter;
    private XagmanPeerPresence? localPresence;
    private IReadOnlyList<XagmanPeerPresence> peers = Array.Empty<XagmanPeerPresence>();
    private string lastStatus = "Idle";
    private bool isHub;

    public XagmanPeerService(
        IPluginLog log,
        string localInstanceId,
        int hubPort,
        Action<IReadOnlyList<XagmanPeerPresence>> peersUpdated)
    {
        this.log = log;
        this.localInstanceId = localInstanceId;
        this.hubPort = NormalizePort(hubPort);
        this.peersUpdated = peersUpdated;
    }

    public IReadOnlyList<XagmanPeerPresence> Peers
    {
        get
        {
            lock (syncRoot)
                return peers;
        }
    }

    public string LastStatus
    {
        get
        {
            lock (syncRoot)
                return lastStatus;
        }
    }

    public int HubPort => hubPort;

    public static int NormalizePort(int value)
    {
        return value >= 1 && value <= 65535 ? value : DefaultHubPort;
    }

    public void Start()
    {
        TryStartHubListener();
        outboundClientTask ??= Task.Run(() => RunOutboundClientLoopAsync(cancellationTokenSource.Token));
        SetStatus(isHub
            ? $"Xagman hub listening on 127.0.0.1:{hubPort}"
            : $"Connecting to Xagman hub 127.0.0.1:{hubPort}...");
    }

    public void PublishPresence(XagmanPeerPresence record)
    {
        lock (syncRoot)
            localPresence = ClonePresence(record);

        _ = SendRegisterAsync();
    }

    public void RepublishPresence()
    {
        _ = SendRegisterAsync();
    }

    public void Dispose()
    {
        cancellationTokenSource.Cancel();

        try
        {
            hubListener?.Stop();
        }
        catch
        {
        }

        lock (hubLock)
        {
            foreach (var session in hubSessions.Values.ToList())
            {
                try
                {
                    session.Client.Dispose();
                }
                catch
                {
                }
            }

            hubSessions.Clear();
        }

        try
        {
            outboundClientTask?.Wait(1000);
        }
        catch
        {
        }

        try
        {
            hubAcceptTask?.Wait(1000);
        }
        catch
        {
        }

        outboundWriteLock.Dispose();
        cancellationTokenSource.Dispose();
    }

    private void TryStartHubListener()
    {
        try
        {
            hubListener = new TcpListener(IPAddress.Loopback, hubPort);
            hubListener.Start();
            isHub = true;
            hubAcceptTask = Task.Run(() => AcceptHubClientsAsync(cancellationTokenSource.Token));
        }
        catch (SocketException)
        {
            isHub = false;
        }
    }

    private async Task AcceptHubClientsAsync(CancellationToken cancellationToken)
    {
        if (hubListener == null)
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await hubListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleHubClientAsync(tcpClient, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[XASlave] Xagman TCP hub accept loop failed.");
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleHubClientAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        using var client = tcpClient;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream);
        using var writer = new StreamWriter(stream) { AutoFlush = true };
        var session = new HubClientSession(client, writer);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                    break;

                var message = JsonSerializer.Deserialize<XagmanPeerMessage>(line, JsonOptions);
                if (message == null)
                    continue;

                await HandleHubMessageAsync(session, message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Xagman hub client session failed.");
        }
        finally
        {
            RemoveHubSession(session);
            await BroadcastPeerListsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleHubMessageAsync(HubClientSession session, XagmanPeerMessage message, CancellationToken cancellationToken)
    {
        if (message.MessageType != XagmanPeerMessageTypes.Register || message.Presence == null)
            return;

        var presence = ClonePresence(message.Presence);
        presence.LastSeenUtc = DateTime.UtcNow;
        session.Presence = presence;

        HubClientSession? staleSession = null;
        lock (hubLock)
        {
            if (hubSessions.TryGetValue(presence.InstanceId, out var existing) && !ReferenceEquals(existing, session))
                staleSession = existing;

            hubSessions[presence.InstanceId] = session;
        }

        if (staleSession != null)
        {
            try
            {
                staleSession.Client.Dispose();
            }
            catch
            {
            }
        }

        await BroadcastPeerListsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task BroadcastPeerListsAsync(CancellationToken cancellationToken)
    {
        List<(HubClientSession Session, XagmanPeerPresence Presence)> recipients;
        lock (hubLock)
        {
            recipients = hubSessions.Values
                .Where(session => session.Presence != null)
                .Select(session => (Session: session, Presence: ClonePresence(session.Presence!)))
                .ToList();
        }

        foreach (var (session, presence) in recipients)
        {
            var peerList = recipients
                .Where(entry => !entry.Presence.InstanceId.Equals(presence.InstanceId, StringComparison.OrdinalIgnoreCase))
                .Select(entry => ClonePresence(entry.Presence))
                .OrderBy(entry => entry.CharacterName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.ProcessId)
                .ToList();

            await SendToSessionAsync(session, new XagmanPeerMessage
            {
                MessageType = XagmanPeerMessageTypes.PeerList,
                SenderInstanceId = localInstanceId,
                TargetInstanceId = presence.InstanceId,
                SentAtUtc = DateTime.UtcNow,
                Peers = peerList,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunOutboundClientLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? tcpClient = null;
            StreamWriter? writer = null;

            try
            {
                tcpClient = new TcpClient();
                await tcpClient.ConnectAsync(IPAddress.Loopback, hubPort, cancellationToken).ConfigureAwait(false);

                using (tcpClient)
                {
                    using var stream = tcpClient.GetStream();
                    using var reader = new StreamReader(stream);
                    using (writer = new StreamWriter(stream) { AutoFlush = true })
                    {
                        lock (syncRoot)
                            outboundWriter = writer;

                        SetStatus(isHub
                            ? $"Xagman hub active on 127.0.0.1:{hubPort}"
                            : $"Connected to Xagman hub 127.0.0.1:{hubPort}");

                        await SendRegisterAsync().ConfigureAwait(false);

                        while (!cancellationToken.IsCancellationRequested)
                        {
                            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                            if (string.IsNullOrWhiteSpace(line))
                                break;

                            var message = JsonSerializer.Deserialize<XagmanPeerMessage>(line, JsonOptions);
                            if (message == null)
                                continue;

                            HandleClientMessage(message);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                SetStatus($"Waiting for Xagman hub on 127.0.0.1:{hubPort}...");
            }
            catch (IOException)
            {
                SetStatus($"Disconnected from Xagman hub 127.0.0.1:{hubPort}.");
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[XASlave] Xagman outbound TCP client loop failed.");
                SetStatus($"Xagman hub client error: {ex.Message}");
            }
            finally
            {
                lock (syncRoot)
                {
                    if (ReferenceEquals(outboundWriter, writer))
                        outboundWriter = null;
                }

                UpdatePeers(Array.Empty<XagmanPeerPresence>());
            }

            try
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void HandleClientMessage(XagmanPeerMessage message)
    {
        if (message.MessageType != XagmanPeerMessageTypes.PeerList)
            return;

        UpdatePeers((message.Peers ?? new List<XagmanPeerPresence>())
            .Where(peer => !peer.InstanceId.Equals(localInstanceId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(peer => peer.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(peer => peer.ProcessId)
            .ToList());
    }

    private async Task SendRegisterAsync()
    {
        XagmanPeerPresence? record;
        lock (syncRoot)
            record = localPresence == null ? null : ClonePresence(localPresence);

        if (record == null)
            return;

        await SendToHubAsync(new XagmanPeerMessage
        {
            MessageType = XagmanPeerMessageTypes.Register,
            SenderInstanceId = localInstanceId,
            TargetInstanceId = string.Empty,
            SentAtUtc = DateTime.UtcNow,
            Presence = record,
        }).ConfigureAwait(false);
    }

    private async Task SendToHubAsync(XagmanPeerMessage message)
    {
        StreamWriter? writer;
        lock (syncRoot)
            writer = outboundWriter;

        if (writer == null)
        {
            SetStatus($"Xagman hub connection unavailable on 127.0.0.1:{hubPort}.");
            return;
        }

        await outboundWriteLock.WaitAsync(cancellationTokenSource.Token).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(message, JsonOptions)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Failed to send message to the Xagman TCP hub.");
            SetStatus($"Failed to send to Xagman hub: {ex.Message}");
        }
        finally
        {
            outboundWriteLock.Release();
        }
    }

    private async Task SendToSessionAsync(HubClientSession session, XagmanPeerMessage message, CancellationToken cancellationToken)
    {
        await session.WriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await session.Writer.WriteLineAsync(JsonSerializer.Serialize(message, JsonOptions)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Failed to send routed Xagman TCP hub message.");
        }
        finally
        {
            session.WriteLock.Release();
        }
    }

    private void RemoveHubSession(HubClientSession session)
    {
        if (session.Presence == null)
            return;

        lock (hubLock)
        {
            if (hubSessions.TryGetValue(session.Presence.InstanceId, out var current) && ReferenceEquals(current, session))
                hubSessions.Remove(session.Presence.InstanceId);
        }
    }

    private void UpdatePeers(IReadOnlyList<XagmanPeerPresence> updatedPeers)
    {
        var clones = updatedPeers.Select(ClonePresence).ToList();

        lock (syncRoot)
            peers = clones;

        peersUpdated(clones);
    }

    private void SetStatus(string value)
    {
        lock (syncRoot)
            lastStatus = value;
    }

    private static XagmanPeerPresence ClonePresence(XagmanPeerPresence record)
    {
        return new XagmanPeerPresence
        {
            InstanceId = record.InstanceId,
            ProcessId = record.ProcessId,
            LastSeenUtc = record.LastSeenUtc,
            IsLoggedIn = record.IsLoggedIn,
            ContentId = record.ContentId,
            CharacterName = record.CharacterName,
            HomeWorld = record.HomeWorld,
            CurrentWorld = record.CurrentWorld,
            TerritoryId = record.TerritoryId,
            TerritoryName = record.TerritoryName,
            XagmanEnabled = record.XagmanEnabled,
            Role = record.Role,
            TonyMode = record.TonyMode,
            Status = record.Status,
            StatusText = record.StatusText,
            ActiveCharacter = record.ActiveCharacter,
            PreferredTonyCharacter = record.PreferredTonyCharacter,
            MeetWorld = record.MeetWorld,
            MeetAetheryte = record.MeetAetheryte,
            QueueRequestedAtUtc = record.QueueRequestedAtUtc,
            TonyCompletionRequestedAtUtc = record.TonyCompletionRequestedAtUtc,
            QueueNumber = record.QueueNumber,
            ActiveTradePartner = record.ActiveTradePartner,
            ActiveTradePartnerInstanceId = record.ActiveTradePartnerInstanceId,
            MainInventoryFreeSlots = record.MainInventoryFreeSlots,
            Gil = record.Gil,
            ItemIds = record.ItemIds == null ? new List<uint>() : new List<uint>(record.ItemIds),
            RequestedItems = record.RequestedItems == null
                ? new List<XagmanTradeRequestEntry>()
                : record.RequestedItems
                    .Select(entry => new XagmanTradeRequestEntry
                    {
                        ItemId = entry.ItemId,
                        ItemName = entry.ItemName,
                        IsHq = entry.IsHq,
                        Mode = entry.Mode,
                        Quantity = entry.Quantity,
                        TargetQuantity = entry.TargetQuantity,
                        CurrentQuantity = entry.CurrentQuantity,
                    })
                    .ToList(),
        };
    }

    private sealed class HubClientSession
    {
        public HubClientSession(TcpClient client, StreamWriter writer)
        {
            Client = client;
            Writer = writer;
        }

        public TcpClient Client { get; }
        public StreamWriter Writer { get; }
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public XagmanPeerPresence? Presence { get; set; }
    }
}
