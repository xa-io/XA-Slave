using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace XASlave.Services;

public enum XagmanTradeRefusalOverride
{
    Inactive,
    IdleDemand,
    DropboxAutoAcceptSuppression,
}

public unsafe sealed class AutoRefuseTradeService : IDisposable
{
    private static string RefusedTradeMessage => global::XASlave.Localization.UiLocale.Native("SD8180BF6C0373F95");
    private const string UnknownTraderName = "the trader";
    private const int IncomingTraderEntityIdTtlMs = 5000;

    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;

    private Hook<AgentShowDelegate>? tradeAgentShowHook;
    private Hook<InventoryManager.Delegates.SendTradeRequest>? sendTradeRequestHook;
    private Hook<TradeStatusUpdateDelegate>? tradeStatusUpdateHook;
    private bool initialized;
    private bool manualEnabled;
    private bool enabled;
    private XagmanTradeRefusalOverride xagmanOverride;
    private bool showNotification = true;
    private bool sendEcho;
    private string extraCommands = string.Empty;
    private long lastOutgoingTradeMs;
    private long lastAutoRefuseMs;
    private uint lastIncomingTradeEntityId;
    private long lastIncomingTradeEntityMs;
    private Func<uint, bool>? onhTradeAdmission;
    private Action<uint>? onhTradeStarted;

    // ONH owns this temporary filter only while running. Unknown actors fail closed.
    public bool SetOnhTradeAdmission(Func<uint, bool>? admission, Action<uint>? started = null)
    {
        onhTradeAdmission = admission;
        onhTradeStarted = started;
        if (admission != null)
        {
            EnsureInitialized(retryMissing: initialized);
            if (tradeStatusUpdateHook == null || sendTradeRequestHook == null)
            {
                onhTradeAdmission = null;
                onhTradeStarted = null;
                return false;
            }
            // A retry may have created the status hook while manual refusal was already active.
            // RefreshEffectiveState's enabled fast path must not leave that new hook disabled.
            if (enabled)
            {
                if (!DisableHooks()) return false;
                enabled = false;
            }
        }
        return RefreshEffectiveState();
    }

    public AutoRefuseTradeService(
        ISigScanner sigScanner,
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.sigScanner = sigScanner;
        this.interopProvider = interopProvider;
        this.log = log;
    }

    public bool IsEnabled => enabled;

    public bool IsManuallyEnabled => manualEnabled;

    public XagmanTradeRefusalOverride XagmanOverride => xagmanOverride;

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);

    public void ApplyConfiguration(bool showNotification, bool sendEcho, string extraCommands)
    {
        this.showNotification = showNotification;
        this.sendEcho = sendEcho;
        this.extraCommands = extraCommands ?? string.Empty;
        RefreshStatusText();
    }

    public bool SetEnabled(bool value)
    {
        if (value && xagmanOverride != XagmanTradeRefusalOverride.DropboxAutoAcceptSuppression)
        {
            EnsureInitialized(retryMissing: initialized);
            if (!HasRequiredHooks)
            {
                manualEnabled = false;
                enabled = false;
                DisableHooks();
                StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SF1C9B6A39E4D6F72"));
                return false;
            }
        }

        manualEnabled = value;
        var applied = RefreshEffectiveState();
        if (value
            && xagmanOverride != XagmanTradeRefusalOverride.DropboxAutoAcceptSuppression
            && !applied)
        {
            manualEnabled = false;
            RefreshEffectiveState();
            return false;
        }

        return manualEnabled;
    }

    public bool SetXagmanOverride(XagmanTradeRefusalOverride value)
    {
        xagmanOverride = value;
        return RefreshEffectiveState();
    }

    public void Dispose()
    {
        onhTradeAdmission = null;
        onhTradeStarted = null;
        manualEnabled = false;
        xagmanOverride = XagmanTradeRefusalOverride.Inactive;
        enabled = false;
        DisableHooks();

        if (tradeAgentShowHook is { IsDisposed: false })
            tradeAgentShowHook.Dispose();
        if (sendTradeRequestHook is { IsDisposed: false })
            sendTradeRequestHook.Dispose();
        if (tradeStatusUpdateHook is { IsDisposed: false })
            tradeStatusUpdateHook.Dispose();

        tradeAgentShowHook = null;
        sendTradeRequestHook = null;
        tradeStatusUpdateHook = null;
    }

    private bool HasRequiredHooks =>
        sendTradeRequestHook != null
        && (tradeStatusUpdateHook != null || tradeAgentShowHook != null);

    private bool RefreshEffectiveState()
    {
        var shouldEnable = onhTradeAdmission != null
            || (xagmanOverride != XagmanTradeRefusalOverride.DropboxAutoAcceptSuppression
                && (manualEnabled || xagmanOverride == XagmanTradeRefusalOverride.IdleDemand));
        if (!shouldEnable)
        {
            enabled = false;
            var disabled = DisableHooks();
            RefreshStatusText();
            return disabled;
        }

        if (enabled)
        {
            RefreshStatusText();
            return true;
        }

        EnsureInitialized(retryMissing: initialized);
        if (!HasRequiredHooks)
        {
            enabled = false;
            DisableHooks();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SF1C9B6A39E4D6F72"));
            return false;
        }

        try
        {
            sendTradeRequestHook!.Enable();
            tradeStatusUpdateHook?.Enable();
            tradeAgentShowHook?.Enable();
            enabled = true;
            RefreshStatusText();
            return true;
        }
        catch (Exception ex)
        {
            enabled = false;
            DisableHooks();
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S41E563CC963EDDA0"));
            log.Warning(ex, "[XASlave] Auto Refuse Trade failed to enable its hooks.");
            return false;
        }
    }

    private bool DisableHooks()
    {
        var success = true;
        try
        {
            if (tradeAgentShowHook is { IsDisposed: false, IsEnabled: true })
                tradeAgentShowHook.Disable();
        }
        catch (Exception ex)
        {
            success = false;
            log.Warning(ex, "[XASlave] Auto Refuse Trade failed to disable AgentTradeShow.");
        }

        try
        {
            if (sendTradeRequestHook is { IsDisposed: false, IsEnabled: true })
                sendTradeRequestHook.Disable();
        }
        catch (Exception ex)
        {
            success = false;
            log.Warning(ex, "[XASlave] Auto Refuse Trade failed to disable SendTradeRequest.");
        }

        try
        {
            if (tradeStatusUpdateHook is { IsDisposed: false, IsEnabled: true })
                tradeStatusUpdateHook.Disable();
        }
        catch (Exception ex)
        {
            success = false;
            log.Warning(ex, "[XASlave] Auto Refuse Trade failed to disable TradeStatusUpdate.");
        }

        return success;
    }

    private void EnsureInitialized(bool retryMissing = false)
    {
        if (initialized && !retryMissing)
            return;

        initialized = true;

        tradeAgentShowHook ??= TryCreateTradeAgentShowHook();
        sendTradeRequestHook ??= TryCreateSendTradeRequestHook();
        tradeStatusUpdateHook ??= TryCreateHook<TradeStatusUpdateDelegate>(Sigs.TradeStatusUpdateSig, TradeStatusUpdateDetour, "TradeStatusUpdate");
    }

    private Hook<T>? TryCreateHook<T>(ProtectedSig signature, T detour, string label)
        where T : Delegate
    {
        try
        {
            if (!sigScanner.TryScanText(signature, out var address) || address == nint.Zero)
            {
                log.Warning($"[XASlave] Auto Refuse Trade could not resolve {label}; retry by disabling and re-enabling the feature.");
                return null;
            }

            var hook = interopProvider.HookFromAddress<T>(address, detour);
            return hook;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Auto Refuse Trade failed to create {label} hook.");
            return null;
        }
    }

    private Hook<InventoryManager.Delegates.SendTradeRequest>? TryCreateSendTradeRequestHook()
    {
        try
        {
            var address = (nint)InventoryManager.MemberFunctionPointers.SendTradeRequest;
            if (address == nint.Zero)
            {
                log.Warning("[XASlave] Auto Refuse Trade could not resolve SendTradeRequest; retry by disabling and re-enabling the feature.");
                return null;
            }

            var hook = interopProvider.HookFromAddress<InventoryManager.Delegates.SendTradeRequest>(address, SendTradeRequestDetour);
            return hook;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Refuse Trade failed to create SendTradeRequest hook.");
            return null;
        }
    }

    private Hook<AgentShowDelegate>? TryCreateTradeAgentShowHook()
    {
        try
        {
            var agentModule = AgentModule.Instance();
            if (agentModule == null)
            {
                log.Warning("[XASlave] Auto Refuse Trade could not access AgentModule while resolving AgentTradeShow; retry after login or a UI transition.");
                return null;
            }

            var agent = agentModule->GetAgentByInternalId(AgentId.Trade);
            if (agent == null)
            {
                log.Warning("[XASlave] Auto Refuse Trade could not resolve the Trade agent while resolving AgentTradeShow; retry after login or a UI transition.");
                return null;
            }

            var address = GetVirtualFunctionAddress(agent->VirtualTable, "Show");
            if (address == nint.Zero)
            {
                log.Warning("[XASlave] Auto Refuse Trade could not resolve AgentTradeShow; retry by disabling and re-enabling the feature.");
                return null;
            }

            var hook = interopProvider.HookFromAddress<AgentShowDelegate>(address, TradeAgentShowDetour);
            return hook;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Refuse Trade failed to create AgentTradeShow hook.");
            return null;
        }
    }

    private static nint GetVirtualFunctionAddress<TVTable>(TVTable* vtable, string fieldName)
        where TVTable : unmanaged
    {
        if (vtable == null)
            return nint.Zero;

        var field = typeof(TVTable).GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
        var offset = field?.GetCustomAttribute<FieldOffsetAttribute>()?.Value;
        if (offset == null)
            return nint.Zero;

        return *(nint*)((byte*)vtable + offset.Value);
    }

    private void RefreshStatusText()
    {
        if (onhTradeAdmission != null)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("SBCC80D9C43821C5F"));
            return;
        }
        if (xagmanOverride == XagmanTradeRefusalOverride.DropboxAutoAcceptSuppression)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S23A4F6EDA10358C1"));
            return;
        }

        if (!enabled)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, (xagmanOverride == XagmanTradeRefusalOverride.IdleDemand ? global::XASlave.Localization.UiMessage.Key("SE80082F7C2F129B3") : global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E")));
            return;
        }

        var feedbackMode = showNotification switch
        {
            true when sendEcho => global::XASlave.Localization.UiMessage.Key("S8E52945D5B35C5C0"),
            true => global::XASlave.Localization.UiMessage.Key("S3F6ECB3C8E303037"),
            _ when sendEcho => global::XASlave.Localization.UiMessage.Key("S77459FE4E6F41A88"),
            _ => global::XASlave.Localization.UiMessage.Key("S5A9B9FC8E986CC2F")
        };

        var surfaceCount = 1;
        surfaceCount += tradeAgentShowHook != null ? 1 : 0;
        surfaceCount += tradeStatusUpdateHook != null ? 1 : 0;
        var extraCommandLabel = CountExtraCommands() switch
        {
            0 => global::XASlave.Localization.UiMessage.Key("S2FDF8CB3F14EFE96"),
            1 => global::XASlave.Localization.UiMessage.Key("S588446A6B4D53BED"),
            var count => global::XASlave.Localization.UiMessage.Format("S0199763351E02A50", count)
        };
        var ownerLabel = (manualEnabled, xagmanOverride == XagmanTradeRefusalOverride.IdleDemand) switch
        {
            (true, true) => global::XASlave.Localization.UiMessage.Key("S547B33DD8CA7CADA"),
            (false, true) => global::XASlave.Localization.UiMessage.Key("S24D1769AFCD7D430"),
            _ => global::XASlave.Localization.UiMessage.Key("S36BDE66F289A3568"),
        };

        var availabilityLabel = (surfaceCount < 3 ? global::XASlave.Localization.UiMessage.Key("S14F862A9FCF680A8") : global::XASlave.Localization.UiMessage.Key("S92C1CDFDF4CB9CF6"));
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Format("SF0691375F9521277", availabilityLabel, ownerLabel, feedbackMode, surfaceCount, extraCommandLabel));
    }

    private void SendTradeRequestDetour(InventoryManager* manager, uint entityId)
    {
        try
        {
            lastOutgoingTradeMs = Environment.TickCount64;
            if (onhTradeAdmission != null)
            {
                if (!onhTradeAdmission(entityId))
                    return;
                onhTradeStarted?.Invoke(entityId);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Refuse Trade failed to record an outgoing trade timestamp.");
        }

        sendTradeRequestHook?.OriginalDisposeSafe(manager, entityId);
    }

    private void TradeAgentShowDetour(AgentInterface* agent)
    {
        if (!enabled)
        {
            tradeAgentShowHook?.OriginalDisposeSafe(agent);
            return;
        }

        try
        {
            var permitted = onhTradeAdmission?.Invoke(GetCurrentTraderEntityId(0, Environment.TickCount64)) ?? false;
            if (!permitted && IsLikelyIncomingTrade() && TryRefuseIncomingTrade())
                return;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Refuse Trade failed while intercepting the trade window.");
        }

        tradeAgentShowHook?.OriginalDisposeSafe(agent);
    }

    private nint TradeStatusUpdateDetour(InventoryManager* manager, nint entityId, nint packet)
    {
        // Record admission before Original can show Trade and Dropbox can confirm it.
        if (onhTradeAdmission != null && packet != nint.Zero && Marshal.ReadByte(packet + 4) == 1)
        {
            try
            {
                var trader = ReadTradePacketEntityId(packet);
                RememberIncomingTrader(trader);
                if (onhTradeAdmission(trader))
                    onhTradeStarted?.Invoke(trader);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "[XASlave] ONH trade admission observation failed.");
            }
        }
        var result = tradeStatusUpdateHook?.OriginalDisposeSafe(manager, entityId, packet) ?? 0;

        if (!enabled || packet == nint.Zero)
            return result;

        try
        {
            var eventType = Marshal.ReadByte(packet + 4);
            if (eventType != 1)
                return result;

            if (onhTradeAdmission == null && !IsLikelyIncomingTrade())
                return result;

            var traderEntityId = ReadTradePacketEntityId(packet);
            RememberIncomingTrader(traderEntityId);
            if (onhTradeAdmission?.Invoke(traderEntityId) == true)
                return result;
            TryRefuseIncomingTrade(traderEntityId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Refuse Trade failed while processing a trade update.");
        }

        return result;
    }

    private bool IsLikelyIncomingTrade()
    {
        return Environment.TickCount64 - lastOutgoingTradeMs > 3000;
    }

    private bool TryRefuseIncomingTrade(uint traderEntityId = 0)
    {
        var nowMs = Environment.TickCount64;
        if (onhTradeAdmission == null && nowMs - lastAutoRefuseMs <= 1000)
            return false;

        lastAutoRefuseMs = nowMs;
        traderEntityId = GetCurrentTraderEntityId(traderEntityId, nowMs);
        var traderName = ResolveTraderNameSafely(traderEntityId);
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            log.Warning("[XASlave] Auto Refuse Trade could not refuse the trade because InventoryManager is unavailable.");
            return false;
        }

        inventoryManager->RefuseTrade();
        if (onhTradeAdmission == null) ReportTradeRefused(traderName);
        return true;
    }

    internal static string SanitizeImportedExtraCommands(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join(
            Environment.NewLine,
            value[..Math.Min(value.Length, 1024)]
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !line.StartsWith("/", StringComparison.Ordinal)));
    }

    private static uint ReadTradePacketEntityId(nint packet)
    {
        if (packet == nint.Zero)
            return 0;

        return unchecked((uint)Marshal.ReadInt32(packet + 40));
    }

    private void RememberIncomingTrader(uint traderEntityId)
    {
        if (traderEntityId == 0)
            return;

        lastIncomingTradeEntityId = traderEntityId;
        lastIncomingTradeEntityMs = Environment.TickCount64;
    }

    private uint GetCurrentTraderEntityId(uint traderEntityId, long nowMs)
    {
        if (traderEntityId != 0)
            return traderEntityId;

        if (lastIncomingTradeEntityId != 0 && nowMs - lastIncomingTradeEntityMs <= IncomingTraderEntityIdTtlMs)
            return lastIncomingTradeEntityId;

        return 0;
    }

    private void ReportTradeRefused(string traderName)
    {
        if (!showNotification && !sendEcho && string.IsNullOrWhiteSpace(extraCommands))
            return;

        try
        {
            Plugin.RunOnGameThread(() =>
            {
                try
                {
                    if (showNotification)
                        Plugin.ToastGui.ShowNormal(RefusedTradeMessage);

                    if (sendEcho)
                        ChatHelper.SendMessage($"/e {RefusedTradeMessage}");

                    ExecuteExtraCommands(traderName);
                }
                catch (Exception ex)
                {
                    log.Warning(ex, "[XASlave] Auto Refuse Trade failed while reporting a refused trade.");
                }
            });
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Refuse Trade failed while reporting a refused trade.");
        }
    }

    private void ExecuteExtraCommands(string traderName)
    {
        if (string.IsNullOrWhiteSpace(extraCommands))
            return;

        var commands = extraCommands.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (commands.Length == 0)
            return;

        foreach (var command in commands)
        {
            var chatEntry = BuildChatEntry(command, traderName);
            if (string.IsNullOrWhiteSpace(chatEntry))
                continue;

            ChatHelper.SendMessage(chatEntry);
        }
    }

    private static string BuildChatEntry(string line, string traderName)
    {
        var expanded = ExpandTradeMessageTokens(line, traderName).Trim();
        if (string.IsNullOrWhiteSpace(expanded))
            return string.Empty;

        return expanded.StartsWith("/", StringComparison.Ordinal)
            ? expanded
            : $"/e {expanded}";
    }

    private static string ExpandTradeMessageTokens(string text, string traderName)
    {
        var safeTraderName = string.IsNullOrWhiteSpace(traderName) ? UnknownTraderName : traderName.Trim();
        return text
            .Replace("<trader>", safeTraderName, StringComparison.OrdinalIgnoreCase)
            .Replace("<target>", safeTraderName, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveTraderNameSafely(uint traderEntityId)
    {
        try
        {
            return ResolveTraderName(traderEntityId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Refuse Trade failed while resolving the incoming trader name.");
            return UnknownTraderName;
        }
    }

    private static string ResolveTraderName(uint traderEntityId)
    {
        if (traderEntityId != 0)
        {
            foreach (var actor in Plugin.ObjectTable)
            {
                if (actor.EntityId != traderEntityId)
                    continue;

                var traderName = NormalizeTraderName(actor.Name.TextValue);
                if (!string.IsNullOrWhiteSpace(traderName))
                    return traderName;
            }
        }

        var targetingPlayerName = ResolveTargetingPlayerName();
        return string.IsNullOrWhiteSpace(targetingPlayerName)
            ? UnknownTraderName
            : targetingPlayerName;
    }

    private static string ResolveTargetingPlayerName()
    {
        var localPlayer = Plugin.ObjectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.GameObjectId == 0)
            return string.Empty;

        IPlayerCharacter? closestPlayer = null;
        var closestDistanceSquared = float.MaxValue;
        foreach (var actor in Plugin.ObjectTable)
        {
            if (actor is not IPlayerCharacter player
                || player.GameObjectId == 0
                || player.GameObjectId == localPlayer.GameObjectId
                || player.TargetObjectId != localPlayer.GameObjectId)
            {
                continue;
            }

            var distanceSquared = System.Numerics.Vector3.DistanceSquared(localPlayer.Position, player.Position);
            if (closestPlayer != null && distanceSquared >= closestDistanceSquared)
                continue;

            closestPlayer = player;
            closestDistanceSquared = distanceSquared;
        }

        return closestPlayer == null
            ? string.Empty
            : NormalizeTraderName(closestPlayer.Name.TextValue);
    }

    private static string NormalizeTraderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var trimmed = name.Trim();
        var atIndex = trimmed.IndexOf('@');
        if (atIndex >= 0)
            trimmed = trimmed[..atIndex].Trim();

        return trimmed;
    }

    private int CountExtraCommands()
    {
        if (string.IsNullOrWhiteSpace(extraCommands))
            return 0;

        return extraCommands.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    private delegate void AgentShowDelegate(AgentInterface* agent);

    private delegate nint TradeStatusUpdateDelegate(InventoryManager* manager, nint entityId, nint packet);
}
