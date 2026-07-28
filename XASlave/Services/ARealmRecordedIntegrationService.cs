using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

public unsafe sealed class ARealmRecordedIntegrationService : IDisposable
{
    private const string NormalizedPluginName = "arealmrecorded";
    private const string GameTypeName = "Game";
    private const string WhitelistFieldName = "whitelistedContentTypes";
    private const string ReplayModuleTypeName = "ContentsReplayModule";
    private const string ReplayModuleStatusFieldName = "status";
    private const string ReplayModulePlaybackControlsFieldName = "playbackControls";
    private const string ReplayModuleRecordingTimeFieldName = "recordingTime";
    private const string ReplayModuleEndRecordingFieldName = "endRecording";
    private const string ReplayModuleInitZonePacketFieldName = "initZonePacket";
    private const string InitZonePacketCfcFieldName = "contentFinderCondition";
    private const string InitializeRecordingDetourMethodName = "InitializeRecordingDetour";
    private const string GameFunctionInvokePropertyName = "Invoke";

    // Canonical state definitions mirrored from ARealmRecorded/Hypostasis:
    // IsRecording => (status & 0x74) == 0x74 (saving packets + record ready + save recording + barrier down),
    // InPlayback => (playbackControls & 4) != 0. ARR's own DTR recording icon uses IsRecording.
    // Field operations never run the normal duty-end path, so bits 0x4/0x20 can linger armed after
    // leaving a forced-recorded zone; that stale state is what the auto-cleanup ends via endRecording.
    private const byte ReplayStatusRecordingMask = 0x74;
    private const byte ReplayStatusArmedMask = 0x4 | 0x20;
    private const byte PlaybackControlsInPlaybackBit = 0x4;

    private static readonly TimeSpan AutoCleanDwell = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AutoCleanRetryInterval = TimeSpan.FromSeconds(10);
    private const int AutoCleanMaxAttempts = 3;

    private static readonly BindingFlags StaticBindings = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly BindingFlags InstanceBindings = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly uint[] FallbackContentTypeIds = Enumerable.Range(1, 64).Select(id => (uint)id).ToArray();

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Stopwatch refreshTimer = Stopwatch.StartNew();

    // Stock ARealmRecorded whitelist, used only as a fallback when the live list cannot be read.
    private static readonly uint[] KnownStockContentTypeIds = [1, 2, 3, 4, 5, 9, 28, 29, 30, 37, 39];

    private bool forceEnabled;
    private bool subscribed;
    private bool injectAllContentTypes = true;
    private HashSet<uint> selectedContentTypes = [];
    private uint[]? allContentTypeIds;
    private WeakReference<HashSet<uint>>? patchedWhitelist;
    private uint[] originalWhitelist = [];
    private HashSet<uint> originalWhitelistSet = [];
    private IReadOnlyList<SelectableContentType> cachedSelectableContentTypes = [];
    private readonly Stopwatch selectableTypesTimer = new();
    private HashSet<uint>? cfcReferencedContentTypeIds;

    private WeakReference<object>? statusReaderSourceInstance;
    private Func<nint>? getReplayModuleAddress;
    private Type? replayModuleType;
    private object? endRecordingGameFunction;
    private int replayModuleStatusOffset = -1;
    private int replayModulePlaybackControlsOffset = -1;
    private int replayModuleRecordingTimeOffset = -1;
    private int replayModuleInitZoneCfcOffset = -1;
    private bool statusReaderUnavailable;
    private bool statusReaderUnavailableLogged;

    private DateTime? staleArmedSinceUtc;
    private DateTime lastAutoCleanAttemptUtc = DateTime.MinValue;
    private int autoCleanAttempts;
    private bool autoCleanGiveUpLogged;

    private LiveState? cachedLiveState;
    private readonly Stopwatch liveStateTimer = Stopwatch.StartNew();

    public readonly record struct SelectableContentType(uint Id, string Name, bool IsStock);

    public sealed class LiveState
    {
        public bool PluginLoaded { get; init; }
        public bool StateAvailable { get; init; }
        public byte StatusByte { get; init; }
        public byte PlaybackControls { get; init; }
        public bool IsRecording { get; init; }
        public bool IsArmed { get; init; }
        public bool InPlayback { get; init; }
        public float RecordingTimeSeconds { get; init; } = -1f;
        public string Description { get; init; } = string.Empty;
        public string StatusBitsText { get; init; } = string.Empty;
    }

    public ARealmRecordedIntegrationService(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.clientState = clientState;
        this.condition = condition;
        this.dataManager = dataManager;
        this.log = log;
    }

    public bool IsForceEnabled => forceEnabled;

    public string StatusText { get; private set; } = "Disabled";

    public string LastForceActionMessage { get; private set; } = string.Empty;

    public void ApplyConfiguration(bool allContentTypes, IEnumerable<uint>? selected)
    {
        injectAllContentTypes = allContentTypes;
        selectedContentTypes = selected == null ? [] : [.. selected];
        if (forceEnabled)
            RefreshNow();
    }

    public IReadOnlyList<SelectableContentType> GetSelectableContentTypes()
    {
        if (cachedSelectableContentTypes.Count > 0 && selectableTypesTimer.IsRunning && selectableTypesTimer.Elapsed < TimeSpan.FromSeconds(2))
            return cachedSelectableContentTypes;

        selectableTypesTimer.Restart();
        var stock = GetStockContentTypeIds();
        var recordable = GetCfcReferencedContentTypeIds();
        var entries = new List<SelectableContentType>();
        try
        {
            foreach (var row in dataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentType>())
            {
                if (row.RowId == 0)
                    continue;

                // Only list content types an actual duty (ContentFinderCondition row) references;
                // categories like FATEs, Levequests, or Retainer Ventures can never reach the recorder.
                if (recordable.Count > 0 && !recordable.Contains(row.RowId) && !stock.Contains(row.RowId))
                    continue;

                var name = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name))
                    name = $"Type {row.RowId}";

                entries.Add(new SelectableContentType(row.RowId, name, stock.Contains(row.RowId)));
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] ARealmRecorded All Zones could not read ContentType names for the selection list.");
        }

        if (entries.Count == 0)
            entries.AddRange(GetAllContentTypeIds().Select(id => new SelectableContentType(id, $"Type {id}", stock.Contains(id))));

        cachedSelectableContentTypes = entries;
        return cachedSelectableContentTypes;
    }

    private HashSet<uint> GetCfcReferencedContentTypeIds()
    {
        if (cfcReferencedContentTypeIds is { Count: > 0 })
            return cfcReferencedContentTypeIds;

        var set = new HashSet<uint>();
        try
        {
            foreach (var row in dataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>())
            {
                if (row.RowId == 0)
                    continue;

                var contentTypeId = row.ContentType.RowId;
                if (contentTypeId != 0)
                    set.Add(contentTypeId);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] ARealmRecorded All Zones could not scan ContentFinderCondition for referenced content types.");
        }

        cfcReferencedContentTypeIds = set;
        return set;
    }

    private HashSet<uint> GetStockContentTypeIds()
    {
        if (originalWhitelist.Length > 0)
            return originalWhitelistSet;

        if (!forceEnabled && TryGetWhitelist(out var live, out _) && live != null)
            return [.. live];

        return [.. KnownStockContentTypeIds];
    }

    public bool SetForceEnabled(bool value)
    {
        forceEnabled = value;
        UpdateSubscriptions();
        if (!forceEnabled)
            RestorePatchedWhitelist();

        RefreshNow();
        return forceEnabled;
    }

    public void Dispose()
    {
        forceEnabled = false;
        UpdateSubscriptions();
        RestorePatchedWhitelist();
        StatusText = "Disabled";
    }

    public LiveState GetLiveState()
    {
        if (cachedLiveState != null && liveStateTimer.Elapsed < TimeSpan.FromMilliseconds(250))
            return cachedLiveState;

        liveStateTimer.Restart();
        cachedLiveState = BuildLiveState();
        return cachedLiveState;
    }

    public void RequestForceStopRecording()
    {
        framework.RunOnFrameworkThread(() =>
        {
            TryEndRecordingNow(force: true, out var message);
            LastForceActionMessage = $"{DateTime.Now:HH:mm:ss} - {message}";
            log.Information($"[XASlave] ARealmRecorded All Zones force stop: {message}");
            cachedLiveState = null;
        });
    }

    public void RequestForceStartRecording()
    {
        framework.RunOnFrameworkThread(() =>
        {
            TryForceStartRecordingNow(out var message);
            LastForceActionMessage = $"{DateTime.Now:HH:mm:ss} - {message}";
            log.Information($"[XASlave] ARealmRecorded All Zones force start: {message}");
            cachedLiveState = null;
        });
    }

    private bool TryForceStartRecordingNow(out string message)
    {
        if (!TryGetPluginInstance(out var pluginInstance) || pluginInstance == null)
        {
            message = "ARealmRecorded is not loaded.";
            return false;
        }

        EnsureStatusReader(pluginInstance);
        if (statusReaderUnavailable || getReplayModuleAddress == null || replayModuleType == null)
        {
            message = "The Duty Recorder module could not be resolved.";
            return false;
        }

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            message = "Cannot force start while zoning; wait until the zone has fully loaded.";
            return false;
        }

        try
        {
            var address = getReplayModuleAddress();
            if (address == nint.Zero)
            {
                message = "The Duty Recorder module pointer is null.";
                return false;
            }

            var before = *(byte*)(address + replayModuleStatusOffset);
            var playback = replayModulePlaybackControlsOffset >= 0 ? *(byte*)(address + replayModulePlaybackControlsOffset) : (byte)0;
            if ((before & ReplayStatusRecordingMask) == ReplayStatusRecordingMask)
            {
                message = $"Already recording (status 0x{before:X2}).";
                return false;
            }

            if ((playback & PlaybackControlsInPlaybackBit) != 0)
            {
                message = "Cannot force start during replay playback.";
                return false;
            }

            if (replayModuleInitZoneCfcOffset < 0)
            {
                message = "The InitZone ContentFinderCondition offset could not be resolved.";
                return false;
            }

            TryGetWhitelist(out var whitelist, out _);

            var spoofNote = string.Empty;
            var contentFinderCondition = *(ushort*)(address + replayModuleInitZoneCfcOffset);
            if (contentFinderCondition == 0)
            {
                var spoof = FindSpoofCfc(whitelist);
                if (spoof == null)
                {
                    message = "This zone has no ContentFinderCondition and no whitelisted duty could be found to spoof; cannot force start.";
                    return false;
                }

                // The replayed zone comes from the InitZone packet's territory field, which stays real;
                // the CFC only drives the replay header/labeling, so a spoof lets the recorder arm here.
                *(ushort*)(address + replayModuleInitZoneCfcOffset) = spoof.Value.Id;
                contentFinderCondition = spoof.Value.Id;
                spoofNote = spoof.Value.TerritoryMatched
                    ? $" This zone had no CFC; spoofed {spoof.Value.Id} ('{spoof.Value.Name}'), which matches this territory."
                    : $" This zone had no CFC and no duty exists for this territory; spoofed {spoof.Value.Id} ('{spoof.Value.Name}') - the replay will show a FALSE location and may be unplayable. Experimental.";
            }

            var contentTypeId = GetContentTypeIdForCfc(contentFinderCondition);
            if (contentTypeId != 0 && whitelist != null && !whitelist.Contains(contentTypeId))
            {
                message = $"Content type {contentTypeId} is not whitelisted right now - enable the mod and tick it (or Record all content types) first.";
                return false;
            }

            var gameType = FindGameType(pluginInstance.GetType().Assembly);
            var detour = gameType?.GetMethod(InitializeRecordingDetourMethodName, StaticBindings);
            if (detour == null)
            {
                message = "ARealmRecorded's InitializeRecordingDetour could not be resolved (unsupported plugin version).";
                return false;
            }

            detour.Invoke(null, [Pointer.Box((void*)address, replayModuleType.MakePointerType())]);

            var after = *(byte*)(address + replayModuleStatusOffset);
            if ((after & ReplayStatusArmedMask) != 0 && (after & 0x40) == 0)
            {
                // Mid-instance the duty barrier is already down, but the native barrier-drop event has
                // long passed, so complete the recording mask the same way ARR's director-sync hack does.
                *(byte*)(address + replayModuleStatusOffset) = (byte)(after | 0x40);
                after = *(byte*)(address + replayModuleStatusOffset);
            }

            var recording = (after & ReplayStatusRecordingMask) == ReplayStatusRecordingMask;
            message = recording
                ? $"Recording force-started for CFC {contentFinderCondition} (status 0x{before:X2} -> 0x{after:X2}).{spoofNote} The replay starts from this moment; earlier events are not included. Force-started recordings can carry a false location and may fail to play back."
                : $"InitializeRecording invoked (status 0x{before:X2} -> 0x{after:X2}) but recording did not arm - ARealmRecorded declined it (check whitelist/CFC).{spoofNote}";
            return recording;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] ARealmRecorded All Zones failed to force start a recording.");
            message = $"Force start failed: {ex.InnerException?.Message ?? ex.Message}";
            return false;
        }
    }

    private (ushort Id, string Name, bool TerritoryMatched)? FindSpoofCfc(HashSet<uint>? whitelist)
    {
        try
        {
            var territory = clientState.TerritoryType;
            (ushort Id, string Name, bool TerritoryMatched)? fallback = null;
            foreach (var row in dataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>())
            {
                if (row.RowId == 0 || row.RowId > ushort.MaxValue)
                    continue;

                var contentTypeId = row.ContentType.RowId;
                if (contentTypeId == 0)
                    continue;

                if (whitelist != null && !whitelist.Contains(contentTypeId))
                    continue;

                var name = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (territory != 0 && row.TerritoryType.RowId == territory)
                    return ((ushort)row.RowId, name, true);

                fallback ??= ((ushort)row.RowId, name, false);
            }

            return fallback;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] ARealmRecorded All Zones failed while searching for a spoofable ContentFinderCondition.");
            return null;
        }
    }

    private uint GetContentTypeIdForCfc(ushort contentFinderCondition)
    {
        try
        {
            var row = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>().GetRowOrDefault(contentFinderCondition);
            return row?.ContentType.RowId ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private void UpdateSubscriptions()
    {
        if (forceEnabled == subscribed)
            return;

        if (forceEnabled)
        {
            framework.Update += OnFrameworkUpdate;
            refreshTimer.Restart();
        }
        else
        {
            framework.Update -= OnFrameworkUpdate;
        }

        subscribed = forceEnabled;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (refreshTimer.Elapsed < TimeSpan.FromMilliseconds(250))
            return;

        refreshTimer.Restart();
        RefreshNow();
    }

    private void RefreshNow()
    {
        if (!forceEnabled)
        {
            StatusText = "Disabled";
            return;
        }

        if (!TryGetWhitelist(out var whitelist, out var pluginInstance) || whitelist == null || pluginInstance == null)
        {
            StatusText = "Enabled - waiting for ARealmRecorded to load.";
            return;
        }

        if (patchedWhitelist == null || !patchedWhitelist.TryGetTarget(out var tracked) || !ReferenceEquals(tracked, whitelist))
        {
            originalWhitelist = whitelist.ToArray();
            originalWhitelistSet = [.. originalWhitelist];
            patchedWhitelist = new WeakReference<HashSet<uint>>(whitelist);
        }

        var added = 0;
        var removed = 0;
        foreach (var id in GetAllContentTypeIds())
        {
            var wanted = injectAllContentTypes || selectedContentTypes.Contains(id) || originalWhitelistSet.Contains(id);
            if (wanted)
            {
                if (whitelist.Add(id))
                    added++;
            }
            else if (!originalWhitelistSet.Contains(id) && whitelist.Remove(id))
            {
                removed++;
            }
        }

        if (added > 0 || removed > 0)
        {
            var scope = injectAllContentTypes ? "all content types" : $"{selectedContentTypes.Count} selected content type(s)";
            log.Information($"[XASlave] ARealmRecorded All Zones reconciled the recording whitelist for {scope}: +{added}/-{removed} ({whitelist.Count} total).");
        }

        EnsureStatusReader(pluginInstance);
        if (statusReaderUnavailable && !statusReaderUnavailableLogged)
        {
            statusReaderUnavailableLogged = true;
            log.Warning("[XASlave] ARealmRecorded All Zones could not resolve the Duty Recorder module; state readout and stale-recorder auto-cleanup are disabled.");
        }

        if (TryReadModuleBytes(out var statusByte, out var playbackControls, out _))
        {
            var isRecording = (statusByte & ReplayStatusRecordingMask) == ReplayStatusRecordingMask;
            var inPlayback = (playbackControls & PlaybackControlsInPlaybackBit) != 0;
            MaybeAutoCleanStaleRecorder(statusByte, isRecording, inPlayback);
        }

        StatusText = injectAllContentTypes
            ? $"Enabled - ARealmRecorded records every content type ({whitelist.Count} whitelisted, {originalWhitelist.Length} stock)."
            : $"Enabled - ARealmRecorded records {selectedContentTypes.Count} selected content type(s) plus its stock whitelist ({whitelist.Count} whitelisted, {originalWhitelist.Length} stock).";
    }

    private void MaybeAutoCleanStaleRecorder(byte statusByte, bool isRecording, bool inPlayback)
    {
        var staleArmed = !isRecording
            && !inPlayback
            && (statusByte & ReplayStatusArmedMask) != 0
            && IsSafeToAutoClean();

        if (!staleArmed)
        {
            staleArmedSinceUtc = null;
            autoCleanAttempts = 0;
            autoCleanGiveUpLogged = false;
            return;
        }

        staleArmedSinceUtc ??= DateTime.UtcNow;
        if (DateTime.UtcNow - staleArmedSinceUtc < AutoCleanDwell)
            return;

        if (autoCleanAttempts >= AutoCleanMaxAttempts)
        {
            if (!autoCleanGiveUpLogged)
            {
                autoCleanGiveUpLogged = true;
                log.Warning($"[XASlave] ARealmRecorded All Zones auto-cleanup gave up after {AutoCleanMaxAttempts} attempts; the Duty Recorder stays armed (status 0x{statusByte:X2}). Use the Force Stop Recording button or relog to clear it.");
            }

            return;
        }

        if (DateTime.UtcNow - lastAutoCleanAttemptUtc < AutoCleanRetryInterval)
            return;

        lastAutoCleanAttemptUtc = DateTime.UtcNow;
        autoCleanAttempts++;
        TryEndRecordingNow(force: false, out var message);
        log.Information($"[XASlave] ARealmRecorded All Zones auto-cleanup (attempt {autoCleanAttempts}/{AutoCleanMaxAttempts}): {message}");
        cachedLiveState = null;
    }

    private bool IsSafeToAutoClean()
    {
        if (!clientState.IsLoggedIn)
            return false;

        if (condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] || condition[ConditionFlag.BoundByDuty95])
            return false;

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return false;

        return true;
    }

    private bool TryEndRecordingNow(bool force, out string message)
    {
        if (!TryGetPluginInstance(out var pluginInstance) || pluginInstance == null)
        {
            message = "ARealmRecorded is not loaded.";
            return false;
        }

        EnsureStatusReader(pluginInstance);
        if (statusReaderUnavailable || getReplayModuleAddress == null || replayModuleType == null || endRecordingGameFunction == null)
        {
            message = "The Duty Recorder endRecording function could not be resolved.";
            return false;
        }

        try
        {
            var address = getReplayModuleAddress();
            if (address == nint.Zero)
            {
                message = "The Duty Recorder module pointer is null.";
                return false;
            }

            var before = *(byte*)(address + replayModuleStatusOffset);
            if (!force && (before & (ReplayStatusRecordingMask | ReplayStatusArmedMask)) == 0)
            {
                message = $"Recorder already idle (status 0x{before:X2}); nothing to stop.";
                return false;
            }

            var invokeDelegate = endRecordingGameFunction.GetType()
                .GetProperty(GameFunctionInvokePropertyName, InstanceBindings)
                ?.GetValue(endRecordingGameFunction) as Delegate;
            if (invokeDelegate == null)
            {
                message = "The endRecording game function is not available (signature unresolved).";
                return false;
            }

            invokeDelegate.DynamicInvoke(Pointer.Box((void*)address, replayModuleType.MakePointerType()));
            var after = *(byte*)(address + replayModuleStatusOffset);
            message = $"EndRecording invoked (status 0x{before:X2} -> 0x{after:X2}).";
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] ARealmRecorded All Zones failed to invoke endRecording.");
            message = $"EndRecording failed: {ex.InnerException?.Message ?? ex.Message}";
            return false;
        }
    }

    private LiveState BuildLiveState()
    {
        if (!TryGetPluginInstance(out var pluginInstance) || pluginInstance == null)
            return new LiveState { Description = "ARealmRecorded is not loaded." };

        EnsureStatusReader(pluginInstance);
        if (!TryReadModuleBytes(out var statusByte, out var playbackControls, out var recordingTime))
            return new LiveState { PluginLoaded = true, Description = "Loaded - Duty Recorder state unavailable." };

        var isRecording = (statusByte & ReplayStatusRecordingMask) == ReplayStatusRecordingMask;
        var inPlayback = (playbackControls & PlaybackControlsInPlaybackBit) != 0;
        var isArmed = !isRecording && (statusByte & ReplayStatusArmedMask) != 0;
        var description = isRecording
            ? $"RECORDING - {FormatRecordingTime(recordingTime)} elapsed."
            : inPlayback
                ? "Playing back a replay."
                : isArmed
                    ? "Armed - recorder initialized but not actively recording (stale after leaving a zone)."
                    : "Idle - not recording.";

        return new LiveState
        {
            PluginLoaded = true,
            StateAvailable = true,
            StatusByte = statusByte,
            PlaybackControls = playbackControls,
            IsRecording = isRecording,
            IsArmed = isArmed,
            InPlayback = inPlayback,
            RecordingTimeSeconds = recordingTime,
            Description = description,
            StatusBitsText = DescribeStatusBits(statusByte),
        };
    }

    private static string DescribeStatusBits(byte statusByte)
    {
        if (statusByte == 0)
            return "none";

        var names = new List<string>(8);
        if ((statusByte & 0x01) != 0) names.Add("LoggedIn");
        if ((statusByte & 0x02) != 0) names.Add("CanRecord");
        if ((statusByte & 0x04) != 0) names.Add("SavingPackets");
        if ((statusByte & 0x08) != 0) names.Add("Unknown8");
        if ((statusByte & 0x10) != 0) names.Add("RecordReady");
        if ((statusByte & 0x20) != 0) names.Add("SaveRecording");
        if ((statusByte & 0x40) != 0) names.Add("BarrierDown");
        if ((statusByte & 0x80) != 0) names.Add("PlaybackBarrier");
        return string.Join(", ", names);
    }

    private static string FormatRecordingTime(float seconds)
    {
        if (seconds < 0 || float.IsNaN(seconds) || float.IsInfinity(seconds))
            return "??:??";

        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}"
            : $"{time.Minutes:D2}:{time.Seconds:D2}";
    }

    private void EnsureStatusReader(object pluginInstance)
    {
        if (statusReaderSourceInstance != null
            && statusReaderSourceInstance.TryGetTarget(out var source)
            && ReferenceEquals(source, pluginInstance))
        {
            return;
        }

        statusReaderSourceInstance = new WeakReference<object>(pluginInstance);
        statusReaderUnavailable = !TryResolveStatusReader(pluginInstance);
    }

    private bool TryReadModuleBytes(out byte statusByte, out byte playbackControls, out float recordingTime)
    {
        statusByte = 0;
        playbackControls = 0;
        recordingTime = -1f;

        if (statusReaderUnavailable || getReplayModuleAddress == null || replayModuleStatusOffset < 0)
            return false;

        try
        {
            var address = getReplayModuleAddress();
            if (address == nint.Zero)
                return false;

            statusByte = *(byte*)(address + replayModuleStatusOffset);
            if (replayModulePlaybackControlsOffset >= 0)
                playbackControls = *(byte*)(address + replayModulePlaybackControlsOffset);
            if (replayModuleRecordingTimeOffset >= 0)
                recordingTime = *(float*)(address + replayModuleRecordingTimeOffset);

            return true;
        }
        catch (Exception ex)
        {
            statusReaderUnavailable = true;
            log.Warning(ex, "[XASlave] ARealmRecorded All Zones failed to read the Duty Recorder module; disabling the state reader.");
            return false;
        }
    }

    private void RestorePatchedWhitelist()
    {
        staleArmedSinceUtc = null;
        autoCleanAttempts = 0;
        autoCleanGiveUpLogged = false;

        if (patchedWhitelist == null || !patchedWhitelist.TryGetTarget(out var whitelist))
        {
            patchedWhitelist = null;
            return;
        }

        try
        {
            whitelist.Clear();
            whitelist.UnionWith(originalWhitelist);
            log.Information($"[XASlave] ARealmRecorded All Zones restored the stock recording whitelist ({whitelist.Count} entries).");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] ARealmRecorded All Zones failed to restore the stock whitelist.");
        }

        patchedWhitelist = null;
        originalWhitelist = [];
        originalWhitelistSet = [];
    }

    private uint[] GetAllContentTypeIds()
    {
        if (allContentTypeIds is { Length: > 0 })
            return allContentTypeIds;

        try
        {
            allContentTypeIds = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentType>()
                .Where(row => row.RowId != 0)
                .Select(row => row.RowId)
                .ToArray();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] ARealmRecorded All Zones could not read the ContentType sheet; using a fixed 1-64 range.");
            allContentTypeIds = [];
        }

        if (allContentTypeIds.Length == 0)
            allContentTypeIds = FallbackContentTypeIds;

        return allContentTypeIds;
    }

    private bool TryResolveStatusReader(object pluginInstance)
    {
        getReplayModuleAddress = null;
        replayModuleType = null;
        endRecordingGameFunction = null;
        replayModuleStatusOffset = -1;
        replayModulePlaybackControlsOffset = -1;
        replayModuleRecordingTimeOffset = -1;
        replayModuleInitZoneCfcOffset = -1;
        statusReaderUnavailableLogged = false;

        try
        {
            var pluginAssembly = pluginInstance.GetType().Assembly;
            var context = AssemblyLoadContext.GetLoadContext(pluginAssembly);
            var assemblies = context == null ? [pluginAssembly] : context.Assemblies.ToArray();

            Type? moduleType = null;
            foreach (var assembly in assemblies)
            {
                moduleType = GetLoadableTypes(assembly).FirstOrDefault(type => type is { IsValueType: true, Name: ReplayModuleTypeName });
                if (moduleType != null)
                    break;
            }

            var statusField = moduleType?.GetField(ReplayModuleStatusFieldName, InstanceBindings);
            if (moduleType == null || statusField == null)
                return false;

            var offsetAttribute = statusField.GetCustomAttribute<FieldOffsetAttribute>();
            if (offsetAttribute == null)
                return false;

            replayModuleType = moduleType;
            replayModuleStatusOffset = offsetAttribute.Value;
            replayModulePlaybackControlsOffset = GetFieldOffset(moduleType, ReplayModulePlaybackControlsFieldName);
            replayModuleRecordingTimeOffset = GetFieldOffset(moduleType, ReplayModuleRecordingTimeFieldName);
            endRecordingGameFunction = moduleType.GetField(ReplayModuleEndRecordingFieldName, StaticBindings)?.GetValue(null);

            var initZoneField = moduleType.GetField(ReplayModuleInitZonePacketFieldName, InstanceBindings);
            var initZoneOffset = initZoneField?.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? -1;
            var cfcSubOffset = initZoneField?.FieldType.GetField(InitZonePacketCfcFieldName, InstanceBindings)?.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? -1;
            replayModuleInitZoneCfcOffset = initZoneOffset >= 0 && cfcSubOffset >= 0 ? initZoneOffset + cfcSubOffset : -1;

            foreach (var assembly in assemblies)
            {
                foreach (var type in GetLoadableTypes(assembly))
                {
                    var property = type.GetProperties(StaticBindings).FirstOrDefault(p => p.Name == ReplayModuleTypeName && p.PropertyType.IsPointer);
                    if (property != null)
                    {
                        getReplayModuleAddress = () => UnboxPointer(property.GetValue(null));
                        return true;
                    }

                    var field = type.GetFields(StaticBindings).FirstOrDefault(f => f.Name == ReplayModuleTypeName && f.FieldType.IsPointer);
                    if (field != null)
                    {
                        getReplayModuleAddress = () => UnboxPointer(field.GetValue(null));
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] ARealmRecorded All Zones failed while resolving the Duty Recorder status reader.");
            return false;
        }
    }

    private static int GetFieldOffset(Type moduleType, string fieldName)
    {
        return moduleType.GetField(fieldName, InstanceBindings)?.GetCustomAttribute<FieldOffsetAttribute>()?.Value ?? -1;
    }

    private static nint UnboxPointer(object? value)
    {
        return value switch
        {
            Pointer pointer => (nint)Pointer.Unbox(pointer),
            nint address => address,
            _ => nint.Zero,
        };
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null)!;
        }
        catch
        {
            return [];
        }
    }

    private bool TryGetWhitelist(out HashSet<uint>? whitelist, out object? pluginInstance)
    {
        whitelist = null;

        if (!TryGetPluginInstance(out pluginInstance) || pluginInstance == null)
            return false;

        try
        {
            var gameType = FindGameType(pluginInstance.GetType().Assembly);
            var field = gameType?.GetField(WhitelistFieldName, StaticBindings);
            whitelist = field?.GetValue(null) as HashSet<uint>;
            return whitelist != null;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] ARealmRecorded All Zones could not resolve the recording whitelist field.");
            return false;
        }
    }

    private static Type? FindGameType(Assembly assembly)
    {
        return assembly.GetType($"ARealmRecorded.{GameTypeName}")
            ?? GetLoadableTypes(assembly).FirstOrDefault(type => type.Name == GameTypeName && type.GetField(WhitelistFieldName, StaticBindings) != null);
    }

    private bool TryGetPluginInstance(out object? pluginInstance)
    {
        pluginInstance = TryGetPluginInstanceFromCollection(pluginInterface.InstalledPlugins);
        if (pluginInstance != null)
            return true;

        pluginInstance = TryGetPluginInstanceFromInternalManager();
        return pluginInstance != null;
    }

    private object? TryGetPluginInstanceFromInternalManager()
    {
        try
        {
            var pluginManagerServiceType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Service`1");
            var pluginManagerType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Plugin.Internal.PluginManager");
            if (pluginManagerServiceType == null || pluginManagerType == null)
                return null;

            var pluginManager = pluginManagerServiceType
                .MakeGenericType(pluginManagerType)
                .GetMethod("Get")
                ?.Invoke(null, null);
            if (pluginManager == null)
                return null;

            var installedPlugins = pluginManager.GetType().GetProperty("InstalledPlugins", InstanceBindings)?.GetValue(pluginManager) as IEnumerable;
            return installedPlugins == null ? null : TryGetPluginInstanceFromCollection(installedPlugins);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] ARealmRecorded All Zones could not inspect Dalamud's internal plugin manager.");
            return null;
        }
    }

    private static object? TryGetPluginInstanceFromCollection(IEnumerable installedPlugins)
    {
        foreach (var pluginState in installedPlugins)
        {
            if (pluginState == null || !IsLoaded(pluginState) || !IsARealmRecorded(pluginState))
                continue;

            var pluginType = pluginState.GetType().Name == "LocalDevPlugin"
                ? pluginState.GetType().BaseType
                : pluginState.GetType();
            if (pluginType == null)
                continue;

            var instanceField = pluginType.GetField("instance", BindingFlags.Instance | BindingFlags.NonPublic);
            var instance = instanceField?.GetValue(pluginState);
            if (instance != null)
                return instance;
        }

        return null;
    }

    private static bool IsLoaded(object pluginState)
    {
        return GetBooleanProperty(pluginState, "IsLoaded");
    }

    private static bool IsARealmRecorded(object pluginState)
    {
        return IsMatchingPluginName(GetStringProperty(pluginState, "InternalName"))
               || IsMatchingPluginName(GetStringProperty(pluginState, "Name"));
    }

    private static bool IsMatchingPluginName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Replace(" ", string.Empty).Equals(NormalizedPluginName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetStringProperty(object instance, string propertyName)
    {
        return instance.GetType().GetProperty(propertyName, InstanceBindings)?.GetValue(instance)?.ToString();
    }

    private static bool GetBooleanProperty(object instance, string propertyName)
    {
        return instance.GetType().GetProperty(propertyName, InstanceBindings)?.GetValue(instance) switch
        {
            bool value => value,
            _ => false,
        };
    }
}
