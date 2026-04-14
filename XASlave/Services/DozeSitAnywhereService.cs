using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GameVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;

namespace XASlave.Services;

public unsafe sealed class DozeSitAnywhereService : IDisposable
{
    private const ushort DozeEmoteId = 88;
    private const ushort SitEmoteId = 96;
    private const float UnsitRestoreDistance = 3f;

    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;

    private Hook<ShouldSnapDelegate>? shouldSnapHook;
    private Hook<ShouldSnapDelegate>? shouldSnapUnsitHook;
    private delegate* unmanaged<nint, ushort, nint, byte, byte, void> useEmote;

    private bool initialized;
    private bool enabled;
    private bool suppressedSnap;
    private Vector3? savedSitPosition;
    private float? savedSitRotation;

    public DozeSitAnywhereService(
        IClientState clientState,
        IObjectTable objectTable,
        ISigScanner sigScanner,
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.sigScanner = sigScanner;
        this.interopProvider = interopProvider;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
        {
            RefreshStatusText();
            return enabled;
        }

        if (!value)
        {
            enabled = false;
            ResetTemporaryState();
            ToggleHook(shouldSnapHook, false, "DozeShouldSnap");
            ToggleHook(shouldSnapUnsitHook, false, "SitShouldSnapUnsit");
            StatusText = "Disabled";
            return false;
        }

        EnsureInitialized();
        if (useEmote == null || shouldSnapHook == null || shouldSnapUnsitHook == null)
        {
            StatusText = "Unavailable - Doze/Sit Anywhere signatures or hooks are missing.";
            return false;
        }

        try
        {
            ToggleHook(shouldSnapHook, true, "DozeShouldSnap");
            ToggleHook(shouldSnapUnsitHook, true, "SitShouldSnapUnsit");
            enabled = true;
            RefreshStatusText();
            return true;
        }
        catch (Exception ex)
        {
            enabled = false;
            ResetTemporaryState();
            ToggleHook(shouldSnapHook, false, "DozeShouldSnap");
            ToggleHook(shouldSnapUnsitHook, false, "SitShouldSnapUnsit");
            StatusText = "Unavailable - failed to enable Doze & Sit Anywhere.";
            log.Warning(ex, "[XASlave] Failed to enable Doze & Sit Anywhere.");
            return false;
        }
    }

    public bool RequestDoze()
    {
        if (!TryGetEmoteAgent(out var agent))
            return false;

        ResetTemporaryState();

        try
        {
            suppressedSnap = true;
            useEmote((nint)agent, DozeEmoteId, nint.Zero, 0, 0);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Doze & Sit Anywhere failed while firing Doze.");
            return false;
        }
        finally
        {
            suppressedSnap = false;
        }
    }

    public bool RequestSit()
    {
        if (!TryGetEmoteAgent(out var agent))
            return false;

        var player = (Character*)(objectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player == null)
            return false;

        try
        {
            savedSitPosition = new Vector3(
                player->GameObject.Position.X,
                player->GameObject.Position.Y,
                player->GameObject.Position.Z);
            savedSitRotation = player->GameObject.Rotation;

            useEmote((nint)agent, SitEmoteId, nint.Zero, 0, 0);
            return true;
        }
        catch (Exception ex)
        {
            ResetTemporaryState();
            log.Warning(ex, "[XASlave] Doze & Sit Anywhere failed while firing Sit.");
            return false;
        }
    }

    public void Dispose()
    {
        enabled = false;
        ResetTemporaryState();
        ToggleHook(shouldSnapHook, false, "DozeShouldSnap");
        ToggleHook(shouldSnapUnsitHook, false, "SitShouldSnapUnsit");
        DisposeHook(ref shouldSnapHook);
        DisposeHook(ref shouldSnapUnsitHook);
        useEmote = null;
        StatusText = "Disabled";
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = true;

        if (sigScanner.TryScanText(Sigs.UseEmoteSig, out var useEmoteAddress) && useEmoteAddress != nint.Zero)
        {
            useEmote = (delegate* unmanaged<nint, ushort, nint, byte, byte, void>)useEmoteAddress;
            log.Information($"[XASlave] Located Doze & Sit Anywhere emote function at 0x{useEmoteAddress:X}.");
        }
        else
        {
            log.Warning("[XASlave] Doze & Sit Anywhere emote signature was not found.");
        }

        shouldSnapHook = TryCreateHook(Sigs.ShouldSnapSig, ShouldSnapDetour, "DozeShouldSnap");
        shouldSnapUnsitHook = TryCreateHook(Sigs.ShouldSnapUnsitSig, ShouldSnapUnsitDetour, "SitShouldSnapUnsit");
    }

    private bool TryGetEmoteAgent(out AgentInterface* agent)
    {
        agent = null;
        if (!enabled || !clientState.IsLoggedIn || useEmote == null)
            return false;

        agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Emote);
        return agent != null;
    }

    private void RefreshStatusText()
    {
        StatusText = enabled
            ? "Enabled - `/xa sit`, `/xa doze`, and the panel buttons now trigger unsnapped Sit/Doze emotes."
            : "Disabled";
    }

    private void ResetTemporaryState()
    {
        suppressedSnap = false;
        savedSitPosition = null;
        savedSitRotation = null;
    }

    private Hook<ShouldSnapDelegate>? TryCreateHook(ProtectedSig signature, ShouldSnapDelegate detour, string label)
    {
        try
        {
            if (!sigScanner.TryScanText(signature, out var address) || address == nint.Zero)
            {
                log.Warning($"[XASlave] {label} signature was not found.");
                return null;
            }

            var hook = interopProvider.HookFromAddress<ShouldSnapDelegate>(address, detour);
            log.Information($"[XASlave] Created {label} hook at 0x{address:X}.");
            return hook;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Failed to create {label} hook.");
            return null;
        }
    }

    private void ToggleHook(Hook<ShouldSnapDelegate>? hook, bool value, string label)
    {
        if (hook == null || hook.IsDisposed)
            return;

        try
        {
            if (value)
            {
                if (!hook.IsEnabled)
                    hook.Enable();
            }
            else if (hook.IsEnabled)
            {
                hook.Disable();
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Failed to {(value ? "enable" : "disable")} {label} hook.");
        }
    }

    private static void DisposeHook(ref Hook<ShouldSnapDelegate>? hook)
    {
        if (hook is { IsDisposed: false })
            hook.Dispose();

        hook = null;
    }

    private byte ShouldSnapDetour(Character* player, SnapPosition* snapPosition)
    {
        return (byte)(suppressedSnap ? 0 : (shouldSnapHook?.Original(player, snapPosition) ?? 0));
    }

    private byte ShouldSnapUnsitDetour(Character* player, SnapPosition* snapPosition)
    {
        var original = shouldSnapUnsitHook?.Original(player, snapPosition) ?? 0;
        if (original == 0)
            return 0;

        var position = savedSitPosition;
        var rotation = savedSitRotation;
        if (position.HasValue && rotation.HasValue && Distance(player->GameObject.Position, position.Value) < UnsitRestoreDistance)
        {
            snapPosition->PositionB.X = position.Value.X;
            snapPosition->PositionB.Y = position.Value.Y;
            snapPosition->PositionB.Z = position.Value.Z;
            snapPosition->RotationB = rotation.Value;
        }

        savedSitPosition = null;
        savedSitRotation = null;
        return original;
    }

    private static float Distance(GameVector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x38)]
    private struct SnapPosition
    {
        [FieldOffset(0x00)] public GameVector3 PositionA;
        [FieldOffset(0x10)] public float RotationA;
        [FieldOffset(0x20)] public GameVector3 PositionB;
        [FieldOffset(0x30)] public float RotationB;
    }

    private delegate byte ShouldSnapDelegate(Character* player, SnapPosition* snapPosition);
}
