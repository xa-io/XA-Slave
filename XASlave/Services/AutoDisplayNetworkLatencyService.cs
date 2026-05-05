using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public sealed class AutoDisplayNetworkLatencyService : IDisposable
{
    private const int AddressFamilyInet = 2;
    private const int AddressFamilyInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int TcpStateEstablished = 5;
    private const int TcpStateListen = 2;
    private const string DtrTitle = "XASlave-DisplayNetworkLatency";
    private const string LegacyDtrTitle = "XA Network Latency";

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IDtrBar dtrBar;
    private readonly IPluginLog log;
    private readonly Ping pingSender = new();
    private readonly object sync = new();
    private readonly float[] history = new float[60];

    private bool enabled;
    private bool subscribed;
    private string format = "Ping: {0} ms";
    private CancellationTokenSource? cancellationTokenSource;
    private Task? monitorTask;
    private DateTime lastUiUpdateUtc = DateTime.MinValue;
    private bool needsAddressRefresh = true;
    private IntPtr tcpBuffer = IntPtr.Zero;
    private int tcpBufferSize;
    private int historyIndex;
    private int filledCount;
    private long currentPing = -1;
    private IDtrBarEntry? dtrEntry;
    private DateTime nextDtrAcquireUtc = DateTime.MinValue;
    private bool hasLoggedDtrAcquireFailure;
    private bool legacyDtrCleanupAttempted;
    private string lastDtrText = string.Empty;
    private string lastDtrTooltip = string.Empty;
    private bool lastDtrShown;
    private IPAddress serverAddress = IPAddress.Loopback;
    private ushort serverPort;
    private IPAddress observedServerAddress = IPAddress.Loopback;
    private ushort observedServerPort;

    public AutoDisplayNetworkLatencyService(
        IFramework framework,
        IClientState clientState,
        IDtrBar dtrBar,
        IPluginLog log)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.dtrBar = dtrBar;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";
    public string LastActionText { get; private set; } = "No actions yet.";
    public long CurrentPing { get; private set; } = -1;
    public float AveragePing { get; private set; }
    public float MinimumPing { get; private set; }
    public float MaximumPing { get; private set; }
    public float LossRate { get; private set; }
    public string ServerEndpoint { get; private set; } = "Unavailable";
    public string ObservedEndpoint { get; private set; } = "Unavailable";

    public static string NormalizeFormat(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "Ping: {0} ms" : value.Trim();
    }

    public void ApplyConfiguration(string format)
    {
        this.format = NormalizeFormat(format);

        if (enabled)
            StatusText = BuildStatusText();
    }

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            StopMonitoring();
            Unsubscribe();
            HideDtrEntry();
            StatusText = "Disabled";
            return false;
        }

        enabled = true;
        Subscribe();
        StartMonitoring();
        PublishPlaceholderDtr("Resolving game endpoint.");
        StatusText = BuildStatusText();
        return true;
    }

    public unsafe void Dispose()
    {
        enabled = false;
        StopMonitoring();
        Unsubscribe();
        RemoveDtrEntry();
        pingSender.Dispose();

        if (tcpBuffer != IntPtr.Zero)
        {
            NativeMemory.Free((void*)tcpBuffer);
            tcpBuffer = IntPtr.Zero;
        }
    }

    private string BuildStatusText()
    {
        return $"Enabled - pinging the detected game endpoint once per second. Current: {(CurrentPing < 0 ? "--" : CurrentPing)} ms.";
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        clientState.TerritoryChanged += OnTerritoryChanged;
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        clientState.TerritoryChanged -= OnTerritoryChanged;
        subscribed = false;
    }

    private void OnTerritoryChanged(uint _)
    {
        needsAddressRefresh = true;
    }

    private void StartMonitoring()
    {
        StopMonitoring();
        cancellationTokenSource = new CancellationTokenSource();
        monitorTask = Task.Run(() => MonitorLoop(cancellationTokenSource.Token), cancellationTokenSource.Token);
    }

    private void StopMonitoring()
    {
        cancellationTokenSource?.Cancel();

        try
        {
            monitorTask?.Wait(2000);
        }
        catch
        {
            // Best-effort shutdown during plugin unload.
        }

        monitorTask = null;
        cancellationTokenSource?.Dispose();
        cancellationTokenSource = null;
        ResetMetrics();
    }

    private async Task MonitorLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!enabled || !clientState.IsLoggedIn)
                {
                    ResetMetrics();
                    PublishPlaceholderDtr("Log in to resolve the game endpoint.");
                    await Task.Delay(1500, cancellationToken);
                    continue;
                }

                if (needsAddressRefresh || serverPort == 0 || IPAddress.IsLoopback(serverAddress))
                    UpdateAddressInfo();

                if (serverPort == 0 || IPAddress.IsLoopback(serverAddress))
                {
                    RecordPing(-1);
                    PublishCurrentMetricsToDtr("Resolving game endpoint.");
                    await Task.Delay(1500, cancellationToken);
                    continue;
                }

                var reply = await pingSender.SendPingAsync(serverAddress, 1000);
                RecordPing(reply.Status == IPStatus.Success ? reply.RoundtripTime : -1);
                PublishCurrentMetricsToDtr();
                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[XASlave] Display Network Latency monitor loop failed.");
                RecordPing(-1);
                PublishCurrentMetricsToDtr("Network latency check failed; retrying.");
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    private void PublishPlaceholderDtr(string tooltip)
    {
        CurrentPing = -1;
        ServerEndpoint = "Unavailable";
        ObservedEndpoint = "Unavailable";
        AveragePing = 0;
        MinimumPing = 0;
        MaximumPing = 0;
        LossRate = 0;
        StatusText = BuildStatusText();

        framework.RunOnTick(() =>
        {
            if (!enabled)
                return;

            SetDtrState(FormatLatencyText(-1), tooltip, true);
        });
    }

    private void PublishCurrentMetricsToDtr(string? tooltipOverride = null)
    {
        if ((DateTime.UtcNow - lastUiUpdateUtc).TotalMilliseconds < 500)
            return;

        lastUiUpdateUtc = DateTime.UtcNow;

        long currentPingSnapshot;
        float[] historySnapshot;
        int filledCountSnapshot;
        string serverEndpointSnapshot;
        string observedEndpointSnapshot;

        lock (sync)
        {
            currentPingSnapshot = currentPing;
            historySnapshot = (float[])history.Clone();
            filledCountSnapshot = filledCount;
            serverEndpointSnapshot = serverPort == 0 ? "Unavailable" : $"{serverAddress}:{serverPort}";
            observedEndpointSnapshot = observedServerPort == 0 ? "Unavailable" : $"{observedServerAddress}:{observedServerPort}";
        }

        var tooltip = tooltipOverride;
        if (string.IsNullOrWhiteSpace(tooltip))
        {
            tooltip = observedEndpointSnapshot != "Unavailable" && observedEndpointSnapshot != serverEndpointSnapshot
                ? $"Observed {observedEndpointSnapshot} -> Effective {serverEndpointSnapshot}"
                : serverEndpointSnapshot;
        }

        framework.RunOnTick(() =>
        {
            if (!enabled)
                return;

            CurrentPing = currentPingSnapshot;
            ServerEndpoint = serverEndpointSnapshot;
            ObservedEndpoint = observedEndpointSnapshot;
            UpdateStats(historySnapshot, filledCountSnapshot);
            StatusText = BuildStatusText();
            SetDtrState(FormatLatencyText(currentPingSnapshot), tooltip, true);
        });
    }

    private void SetDtrState(string text, string tooltip, bool shown)
    {
        try
        {
            if (!shown)
            {
                if (lastDtrShown && dtrEntry != null)
                    dtrEntry.Shown = false;

                lastDtrText = string.Empty;
                lastDtrTooltip = string.Empty;
                lastDtrShown = false;
                return;
            }

            var entry = TryGetDtrEntry();
            if (entry == null)
                return;

            if (!string.Equals(lastDtrText, text, StringComparison.Ordinal))
                entry.Text = text;

            if (!string.Equals(lastDtrTooltip, tooltip, StringComparison.Ordinal))
                entry.Tooltip = tooltip;

            entry.Shown = true;
            lastDtrText = text;
            lastDtrTooltip = tooltip;
            lastDtrShown = true;
            LastActionText = entry.UserHidden
                ? "DTR entry is hidden in /xlsettings."
                : "DTR entry visible.";
        }
        catch (Exception ex)
        {
            dtrEntry = null;
            nextDtrAcquireUtc = DateTime.UtcNow.AddSeconds(5);
            lastDtrShown = false;
            LastActionText = "DTR entry update failed; retrying.";
            if (!hasLoggedDtrAcquireFailure)
            {
                log.Warning(ex, "[XASlave] Display Network Latency could not update its DTR entry; the service will retry without stopping the plugin.");
                hasLoggedDtrAcquireFailure = true;
            }
        }
    }

    private IDtrBarEntry? TryGetDtrEntry()
    {
        if (dtrEntry != null)
            return dtrEntry;

        if (DateTime.UtcNow < nextDtrAcquireUtc)
            return null;

        try
        {
            TryRemoveLegacyDtrEntry();
            dtrEntry = dtrBar.Get(DtrTitle, Dalamud.Game.Text.SeStringHandling.SeString.Empty);
            hasLoggedDtrAcquireFailure = false;
            LastActionText = "DTR entry acquired.";
            return dtrEntry;
        }
        catch (ArgumentException ex)
        {
            nextDtrAcquireUtc = DateTime.UtcNow.AddSeconds(1);
            LastActionText = "DTR entry is already registered; retrying.";
            if (!hasLoggedDtrAcquireFailure)
            {
                log.Warning(ex, "[XASlave] Display Network Latency could not acquire its DTR entry; retrying after requesting stale entry removal.");
                hasLoggedDtrAcquireFailure = true;
            }

            try
            {
                dtrBar.Remove(DtrTitle);
            }
            catch
            {
                // DTR cleanup is best-effort; the service can keep running without a bar entry.
            }

            return null;
        }
        catch (Exception ex)
        {
            nextDtrAcquireUtc = DateTime.UtcNow.AddSeconds(5);
            LastActionText = "DTR entry is unavailable; retrying.";
            if (!hasLoggedDtrAcquireFailure)
            {
                log.Warning(ex, "[XASlave] Display Network Latency could not acquire its DTR entry; the service will keep running without crashing plugin startup.");
                hasLoggedDtrAcquireFailure = true;
            }

            return null;
        }
    }

    private void HideDtrEntry()
    {
        SetDtrState(string.Empty, string.Empty, false);
    }

    private void RemoveDtrEntry()
    {
        try
        {
            if (dtrEntry != null)
            {
                dtrEntry.Shown = false;
                dtrEntry.Remove();
            }
        }
        catch
        {
            // DTR cleanup is best-effort during plugin unload.
        }
        finally
        {
            try
            {
                dtrBar.Remove(DtrTitle);
            }
            catch
            {
                // DTR cleanup is best-effort during plugin unload.
            }

            dtrEntry = null;
            lastDtrText = string.Empty;
            lastDtrTooltip = string.Empty;
            lastDtrShown = false;
        }
    }

    private void TryRemoveLegacyDtrEntry()
    {
        if (legacyDtrCleanupAttempted)
            return;

        legacyDtrCleanupAttempted = true;
        try
        {
            dtrBar.Remove(LegacyDtrTitle);
        }
        catch
        {
            // The old friendly title may not exist or may already be owned by a stale entry.
        }
    }

    private string FormatLatencyText(long ping)
    {
        try
        {
            return string.Format(format, ping < 0 ? "--" : ping.ToString());
        }
        catch
        {
            return ping < 0 ? "Ping: --" : $"Ping: {ping} ms";
        }
    }

    private void UpdateStats(float[] samples, int sampleCount)
    {
        if (sampleCount == 0)
        {
            AveragePing = 0;
            MinimumPing = 0;
            MaximumPing = 0;
            LossRate = 0;
            return;
        }

        var min = float.MaxValue;
        var max = 0f;
        var total = 0f;
        var validCount = 0;
        var lostCount = 0;

        for (var i = 0; i < sampleCount; i++)
        {
            var value = samples[i];
            if (value <= 0)
            {
                lostCount++;
                continue;
            }

            if (value < min)
                min = value;

            if (value > max)
                max = value;

            total += value;
            validCount++;
        }

        AveragePing = validCount == 0 ? 0f : total / validCount;
        MinimumPing = min == float.MaxValue ? 0f : min;
        MaximumPing = max;
        LossRate = sampleCount == 0 ? 0f : (float)lostCount / sampleCount;
    }

    private void RecordPing(long ping)
    {
        lock (sync)
        {
            currentPing = ping;
            history[historyIndex] = ping < 0 ? 0f : ping;
            historyIndex = (historyIndex + 1) % history.Length;
            if (filledCount < history.Length)
                filledCount++;
        }
    }

    private void ResetMetrics()
    {
        lock (sync)
        {
            CurrentPing = -1;
            currentPing = -1;
            Array.Clear(history);
            historyIndex = 0;
            filledCount = 0;
            serverAddress = IPAddress.Loopback;
            serverPort = 0;
            observedServerAddress = IPAddress.Loopback;
            observedServerPort = 0;
            needsAddressRefresh = true;
            ServerEndpoint = "Unavailable";
            ObservedEndpoint = "Unavailable";
        }
    }

    private void UpdateAddressInfo()
    {
        try
        {
            var currentPid = (uint)Environment.ProcessId;
            if (!TryFindBestEndpointForPid(currentPid, out var observed))
            {
                lock (sync)
                {
                    serverAddress = IPAddress.Loopback;
                    serverPort = 0;
                    observedServerAddress = IPAddress.Loopback;
                    observedServerPort = 0;
                }

                return;
            }

            var effective = observed;
            if (IPAddress.IsLoopback(observed.Address) && TryFindProxyPidByListenPort(observed.Port, out var proxyPid))
            {
                if (TryFindBestEndpointForPid(proxyPid, out var proxyEndpoint) && !IPAddress.IsLoopback(proxyEndpoint.Address))
                    effective = proxyEndpoint;
            }

            lock (sync)
            {
                observedServerAddress = observed.Address;
                observedServerPort = observed.Port;
                serverAddress = effective.Address;
                serverPort = effective.Port;
                needsAddressRefresh = false;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Display Network Latency failed while resolving the game endpoint.");
        }
    }

    private bool TryFindBestEndpointForPid(uint pid, out ConnectionEndpoint endpoint)
    {
        if (TryFindBestEndpointForPid(pid, true, out endpoint))
            return true;

        if (TryFindBestEndpointForPid(pid, false, out endpoint))
            return true;

        endpoint = default;
        return false;
    }

    private bool TryFindBestEndpointForPid(uint pid, bool onlyXivPorts, out ConnectionEndpoint endpoint)
    {
        endpoint = default;
        var found = false;

        ScanTcpTable(pid, AddressFamilyInet, onlyXivPorts, ref endpoint, ref found);
        ScanTcpTable(pid, AddressFamilyInet6, onlyXivPorts, ref endpoint, ref found);
        return found;
    }

    private unsafe void ScanTcpTable(uint pid, int ipVersion, bool onlyXivPorts, ref ConnectionEndpoint best, ref bool found)
    {
        var requiredSize = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref requiredSize, false, ipVersion, TcpTableOwnerPidAll, 0);
        if (requiredSize <= 0)
            return;

        if (tcpBufferSize < requiredSize)
        {
            if (tcpBuffer != IntPtr.Zero)
                NativeMemory.Free((void*)tcpBuffer);

            tcpBufferSize = requiredSize;
            tcpBuffer = (IntPtr)NativeMemory.Alloc((nuint)tcpBufferSize);
        }

        if (GetExtendedTcpTable(tcpBuffer, ref requiredSize, false, ipVersion, TcpTableOwnerPidAll, 0) != 0)
            return;

        var buffer = (byte*)tcpBuffer;
        var entryCount = Unsafe.Read<int>(buffer);
        switch (ipVersion)
        {
            case AddressFamilyInet:
            {
                var rowPtr = (TcpRow*)(buffer + sizeof(int));
                for (var i = 0; i < entryCount; i++)
                {
                    ref readonly var row = ref rowPtr[i];
                    if (row.OwningPid != pid || row.State != TcpStateEstablished)
                        continue;

                    var port = BinaryPrimitives.ReverseEndianness((ushort)row.RemotePort);
                    if (IsFilteredPort(port) || (onlyXivPorts && !IsXivPort(port)) || row.RemoteAddress == 0)
                        continue;

                    var isLoopback = row.RemoteAddress == 0x0100007F;
                    var isPrivate = IsPrivateOrLocalIpv4(row.RemoteAddress);
                    var score = ScoreEndpoint(isLoopback, isPrivate, port, IsXivPort(port));
                    if (found && score <= best.Score)
                        continue;

                    best = new ConnectionEndpoint(new IPAddress(row.RemoteAddress), port, score);
                    found = true;
                }

                break;
            }
            case AddressFamilyInet6:
            {
                var rowPtr = (Tcp6Row*)(buffer + sizeof(int));
                Span<byte> addressBytes = stackalloc byte[16];

                for (var i = 0; i < entryCount; i++)
                {
                    ref readonly var row = ref rowPtr[i];
                    if (row.OwningPid != pid || row.State != TcpStateEstablished)
                        continue;

                    var port = BinaryPrimitives.ReverseEndianness((ushort)row.RemotePort);
                    if (IsFilteredPort(port) || (onlyXivPorts && !IsXivPort(port)))
                        continue;

                    var isUnspecified = true;
                    var isLoopback = true;
                    for (var j = 0; j < 16; j++)
                    {
                        var b = row.RemoteAddress[j];
                        isUnspecified &= b == 0;
                        isLoopback &= j == 15 ? b == 1 : b == 0;
                        addressBytes[j] = b;
                    }

                    if (isUnspecified)
                        continue;

                    fixed (byte* ptr = row.RemoteAddress)
                    {
                        var isPrivate = IsPrivateOrLocalIpv6(ptr);
                        var score = ScoreEndpoint(isLoopback, isPrivate, port, IsXivPort(port));
                        if (found && score <= best.Score)
                            continue;

                        best = new ConnectionEndpoint(new IPAddress(addressBytes), port, score);
                        found = true;
                    }
                }

                break;
            }
        }
    }

    private bool TryFindProxyPidByListenPort(ushort listenPort, out uint proxyPid)
    {
        proxyPid = 0;
        return TryFindProxyPidByListenPortForIpVersion(listenPort, AddressFamilyInet, out proxyPid)
            || TryFindProxyPidByListenPortForIpVersion(listenPort, AddressFamilyInet6, out proxyPid);
    }

    private unsafe bool TryFindProxyPidByListenPortForIpVersion(ushort listenPort, int ipVersion, out uint proxyPid)
    {
        proxyPid = 0;
        var requiredSize = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref requiredSize, false, ipVersion, TcpTableOwnerPidAll, 0);
        if (requiredSize <= 0)
            return false;

        if (tcpBufferSize < requiredSize)
        {
            if (tcpBuffer != IntPtr.Zero)
                NativeMemory.Free((void*)tcpBuffer);

            tcpBufferSize = requiredSize;
            tcpBuffer = (IntPtr)NativeMemory.Alloc((nuint)tcpBufferSize);
        }

        if (GetExtendedTcpTable(tcpBuffer, ref requiredSize, false, ipVersion, TcpTableOwnerPidAll, 0) != 0)
            return false;

        var buffer = (byte*)tcpBuffer;
        var entryCount = Unsafe.Read<int>(buffer);
        switch (ipVersion)
        {
            case AddressFamilyInet:
            {
                var rowPtr = (TcpRow*)(buffer + sizeof(int));
                for (var i = 0; i < entryCount; i++)
                {
                    ref readonly var row = ref rowPtr[i];
                    if (row.State != TcpStateListen)
                        continue;

                    var port = BinaryPrimitives.ReverseEndianness((ushort)row.LocalPort);
                    if (port != listenPort || (row.LocalAddress != 0 && row.LocalAddress != 0x0100007F))
                        continue;

                    proxyPid = row.OwningPid;
                    return true;
                }

                break;
            }
            case AddressFamilyInet6:
            {
                var rowPtr = (Tcp6Row*)(buffer + sizeof(int));
                for (var i = 0; i < entryCount; i++)
                {
                    ref readonly var row = ref rowPtr[i];
                    if (row.State != TcpStateListen)
                        continue;

                    var port = BinaryPrimitives.ReverseEndianness((ushort)row.LocalPort);
                    if (port != listenPort)
                        continue;

                    var isUnspecified = true;
                    var isLoopback = true;
                    for (var j = 0; j < 16; j++)
                    {
                        var b = row.LocalAddress[j];
                        isUnspecified &= b == 0;
                        isLoopback &= j == 15 ? b == 1 : b == 0;
                    }

                    if (!isUnspecified && !isLoopback)
                        continue;

                    proxyPid = row.OwningPid;
                    return true;
                }

                break;
            }
        }

        return false;
    }

    private static bool IsXivPort(ushort port)
    {
        return port is >= 54992 and <= 54994
            or >= 55006 and <= 55007
            or >= 55021 and <= 55040
            or >= 55296 and <= 55551;
    }

    private static bool IsFilteredPort(ushort port)
    {
        return port is 80 or 443;
    }

    private static int ScoreEndpoint(bool isLoopback, bool isPrivateOrLocal, ushort port, bool isXivPort)
    {
        var score = 0;
        if (isXivPort)
            score += 100;

        score += isLoopback ? -200 : 40;
        score += isPrivateOrLocal ? -30 : 20;
        return score + (port is 80 or 443 ? 10 : 0);
    }

    private static bool IsPrivateOrLocalIpv4(uint addressValue)
    {
        var first = (byte)addressValue;
        var second = (byte)(addressValue >> 8);

        return first switch
        {
            10 => true,
            100 when second is >= 64 and <= 127 => true,
            172 when second is >= 16 and <= 31 => true,
            192 when second == 168 => true,
            169 when second == 254 => true,
            0 => true,
            127 => true,
            >= 224 => true,
            _ => false,
        };
    }

    private static unsafe bool IsPrivateOrLocalIpv6(byte* address)
    {
        var span = new ReadOnlySpan<byte>(address, 16);
        if (span[0] == 0xFF)
            return true;

        if (span[0] == 0xFE && (span[1] & 0xC0) == 0x80)
            return true;

        if ((span[0] & 0xFE) == 0xFC)
            return true;

        var allZero = true;
        var isLoopback = true;
        for (var i = 0; i < 16; i++)
        {
            allZero &= span[i] == 0;
            isLoopback &= i == 15 ? span[i] == 1 : span[i] == 0;
        }

        return allZero || isLoopback;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int dwOutBufLen,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int ipVersion,
        int tableClass,
        uint reserved);

    private readonly record struct ConnectionEndpoint(IPAddress Address, ushort Port, int Score);

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Tcp6Row
    {
        public fixed byte LocalAddress[16];
        public uint LocalScopeId;
        public uint LocalPort;
        public fixed byte RemoteAddress[16];
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }
}
