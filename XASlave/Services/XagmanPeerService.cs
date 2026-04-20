using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using XASlave.Data;

namespace XASlave.Services;

public sealed class XagmanPeerService : IDisposable
{
    public const string DefaultHubAddress = "127.0.0.1";
    public const int DefaultHubPort = 45215;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IPluginLog log;
    private readonly string localInstanceId;
    private readonly string hubAddress;
    private readonly int hubPort;
    private readonly Action<IReadOnlyList<XagmanPeerPresence>> peersUpdated;
    private readonly object syncRoot = new();
    private readonly object hubLock = new();
    private readonly CancellationTokenSource cancellationTokenSource = new();
    private readonly SemaphoreSlim outboundWriteLock = new(1, 1);
    private readonly Dictionary<string, HubClientSession> hubSessions = new(StringComparer.OrdinalIgnoreCase);

    // Event handlers for task control
    public event Action? OnTaskStartRequested;
    public event Action? OnTaskStopRequested;
    public event Action? OnTaskRecallRequested;
    public event Action? OnTaskCompleteRequested;

    // Sequential ping scheduling to prevent message overlap
    private readonly object pingScheduleLock = new();
    private readonly Queue<string> pingQueue = new();
    private Task? pingSchedulerTask;
    private bool isPingSchedulerRunning;
    private DateTime lastPingSent = DateTime.MinValue;
    private const int PingIntervalMs = 100; // 100ms between pings to prevent overlap while staying fast
    private int registerSendRequested;
    private int registerSendRunning;

    private TcpListener? hubListener;
    private Task? hubAcceptTask;
    private Task? outboundClientTask;
    private TcpClient? outboundClient;
    private StreamWriter? outboundWriter;
    private XagmanPeerPresence? localPresence;
    private IReadOnlyList<XagmanPeerPresence> peers = Array.Empty<XagmanPeerPresence>();
    private string lastStatus = "Disconnected";
    private bool isHub;
    private bool isDisposed;
    private bool started;

    public bool IsDisposed => isDisposed;
    public bool IsStarted => started;
    public bool IsConnected
    {
        get
        {
            lock (syncRoot)
                return started && outboundClient != null && outboundWriter != null;
        }
    }

    public XagmanPeerService(
        IPluginLog log,
        string localInstanceId,
        string hubAddress,
        int hubPort,
        Action<IReadOnlyList<XagmanPeerPresence>> peersUpdated)
    {
        this.log = log;
        this.localInstanceId = localInstanceId;
        this.hubAddress = NormalizeHubAddress(hubAddress);
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
    public string HubAddress => hubAddress;
    public string HubEndpoint => $"{FormatEndpointHost(hubAddress)}:{hubPort}";

    public static string NormalizeHubAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DefaultHubAddress;

        var normalized = value.Trim();
        if (normalized.StartsWith("[", StringComparison.Ordinal)
            && normalized.EndsWith("]", StringComparison.Ordinal)
            && normalized.Length > 2)
        {
            normalized = normalized[1..^1];
        }

        return normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? DefaultHubAddress
            : normalized;
    }

    public static int NormalizePort(int value)
    {
        return value >= 1 && value <= 65535 ? value : DefaultHubPort;
    }

    public void Start()
    {
        if (isDisposed || started)
            return;

        started = true;
        TryStartHubListener();
        if (outboundClientTask == null || outboundClientTask.IsCompleted)
            outboundClientTask = Task.Run(() => RunOutboundClientLoopAsync(cancellationTokenSource.Token));
        SetStatus(isHub
            ? $"Starting Xagman hub on {HubEndpoint}..."
            : $"Connecting to Xagman hub {HubEndpoint}...");
    }

    public void Stop()
    {
        if (isDisposed)
            return;

        started = false;

        StreamWriter? writerToDispose;
        TcpClient? clientToDispose;
        lock (syncRoot)
        {
            localPresence = null;
            writerToDispose = outboundWriter;
            clientToDispose = outboundClient;
            outboundWriter = null;
            outboundClient = null;
        }

        try
        {
            writerToDispose?.Dispose();
        }
        catch
        {
        }

        try
        {
            clientToDispose?.Dispose();
        }
        catch
        {
        }

        try
        {
            hubListener?.Stop();
        }
        catch
        {
        }

        hubListener = null;
        isHub = false;

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

        lock (pingScheduleLock)
        {
            pingQueue.Clear();
            isPingSchedulerRunning = false;
        }

        Interlocked.Exchange(ref registerSendRequested, 0);

        UpdatePeers(Array.Empty<XagmanPeerPresence>());
        SetStatus($"Disconnected from Xagman hub {HubEndpoint}.");
    }

    public void PublishPresence(XagmanPeerPresence record)
    {
        lock (syncRoot)
            localPresence = ClonePresence(record);

        if (!started)
            return;

        QueueRegisterSend();
    }

    public void RepublishPresence()
    {
        if (!started)
            return;

        QueueRegisterSend();
    }

    private void QueueRegisterSend()
    {
        if (!started || isDisposed)
            return;

        Interlocked.Exchange(ref registerSendRequested, 1);
        if (Interlocked.CompareExchange(ref registerSendRunning, 1, 0) != 0)
            return;

        _ = Task.Run(ProcessRegisterQueueAsync);
    }

    private async Task ProcessRegisterQueueAsync()
    {
        try
        {
            while (!isDisposed && started)
            {
                if (Interlocked.Exchange(ref registerSendRequested, 0) == 0)
                    break;

                await SendRegisterAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Xagman background register sender failed.");
        }
        finally
        {
            Interlocked.Exchange(ref registerSendRunning, 0);

            if (!isDisposed
                && started
                && Interlocked.CompareExchange(ref registerSendRequested, 0, 0) != 0
                && Interlocked.CompareExchange(ref registerSendRunning, 1, 0) == 0)
            {
                _ = Task.Run(ProcessRegisterQueueAsync);
            }
        }
    }

    public async Task<bool> WaitForConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!started || isDisposed)
            return false;
        if (IsConnected)
            return true;

        try
        {
            var deadlineUtc = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadlineUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsConnected)
                    return true;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        return IsConnected;
    }

    public void Dispose()
    {
        if (isDisposed)
            return;

        Stop();
        isDisposed = true;
        cancellationTokenSource.Cancel();

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

        // Clean up ping scheduler
        lock (pingScheduleLock)
        {
            isPingSchedulerRunning = false;
            pingQueue.Clear();
        }

        try
        {
            pingSchedulerTask?.Wait(1000);
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
            var listenerAddress = GetHubListenerAddress();
            if (listenerAddress == null)
            {
                isHub = false;
                return;
            }

            hubListener = new TcpListener(listenerAddress, hubPort);
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

        while (!cancellationToken.IsCancellationRequested && started)
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
            catch (SocketException ex) when (!started
                                             || isDisposed
                                             || ex.SocketErrorCode == SocketError.OperationAborted
                                             || ex.SocketErrorCode == SocketError.Interrupted
                                             || ex.ErrorCode == 995)
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
            while (!cancellationToken.IsCancellationRequested && started)
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
        switch (message.MessageType)
        {
            case XagmanPeerMessageTypes.Register:
                if (message.Presence == null)
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
                break;

            case XagmanPeerMessageTypes.StartTask:
            case XagmanPeerMessageTypes.StopTask:
            case XagmanPeerMessageTypes.RecallTask:
            case XagmanPeerMessageTypes.CompleteTask:
                await RouteHubControlMessageAsync(message, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task RouteHubControlMessageAsync(XagmanPeerMessage message, CancellationToken cancellationToken)
    {
        List<(HubClientSession Session, XagmanPeerPresence Presence)> recipients;
        lock (hubLock)
        {
            recipients = hubSessions.Values
                .Where(session => session.Presence != null)
                .Select(session => (Session: session, Presence: ClonePresence(session.Presence!)))
                .ToList();
        }

        var targets = recipients
            .Where(entry => entry.Presence.XagmanEnabled)
            .Where(entry => !entry.Presence.InstanceId.Equals(message.SenderInstanceId, StringComparison.OrdinalIgnoreCase))
            .Where(entry => string.IsNullOrWhiteSpace(message.TargetInstanceId)
                || entry.Presence.InstanceId.Equals(message.TargetInstanceId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var (targetSession, targetPresence) in targets)
        {
            await SendToSessionAsync(targetSession, new XagmanPeerMessage
            {
                MessageType = message.MessageType,
                SenderInstanceId = message.SenderInstanceId,
                TargetInstanceId = targetPresence.InstanceId,
                SentAtUtc = DateTime.UtcNow,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task BroadcastPeerListsAsync(CancellationToken cancellationToken)
    {
        if (!started || isDisposed)
            return;

        List<(HubClientSession Session, XagmanPeerPresence Presence)> recipients;
        lock (hubLock)
        {
            recipients = hubSessions.Values
                .Where(session => session.Presence != null)
                .Select(session => (Session: session, Presence: ClonePresence(session.Presence!)))
                .ToList();
        }

        // Schedule sequential pings to prevent message overlap
        lock (pingScheduleLock)
        {
            foreach (var (session, presence) in recipients)
            {
                pingQueue.Enqueue(presence.InstanceId);
            }

            // Start ping scheduler if not already running
            if (!isPingSchedulerRunning)
            {
                isPingSchedulerRunning = true;
                pingSchedulerTask = Task.Run(() => PingSchedulerLoop(cancellationToken));
            }
        }
    }

    private async Task PingSchedulerLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && pingQueue.Count > 0)
            {
                string? targetInstanceId = null;

                lock (pingScheduleLock)
                {
                    if (pingQueue.Count > 0)
                        targetInstanceId = pingQueue.Dequeue();
                    else
                    {
                        isPingSchedulerRunning = false;
                        break;
                    }
                }

                if (targetInstanceId != null)
                {
                    // Rate limiting to prevent overlap while staying fast
                    var delaySinceLastPing = (DateTime.UtcNow - lastPingSent).TotalMilliseconds;
                    if (delaySinceLastPing < PingIntervalMs)
                    {
                        await Task.Delay(PingIntervalMs - (int)delaySinceLastPing, cancellationToken).ConfigureAwait(false);
                    }

                    await SendPingToTarget(targetInstanceId, cancellationToken).ConfigureAwait(false);
                    lastPingSent = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            log.Error(ex, "[TCP] [XASlave] Error in ping scheduler loop");
        }
        finally
        {
            isPingSchedulerRunning = false;
        }
    }

    private async Task SendPingToTarget(string targetInstanceId, CancellationToken cancellationToken)
    {
        List<(HubClientSession Session, XagmanPeerPresence Presence)> recipients;
        lock (hubLock)
        {
            recipients = hubSessions.Values
                .Where(session => session.Presence != null)
                .Select(session => (Session: session, Presence: ClonePresence(session.Presence!)))
                .ToList();
        }

        var targetRecipient = recipients.FirstOrDefault(r => r.Presence.InstanceId.Equals(targetInstanceId, StringComparison.OrdinalIgnoreCase));
        if (targetRecipient.Session == null) return;

        var peerList = recipients
            .Where(entry => !entry.Presence.InstanceId.Equals(targetInstanceId, StringComparison.OrdinalIgnoreCase))
            .Select(entry => ClonePresence(entry.Presence))
            .OrderBy(entry => entry.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.ProcessId)
            .ToList();

        await SendToSessionAsync(targetRecipient.Session, new XagmanPeerMessage
        {
            MessageType = XagmanPeerMessageTypes.PeerList,
            SenderInstanceId = localInstanceId,
            TargetInstanceId = targetInstanceId,
            SentAtUtc = DateTime.UtcNow,
            Peers = peerList,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunOutboundClientLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && started)
        {
            TcpClient? tcpClient = null;
            StreamWriter? writer = null;

            try
            {
                tcpClient = new TcpClient();
                var connectAddress = GetPreferredHubConnectAddress();
                if (connectAddress != null)
                    await tcpClient.ConnectAsync(connectAddress, hubPort, cancellationToken).ConfigureAwait(false);
                else
                    await tcpClient.ConnectAsync(hubAddress, hubPort, cancellationToken).ConfigureAwait(false);

                using (tcpClient)
                {
                    using var stream = tcpClient.GetStream();
                    using var reader = new StreamReader(stream);
                    using (writer = new StreamWriter(stream) { AutoFlush = true })
                    {
                        lock (syncRoot)
                        {
                            outboundClient = tcpClient;
                            outboundWriter = writer;
                        }

                        SetStatus(isHub
                            ? $"Xagman hub active on {HubEndpoint}"
                            : $"Connected to Xagman hub {HubEndpoint}");

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
                SetStatus($"Waiting for Xagman hub on {HubEndpoint}...");
            }
            catch (IOException)
            {
                SetStatus($"Disconnected from Xagman hub {HubEndpoint}.");
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
                    if (ReferenceEquals(outboundClient, tcpClient))
                        outboundClient = null;
                    if (ReferenceEquals(outboundWriter, writer))
                        outboundWriter = null;
                }

                UpdatePeers(Array.Empty<XagmanPeerPresence>());
            }

            if (!started)
                break;

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
        switch (message.MessageType)
        {
            case XagmanPeerMessageTypes.PeerList:
                UpdatePeers((message.Peers ?? new List<XagmanPeerPresence>())
                    .Where(peer => !peer.InstanceId.Equals(localInstanceId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(peer => peer.CharacterName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(peer => peer.ProcessId)
                    .ToList());
                break;

            case XagmanPeerMessageTypes.StartTask:
                Plugin.Log.Information($"[XagmanPeerService] Received start task command from {message.SenderInstanceId}");
                // Trigger Xagman task start on this client
                TriggerXagmanTaskStart();
                break;

            case XagmanPeerMessageTypes.StopTask:
                Plugin.Log.Information($"[XagmanPeerService] Received stop task command from {message.SenderInstanceId}");
                // Trigger Xagman task stop on this client
                TriggerXagmanTaskStop();
                break;

            case XagmanPeerMessageTypes.RecallTask:
                Plugin.Log.Information($"[XagmanPeerService] Received recall task command from {message.SenderInstanceId}");
                TriggerXagmanTaskRecall();
                break;

            case XagmanPeerMessageTypes.CompleteTask:
                Plugin.Log.Information($"[XagmanPeerService] Received complete task command from {message.SenderInstanceId}");
                TriggerXagmanTaskComplete();
                break;
        }
    }

    private void TriggerXagmanTaskStart()
    {
        Plugin.Log.Information("[XagmanPeerService] Triggering Xagman task start");
        OnTaskStartRequested?.Invoke();
    }

    private void TriggerXagmanTaskStop()
    {
        Plugin.Log.Information("[XagmanPeerService] Triggering Xagman task stop");
        OnTaskStopRequested?.Invoke();
    }

    private void TriggerXagmanTaskRecall()
    {
        Plugin.Log.Information("[XagmanPeerService] Triggering Xagman task recall");
        OnTaskRecallRequested?.Invoke();
    }

    private void TriggerXagmanTaskComplete()
    {
        Plugin.Log.Information("[XagmanPeerService] Triggering Xagman task completion");
        OnTaskCompleteRequested?.Invoke();
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

    private async Task<bool> SendToHubAsync(XagmanPeerMessage message)
    {
        if (!started || isDisposed)
            return false;
        if (!IsConnected && !await WaitForConnectionAsync(TimeSpan.FromSeconds(2), cancellationTokenSource.Token).ConfigureAwait(false))
        {
            SetStatus($"Xagman hub connection unavailable on {HubEndpoint}.");
            return false;
        }

        StreamWriter? writer;
        TcpClient? client;
        lock (syncRoot)
        {
            writer = outboundWriter;
            client = outboundClient;
        }

        if (writer == null)
        {
            SetStatus($"Xagman hub connection unavailable on {HubEndpoint}.");
            return false;
        }

        await outboundWriteLock.WaitAsync(cancellationTokenSource.Token).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(message, JsonOptions)).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Categorize connection issues as multiclient latency instead of generic warnings
            var errorType = ex is IOException || ex is System.Net.Sockets.SocketException
                ? "[Multiclient Latency]"
                : "[TCP Error]";

            log.Information($"{errorType} [XASlave] Failed to send message to the Xagman TCP hub: {ex.Message}");
            ResetOutboundConnection(client, $"Failed to send to Xagman hub: {ex.Message}");

            if (!(ex is IOException || ex is System.Net.Sockets.SocketException))
            {
                log.Warning(ex, "[TCP] [XASlave] Unexpected error in Xagman TCP hub message.");
            }

            return false;
        }
        finally
        {
            outboundWriteLock.Release();
        }
    }

    private void ResetOutboundConnection(TcpClient? expectedClient, string status)
    {
        TcpClient? clientToDispose = null;
        lock (syncRoot)
        {
            if (expectedClient != null && outboundClient != null && !ReferenceEquals(outboundClient, expectedClient))
                return;

            clientToDispose = outboundClient;
            outboundClient = null;
            outboundWriter = null;
        }

        try
        {
            clientToDispose?.Dispose();
        }
        catch
        {
        }

        UpdatePeers(Array.Empty<XagmanPeerPresence>());
        SetStatus(status);
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
            // Categorize connection issues as multiclient latency instead of generic warnings
            var errorType = ex is IOException || ex is System.Net.Sockets.SocketException
                ? "[Multiclient Latency]"
                : "[TCP Error]";

            log.Information($"{errorType} [XASlave] Failed to send routed Xagman TCP hub message: {ex.Message}");

            if (!(ex is IOException || ex is System.Net.Sockets.SocketException))
            {
                log.Warning(ex, "[TCP] [XASlave] Unexpected error in routed Xagman TCP hub message.");
            }
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
            TotalCharacters = record.TotalCharacters,
            CompletedCharacters = record.CompletedCharacters,
            QueueNumber = record.QueueNumber,
            ActiveTradePartner = record.ActiveTradePartner,
            ActiveTradePartnerInstanceId = record.ActiveTradePartnerInstanceId,
            TonyRotationReady = record.TonyRotationReady,
            MainInventoryFreeSlots = record.MainInventoryFreeSlots,
            Gil = record.Gil,
            TonyGilMinimum = record.TonyGilMinimum,
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

    private IPAddress? GetHubListenerAddress()
    {
        return ResolveHubAddresses(hubAddress)
            .Where(address => !IPAddress.Any.Equals(address) && !IPAddress.IPv6Any.Equals(address))
            .FirstOrDefault(IsLocalAddress);
    }

    private IPAddress? GetPreferredHubConnectAddress()
    {
        return ResolveHubAddresses(hubAddress)
            .Where(address => !IPAddress.Any.Equals(address) && !IPAddress.IPv6Any.Equals(address))
            .FirstOrDefault();
    }

    private static IReadOnlyList<IPAddress> ResolveHubAddresses(string address)
    {
        if (IPAddress.TryParse(address, out var parsedAddress))
            return OrderResolvedAddresses(new[] { parsedAddress });

        try
        {
            return OrderResolvedAddresses(Dns.GetHostAddresses(address));
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }
    }

    private static IReadOnlyList<IPAddress> OrderResolvedAddresses(IEnumerable<IPAddress> addresses)
    {
        return addresses
            .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            .OrderByDescending(address => address.AddressFamily == AddressFamily.InterNetwork)
            .ToArray();
    }

    private static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                    continue;

                var properties = networkInterface.GetIPProperties();
                foreach (var unicastAddress in properties.UnicastAddresses)
                {
                    if (AreEquivalentAddresses(unicastAddress.Address, address))
                        return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool AreEquivalentAddresses(IPAddress left, IPAddress right)
    {
        if (left.Equals(right))
            return true;

        if (left.IsIPv4MappedToIPv6)
            left = left.MapToIPv4();
        if (right.IsIPv4MappedToIPv6)
            right = right.MapToIPv4();

        return left.Equals(right);
    }

    private static string FormatEndpointHost(string address)
    {
        return address.Contains(':') && !address.StartsWith("[", StringComparison.Ordinal)
            ? $"[{address}]"
            : address;
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

    public async Task<bool> SendStartTaskToAllPeersAsync(CancellationToken cancellationToken = default)
    {
        if (!started)
            return false;

        return await SendToHubAsync(new XagmanPeerMessage
        {
            MessageType = XagmanPeerMessageTypes.StartTask,
            SenderInstanceId = localInstanceId,
            TargetInstanceId = string.Empty,
            SentAtUtc = DateTime.UtcNow,
        }).ConfigureAwait(false);
    }

    public async Task<bool> SendStopTaskToAllPeersAsync(CancellationToken cancellationToken = default)
    {
        if (!started)
            return false;

        return await SendToHubAsync(new XagmanPeerMessage
        {
            MessageType = XagmanPeerMessageTypes.StopTask,
            SenderInstanceId = localInstanceId,
            TargetInstanceId = string.Empty,
            SentAtUtc = DateTime.UtcNow,
        }).ConfigureAwait(false);
    }

    public async Task<bool> SendRecallTaskToAllPeersAsync(CancellationToken cancellationToken = default)
    {
        if (!started)
            return false;

        return await SendToHubAsync(new XagmanPeerMessage
        {
            MessageType = XagmanPeerMessageTypes.RecallTask,
            SenderInstanceId = localInstanceId,
            TargetInstanceId = string.Empty,
            SentAtUtc = DateTime.UtcNow,
        }).ConfigureAwait(false);
    }

    public async Task<bool> SendCompleteTaskToAllPeersAsync(CancellationToken cancellationToken = default)
    {
        if (!started)
            return false;

        return await SendToHubAsync(new XagmanPeerMessage
        {
            MessageType = XagmanPeerMessageTypes.CompleteTask,
            SenderInstanceId = localInstanceId,
            TargetInstanceId = string.Empty,
            SentAtUtc = DateTime.UtcNow,
        }).ConfigureAwait(false);
    }
}
