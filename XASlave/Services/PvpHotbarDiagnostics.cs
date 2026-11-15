using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

/// <summary>Bounded, read-only command traces. Native visibility is not proof of rendered visibility.</summary>
internal sealed unsafe class PvpHotbarDiagnostics
{
    private static readonly string[] Names = ["_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04", "_ActionBar05", "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09"];
    private static readonly long[] SampleTimesMs = [100, 500, 2000];
    private static readonly uint[] SavedNameHashes = MakeSavedNameHashes();
    private readonly object sync = new();
    private readonly Trace?[] traces = new Trace?[10];
    private long nextTraceId;
    private bool drawListening;
    private bool faultReported;

    private sealed class Trace
    {
        internal long Id;
        internal long StartedAt;
        internal nint AddonIdentity;
        internal bool RequestedVisibility;
        internal int NextSample;
        internal int DrawCount;
        internal bool Confirmed;
        internal int DrawCountAtConfirmation;
        internal bool NativeStateChangedAfterConfirmation;
    }

    internal void Request(int number, bool? intent, string origin)
    {
        Write(number, "request", $"origin={origin}; intent={(intent.HasValue ? intent.Value ? "show" : "hide" : "toggle")}");
    }

    internal void Begin(int number, bool visible, AtkUnitBase* addon)
    {
        try
        {
            if (!ValidNumber(number) || !Plugin.Framework.IsInFrameworkUpdateThread)
                return;
            lock (sync)
            {
                var index = number - 1;
                if (traces[index] != null)
                    Write(number, "trace-ended", "reason=superseded by another native request");
                traces[index] = new Trace
                {
                    Id = ++nextTraceId,
                    StartedAt = Environment.TickCount64,
                    AddonIdentity = (nint)addon,
                    RequestedVisibility = visible,
                };
                StartDrawListener();
                Write(number, "before-native", $"intent={(visible ? "show" : "hide")}; observationWindowMs=2000");
            }
        }
        catch (Exception ex) { ReportFailure("begin", ex); }
    }

    internal void Event(int number, string phase, string detail)
    {
        try
        {
            if (phase == "confirmed" && ValidNumber(number) && Plugin.Framework.IsInFrameworkUpdateThread)
            {
                lock (sync)
                {
                    var trace = traces[number - 1];
                    if (trace != null)
                    {
                        trace.Confirmed = true;
                        trace.DrawCountAtConfirmation = trace.DrawCount;
                    }
                }
            }
        }
        catch (Exception ex) { ReportFailure("confirmation tracking", ex); }
        Write(number, phase, detail);
    }

    internal void Observe()
    {
        try
        {
            if (!Plugin.Framework.IsInFrameworkUpdateThread)
                return;
            lock (sync)
            {
                var now = Environment.TickCount64;
                for (var index = 0; index < traces.Length; index++)
                {
                    var trace = traces[index];
                    if (trace == null || now - trace.StartedAt < SampleTimesMs[trace.NextSample])
                        continue;
                    // A long frame produces one current snapshot, never several pretend historical samples.
                    while (trace.NextSample < SampleTimesMs.Length && now - trace.StartedAt >= SampleTimesMs[trace.NextSample])
                        trace.NextSample++;
                    var finished = trace.NextSample == SampleTimesMs.Length;
                    Write(index + 1, finished ? "trace-ended" : "sample", finished ? "reason=observation window elapsed; screen visibility remains unverified" : "bounded follow-up");
                    if (finished)
                        traces[index] = null;
                }
                StopDrawListenerIfIdle();
            }
        }
        catch (Exception ex) { ReportFailure("observe", ex); }
    }

    internal void OnPostDraw(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (type != AddonEvent.PostDraw || !Plugin.Framework.IsInFrameworkUpdateThread)
                return;
            lock (sync)
            {
                var index = Array.IndexOf(Names, args.AddonName);
                if (index < 0)
                    return;
                var trace = traces[index];
                // Never dereference a remembered identity, and never format or scan nodes in Draw.
                if (trace != null && trace.AddonIdentity == args.Addon.Address && trace.DrawCount < int.MaxValue)
                    trace.DrawCount++;
            }
        }
        catch (Exception ex) { ReportFailure("post-draw", ex); }
    }

    internal void Stop(string reason)
    {
        try
        {
            lock (sync)
            {
                for (var index = 0; index < traces.Length; index++)
                {
                    if (traces[index] == null)
                        continue;
                    // Stop can run on unload or a foreign thread; do not touch game state here.
                    WriteWithoutSnapshot(index + 1, "trace-ended", $"reason={reason}");
                    traces[index] = null;
                }
                StopDrawListenerIfIdle();
            }
        }
        catch (Exception ex) { ReportFailure("stop", ex); }
    }

    internal void AddonFinalizing(string name)
    {
        try
        {
            lock (sync)
            {
                var index = Array.IndexOf(Names, name);
                if (index < 0 || traces[index] == null)
                    return;
                WriteWithoutSnapshot(index + 1, "trace-ended", "reason=addon finalizing");
                traces[index] = null;
                StopDrawListenerIfIdle();
            }
        }
        catch (Exception ex) { ReportFailure("finalize", ex); }
    }

    private void Write(int number, string phase, string detail)
    {
        try
        {
            // This must precede ClientState, Condition, GameGui, config and native access.
            if (!Plugin.Framework.IsInFrameworkUpdateThread)
            {
                Plugin.Log.Information($"[XASlave] [PvP Hotbars] bar={number}; phase={phase}; {detail}; snapshot=skipped off framework thread");
                return;
            }
            lock (sync)
            {
                var snapshot = ValidNumber(number) ? Capture(number - 1) : "snapshot=invalid bar number";
                Plugin.Log.Information($"[XASlave] [PvP Hotbars] bar={number}; phase={phase}; {TraceSummary(number)}; {detail}; {snapshot}");
            }
        }
        catch (Exception ex) { ReportFailure(phase, ex); }
    }

    private void WriteWithoutSnapshot(int number, string phase, string detail)
    {
        try { Plugin.Log.Information($"[XASlave] [PvP Hotbars] bar={number}; phase={phase}; {TraceSummary(number)}; {detail}; snapshot=not read during cancellation"); }
        catch (Exception ex) { ReportFailure(phase, ex); }
    }

    private string TraceSummary(int number)
    {
        var trace = ValidNumber(number) ? traces[number - 1] : null;
        return trace == null ? "trace=none" : $"trace={trace.Id}; elapsedMs={Environment.TickCount64 - trace.StartedAt}; intent={(trace.RequestedVisibility ? "show" : "hide")}; expectedAddon=0x{trace.AddonIdentity:X}; confirmed={trace.Confirmed}; postDrawCount={trace.DrawCount}; drawsAfterConfirmation={(trace.Confirmed ? (trace.DrawCount - trace.DrawCountAtConfirmation).ToString() : "unavailable")}; nativeStateChangedAfterConfirmation={trace.NativeStateChangedAfterConfirmation}; drawListener={drawListening}";
    }

    private string Capture(int index)
    {
        var loggedIn = Plugin.ClientState.IsLoggedIn;
        var pvp = Plugin.ClientState.IsPvP;
        var loading = Plugin.Condition[ConditionFlag.BetweenAreas];
        var loading51 = Plugin.Condition[ConditionFlag.BetweenAreas51];
        var hudHidden = Plugin.GameGui.GameUiHidden;
        var context = $"login={loggedIn}; pvp={pvp}; betweenAreas={loading}; betweenAreas51={loading51}; hudHidden={hudHidden}";
        if (!loggedIn || loading || loading51)
            return $"{context}; native=skipped while logged out or loading";

        var hotbars = RaptureHotbarModule.Instance();
        context += $"; pvpBank={(hotbars == null ? "unavailable" : hotbars->PvPHotbarsActive.ToString())}";
        var addon = (AtkUnitBase*)Plugin.GameGui.GetAddonByName(Names[index]).Address;
        var config = CaptureConfig(index);
        if (addon == null)
            return $"{context}; addonName={Names[index]}; addon=missing; {Assessment(index, addon, null)}; {config}";

        var displaySuppressed = (addon->VisibilityFlags & 1) != 0;
        var drawSuppressionMask = addon->VisibilityFlags & 0xFFF;
        var header = $"{context}; addonName={Names[index]}; addon=0x{(nint)addon:X}; addonId={addon->Id}; ready={addon->IsReady}; requested=0x{(byte)addon->VisibilityState:X2}; applied=0x{(byte)addon->AppliedVisibilityState:X2}; showHideFlags=0x{addon->ShowHideFlags:X2}; visibilityFlags=0x{addon->VisibilityFlags:X4}; displaySuppressed={displaySuppressed}; drawSuppressionMask=0x{drawSuppressionMask:X3}";
        if (!addon->IsReady)
            return $"{header}; nodes=skipped while addon not ready; {Assessment(index, addon, null)}; {config}";
        var root = addon->RootNode;
        var rootState = root == null ? "root=missing" : $"root=0x{(nint)root:X}; rootVisibleBit={(root->NodeFlags & NodeFlags.Visible) != 0}; rootIsVisible={root->IsVisible()}; rootAlpha={root->Color.A}; rootDrawFlags=0x{root->DrawFlags:X8}";
        var trace = traces[index];
        bool? nativeMatch = null;
        if (trace != null)
        {
            var expected = trace.RequestedVisibility ? AtkUnitBaseVisibilityState.Show : AtkUnitBaseVisibilityState.Hide;
            var mask = AtkUnitBaseVisibilityState.Show | AtkUnitBaseVisibilityState.Hide;
            nativeMatch = (addon->VisibilityState & mask) == expected
                && (addon->AppliedVisibilityState & mask) == expected
                && (addon->AppliedVisibilityState & AtkUnitBaseVisibilityState.TransitionComplete) != 0
                && (trace.RequestedVisibility ? drawSuppressionMask == 0 : displaySuppressed)
                && root != null && root->IsVisible() == trace.RequestedVisibility;
        }
        return $"{header}; {rootState}; uldRoot=0x{(nint)addon->UldManager.RootNode:X}; {Assessment(index, addon, nativeMatch)}; {CaptureNodeList(addon)}; {config}";
    }

    private string Assessment(int index, AtkUnitBase* addon, bool? nativeMatch)
    {
        var trace = traces[index];
        if (trace == null)
            return "identity=untracked; nativeMatch=untracked";
        var identityMatches = trace.AddonIdentity == (nint)addon;
        if (trace.Confirmed && (!identityMatches || nativeMatch == false))
            trace.NativeStateChangedAfterConfirmation = true;
        var identity = addon == null ? "missing" : identityMatches ? "same" : "replaced";
        return $"identity={identity}; nativeMatch={(nativeMatch.HasValue ? nativeMatch.Value.ToString() : "unavailable")}";
    }

    private static string CaptureNodeList(AtkUnitBase* addon)
    {
        var uld = &addon->UldManager;
        var count = uld->NodeListCount;
        if (uld->NodeList == null || count > uld->NodeListSize)
            return $"nodeList=unavailable; nodeCount={count}; nodeCapacity={uld->NodeListSize}";
        var sampled = Math.Min((int)count, 128);
        var visible = 0;
        for (var index = 0; index < sampled; index++)
        {
            var node = uld->NodeList[index];
            if (node != null && node->IsVisible())
                visible++;
        }
        // Individual node flags do not prove inherited visibility or pixels rendered.
        return $"nodeListLocalVisible={visible}/{sampled}; nodeCount={count}; nodeScanCapped={count > sampled}";
    }

    private static string CaptureConfig(int index)
    {
        try
        {
            var hotbarDisp = Plugin.GameConfig.UiControl.TryGetUInt("HotbarDisp", out var raw) ? $"0x{raw:X8}" : "unavailable";
            var config = AddonConfig.Instance();
            if (config == null || !config->IsLoaded || config->ActiveDataSet == null)
                return $"HotbarDispRaw={hotbarDisp}; savedConfig=unavailable or not loaded";
            var data = config->ActiveDataSet;
            var layout = data->CurrentHudLayout;
            var hash = SavedNameHashes[index];
            // GetConfigEntryByAddonName can create/reorder entries. Read fixed SDK spans instead.
            var active = FindEntry(data->ConfigEntries, hash);
            var layouts = data->HudLayoutConfigEntries;
            var perLayout = layouts.Length / 4;
            var saved = layout is >= 0 and < 4 && layouts.Length % 4 == 0
                ? FindEntry(layouts.Slice(layout * perLayout, perLayout), hash)
                : "unavailable invalid layout index";
            return $"HotbarDispRaw={hotbarDisp}; hudLayoutIndex={layout}; savedNameHash=0x{hash:X8}; activeConfig=[{active}]; layoutConfig=[{saved}]";
        }
        catch (Exception ex) { return $"configReadError={ex.GetType().Name}"; }
    }

    private static string FindEntry(ReadOnlySpan<AddonConfigEntry> entries, uint hash)
    {
        var firstIndex = -1;
        var matches = 0;
        for (var index = 0; index < entries.Length; index++)
        {
            if (entries[index].AddonNameHash != hash)
                continue;
            if (firstIndex < 0)
                firstIndex = index;
            matches++;
        }
        if (firstIndex < 0)
            return "not found; matches=0";
        // Fixed storage can include spare entries. Do not imply the first hash match is authoritative.
        var entry = entries[firstIndex];
        return $"index={firstIndex}; matches={matches}; ambiguous={matches > 1}; rawCandidateOnly=true; ByteValue1=0x{entry.ByteValue1:X2}; ByteValue2=0x{entry.ByteValue2:X2}; ByteValue3=0x{entry.ByteValue3:X2}; Alpha={entry.Alpha}; ElementFlags=0x{entry.ElementFlags:X8}; HasValue={entry.HasValue}; IsOpen={entry.IsOpen}";
    }

    private static uint[] MakeSavedNameHashes()
    {
        var hashes = new uint[Names.Length];
        for (var index = 0; index < Names.Length; index++)
        {
            // Installed SDK: AddonNameHash is CRC32 of the fixed ASCII addon name plus "_a".
            var crc = uint.MaxValue;
            foreach (var value in Names[index] + "_a")
            {
                crc ^= (byte)value;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xEDB88320u);
            }
            hashes[index] = ~crc;
        }
        return hashes;
    }

    private void StartDrawListener()
    {
        if (drawListening)
            return;
        try
        {
            // Retain cleanup responsibility even if registration fails after only some names.
            drawListening = true;
            foreach (var name in Names)
                Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, name, OnPostDraw);
        }
        catch (Exception ex)
        {
            try
            {
                Plugin.AddonLifecycle.UnregisterListener(OnPostDraw);
                drawListening = false;
            }
            catch (Exception cleanup) { ReportFailure("draw-listener cleanup", cleanup); }
            ReportFailure("draw-listener registration", ex);
        }
    }

    private void StopDrawListenerIfIdle()
    {
        if (!drawListening || Array.Exists(traces, trace => trace != null))
            return;
        try
        {
            Plugin.AddonLifecycle.UnregisterListener(OnPostDraw);
            drawListening = false;
        }
        catch (Exception ex) { ReportFailure("draw-listener removal", ex); }
    }

    private void ReportFailure(string operation, Exception ex)
    {
        if (faultReported)
            return;
        faultReported = true;
        try { Plugin.Log.Warning($"[XASlave] [PvP Hotbars] diagnostic read failed during {operation}: {ex.GetType().Name}; command behavior was not changed."); }
        catch (Exception) { /* Diagnostics must never change the command result. */ }
    }

    private static bool ValidNumber(int number) => number is >= 1 and <= 10;
}
