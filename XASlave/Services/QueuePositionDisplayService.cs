using System;
using System.Runtime.InteropServices;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

public unsafe sealed class QueuePositionDisplayService : IDisposable
{
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;

    private Hook<UpdateWorldTravelDataDelegate>? updateWorldTravelDataHook;
    private Hook<AgentWorldTravelUpdateDelegate>? agentWorldTravelUpdateHook;
    private Hook<ContentFinderQueuePositionDataDelegate>? contentFinderQueuePositionDataHook;

    private bool initialized;
    private bool enabled;
    private DateTime queueEtaUtc = DateTime.UtcNow;

    public QueuePositionDisplayService(
        ISigScanner sigScanner,
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.sigScanner = sigScanner;
        this.interopProvider = interopProvider;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            ToggleHook(updateWorldTravelDataHook, false, "UpdateWorldTravelData");
            ToggleHook(agentWorldTravelUpdateHook, false, "AgentWorldTravelUpdate");
            ToggleHook(contentFinderQueuePositionDataHook, false, "ContentFinderQueuePositionData");
            StatusText = "Disabled";
            return false;
        }

        EnsureInitialized();

        var activeSurfaces = 0;
        activeSurfaces += ToggleHook(updateWorldTravelDataHook, true, "UpdateWorldTravelData");
        activeSurfaces += ToggleHook(agentWorldTravelUpdateHook, true, "AgentWorldTravelUpdate");
        activeSurfaces += ToggleHook(contentFinderQueuePositionDataHook, true, "ContentFinderQueuePositionData");
        if (activeSurfaces == 0)
        {
            StatusText = "Unavailable - queue hooks are missing.";
            return false;
        }

        enabled = true;
        StatusText = $"Enabled - expanded queue details active on {activeSurfaces} surfaces.";
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        DisposeHook(ref updateWorldTravelDataHook);
        DisposeHook(ref agentWorldTravelUpdateHook);
        DisposeHook(ref contentFinderQueuePositionDataHook);
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = true;
        updateWorldTravelDataHook = TryCreateHook<UpdateWorldTravelDataDelegate>(Sigs.UpdateWorldTravelDataSig, UpdateWorldTravelDataDetour, "UpdateWorldTravelData");
        agentWorldTravelUpdateHook = TryCreateHook<AgentWorldTravelUpdateDelegate>(Sigs.AgentWorldTravelUpdateSig, AgentWorldTravelUpdateDetour, "AgentWorldTravelUpdate");
        contentFinderQueuePositionDataHook = TryCreateHook<ContentFinderQueuePositionDataDelegate>(Sigs.ContentFinderQueuePositionDataSig, ContentFinderQueuePositionDataDetour, "ContentFinderQueuePositionData");
    }

    private Hook<T>? TryCreateHook<T>(string signature, T detour, string label)
        where T : Delegate
    {
        try
        {
            if (!sigScanner.TryScanText(signature, out var address) || address == nint.Zero)
            {
                log.Warning($"[XASlave] {label} signature was not found.");
                return null;
            }

            var hook = interopProvider.HookFromAddress<T>(address, detour);
            log.Information($"[XASlave] Created {label} hook at 0x{address:X}.");
            return hook;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Failed to create {label} hook.");
            return null;
        }
    }

    private int ToggleHook<T>(Hook<T>? hook, bool targetEnabled, string label)
        where T : Delegate
    {
        if (hook == null || hook.IsDisposed)
            return 0;

        try
        {
            if (targetEnabled)
            {
                if (!hook.IsEnabled)
                    hook.Enable();

                return hook.IsEnabled ? 1 : 0;
            }

            if (hook.IsEnabled)
                hook.Disable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Failed to {(targetEnabled ? "enable" : "disable")} {label} hook.");
        }

        return 0;
    }

    private static void DisposeHook<T>(ref Hook<T>? hook)
        where T : Delegate
    {
        if (hook is { IsDisposed: false })
            hook.Dispose();

        hook = null;
    }

    private void UpdateWorldTravelDataDetour(nint a1, nint a2)
    {
        try
        {
            if (enabled && a2 != nint.Zero)
            {
                var type = *(byte*)(a2 + 16);
                if (type == 1)
                {
                    var position = *(int*)(a2 + 20);
                    queueEtaUtc = DateTime.UtcNow.AddSeconds(CalculateQueueWaitSeconds(position));
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Queue display failed while sampling world travel data.");
        }

        updateWorldTravelDataHook?.Original(a1, a2);
    }

    private bool AgentWorldTravelUpdateDetour(nint a1, NumberArrayData* numberArrayData, StringArrayData* stringArrayData, bool a4)
    {
        var result = agentWorldTravelUpdateHook?.Original(a1, numberArrayData, stringArrayData, a4) ?? false;
        if (!enabled || !result || numberArrayData == null || stringArrayData == null)
            return result;

        try
        {
            var agentAddress = (nint)AgentWorldTravel.Instance();
            if (agentAddress == nint.Zero || !(*(bool*)(agentAddress + 0x120)))
                return result;

            var positionIndex = numberArrayData->IntArray[5] > 0 ? 6 : 5;
            var queuePosition = *(uint*)(agentAddress + 0x12C);
            var elapsed = TimeSpan.FromSeconds(*(int*)(agentAddress + 0x128));
            var eta = queueEtaUtc - DateTime.UtcNow;
            if (eta < TimeSpan.Zero)
                eta = TimeSpan.Zero;

            var queueText = new SeStringBuilder().AddText($"Queue Position: {queuePosition}").Build();
            var etaText = new SeStringBuilder().AddText($"Elapsed: {elapsed:mm\\:ss} | ETA: {eta:mm\\:ss}").Build();

            stringArrayData->SetValue(positionIndex, queueText.EncodeWithNullTerminator(), false, true, false);
            stringArrayData->SetValue(positionIndex + 1, etaText.EncodeWithNullTerminator(), false, true, false);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Queue display failed while rewriting queue text.");
        }

        return result;
    }

    private byte ContentFinderQueuePositionDataDetour(nint a1, uint a2, nint a3)
    {
        try
        {
            if (enabled && a3 != nint.Zero)
            {
                var position = Marshal.ReadByte(a3 + 4);
                if (position != 0)
                {
                    Marshal.WriteByte(a1 + 91, position);
                    Marshal.WriteByte(a1 + 92, position);
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Queue display failed while applying content finder queue data.");
        }

        return contentFinderQueuePositionDataHook?.Original(a1, a2, a3) ?? 0;
    }

    private static double CalculateQueueWaitSeconds(int position)
    {
        if (position <= 0)
            return 0;

        var fullGroups = (position - 1) / 4;
        var remainingPeople = (position - 1) % 4;
        return (fullGroups * 10f) + (remainingPeople > 0 ? 10f : 0f);
    }

    private delegate void UpdateWorldTravelDataDelegate(nint a1, nint a2);

    private delegate bool AgentWorldTravelUpdateDelegate(nint a1, NumberArrayData* numberArrayData, StringArrayData* stringArrayData, bool a4);

    private delegate byte ContentFinderQueuePositionDataDelegate(nint a1, uint a2, nint a3);
}
