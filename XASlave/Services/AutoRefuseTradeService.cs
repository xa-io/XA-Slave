using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace XASlave.Services;

public unsafe sealed class AutoRefuseTradeService : IDisposable
{
    private const string RefusedTradeMessage = "XA Slave: Refused incoming trade request.";
    private const string UnknownTraderName = "the trader";
    private const int IncomingTraderEntityIdTtlMs = 5000;

    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;

    private Hook<AgentShowDelegate>? tradeAgentShowHook;
    private Hook<InventoryManager.Delegates.SendTradeRequest>? sendTradeRequestHook;
    private Hook<TradeStatusUpdateDelegate>? tradeStatusUpdateHook;
    private bool initialized;
    private bool enabled;
    private bool showNotification = true;
    private bool sendEcho;
    private string extraCommands = string.Empty;
    private long lastOutgoingTradeMs;
    private long lastAutoRefuseMs;
    private uint lastIncomingTradeEntityId;
    private long lastIncomingTradeEntityMs;

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

    public string StatusText { get; private set; } = "Disabled";

    public void ApplyConfiguration(bool showNotification, bool sendEcho, string extraCommands)
    {
        this.showNotification = showNotification;
        this.sendEcho = sendEcho;
        this.extraCommands = extraCommands ?? string.Empty;
        RefreshStatusText();
    }

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            DisableHooks();
            StatusText = "Disabled";
            return false;
        }

        EnsureInitialized();
        if (sendTradeRequestHook == null || (tradeStatusUpdateHook == null && tradeAgentShowHook == null))
        {
            StatusText = "Unavailable - trade refusal hooks are missing.";
            return false;
        }

        sendTradeRequestHook.Enable();
        tradeStatusUpdateHook?.Enable();
        tradeAgentShowHook?.Enable();
        enabled = true;
        RefreshStatusText();
        return true;
    }

    public void Dispose()
    {
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

    private void DisableHooks()
    {
        if (tradeAgentShowHook is { IsDisposed: false, IsEnabled: true })
            tradeAgentShowHook.Disable();
        if (sendTradeRequestHook is { IsDisposed: false, IsEnabled: true })
            sendTradeRequestHook.Disable();
        if (tradeStatusUpdateHook is { IsDisposed: false, IsEnabled: true })
            tradeStatusUpdateHook.Disable();
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = true;

        tradeAgentShowHook = TryCreateTradeAgentShowHook();
        sendTradeRequestHook = TryCreateSendTradeRequestHook();
        tradeStatusUpdateHook = TryCreateHook<TradeStatusUpdateDelegate>(Sigs.TradeStatusUpdateSig, TradeStatusUpdateDetour, "TradeStatusUpdate");
    }

    private Hook<T>? TryCreateHook<T>(ProtectedSig signature, T detour, string label)
        where T : Delegate
    {
        try
        {
            if (!sigScanner.TryScanText(signature, out var address) || address == nint.Zero)
                return null;

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
                return null;

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
            var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Trade);
            if (agent == null)
                return null;

            var address = GetVirtualFunctionAddress(agent->VirtualTable, "Show");
            if (address == nint.Zero)
                return null;

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
        if (!enabled)
        {
            StatusText = "Disabled";
            return;
        }

        var feedbackMode = showNotification switch
        {
            true when sendEcho => "notification + /e",
            true => "notification only",
            _ when sendEcho => "/e only",
            _ => "silent"
        };

        var surfaceCount = 0;
        surfaceCount += tradeAgentShowHook != null ? 1 : 0;
        surfaceCount += tradeStatusUpdateHook != null ? 1 : 0;
        var extraCommandLabel = CountExtraCommands() switch
        {
            0 => "no extra messages",
            1 => "1 extra message/command",
            var count => $"{count} extra messages/commands"
        };

        StatusText = $"Enabled - incoming trades are refused automatically ({feedbackMode}, {surfaceCount} surfaces, {extraCommandLabel}).";
    }

    private void SendTradeRequestDetour(InventoryManager* manager, uint entityId)
    {
        lastOutgoingTradeMs = Environment.TickCount64;
        sendTradeRequestHook?.Original(manager, entityId);
    }

    private void TradeAgentShowDetour(AgentInterface* agent)
    {
        if (!enabled)
        {
            tradeAgentShowHook?.Original(agent);
            return;
        }

        try
        {
            if (IsLikelyIncomingTrade() && TryRefuseIncomingTrade())
                return;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Refuse Trade failed while intercepting the trade window.");
        }

        tradeAgentShowHook?.Original(agent);
    }

    private nint TradeStatusUpdateDetour(InventoryManager* manager, nint entityId, nint packet)
    {
        var result = tradeStatusUpdateHook?.Original(manager, entityId, packet) ?? 0;

        if (!enabled || packet == nint.Zero)
            return result;

        try
        {
            var eventType = Marshal.ReadByte(packet + 4);
            if (eventType != 1)
                return result;

            if (!IsLikelyIncomingTrade())
                return result;

            var traderEntityId = ReadTradePacketEntityId(packet);
            RememberIncomingTrader(traderEntityId);
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
        if (nowMs - lastAutoRefuseMs <= 1000)
            return false;

        lastAutoRefuseMs = nowMs;
        traderEntityId = GetCurrentTraderEntityId(traderEntityId, nowMs);
        var traderName = ResolveTraderNameSafely(traderEntityId);
        InventoryManager.Instance()->RefuseTrade();
        ReportTradeRefused(traderName);
        return true;
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
            Plugin.Framework.Run(() =>
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
