using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace XASlave.Services;

public unsafe sealed class AutoRefuseTradeService : IDisposable
{
    private const string RefusedTradeMessage = "XA Slave: Refused incoming trade request.";

    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;

    private Hook<AgentShowDelegate>? tradeAgentShowHook;
    private Hook<TradeRequestDelegate>? tradeRequestHook;
    private Hook<TradeStatusUpdateDelegate>? tradeStatusUpdateHook;
    private bool initialized;
    private bool enabled;
    private bool showNotification = true;
    private bool sendEcho;
    private string extraCommands = string.Empty;
    private long lastOutgoingTradeMs;
    private long lastAutoRefuseMs;

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
        if (tradeRequestHook == null || (tradeStatusUpdateHook == null && tradeAgentShowHook == null))
        {
            StatusText = "Unavailable - trade refusal hooks are missing.";
            return false;
        }

        tradeRequestHook.Enable();
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
        if (tradeRequestHook is { IsDisposed: false })
            tradeRequestHook.Dispose();
        if (tradeStatusUpdateHook is { IsDisposed: false })
            tradeStatusUpdateHook.Dispose();

        tradeAgentShowHook = null;
        tradeRequestHook = null;
        tradeStatusUpdateHook = null;
    }

    private void DisableHooks()
    {
        if (tradeAgentShowHook is { IsDisposed: false, IsEnabled: true })
            tradeAgentShowHook.Disable();
        if (tradeRequestHook is { IsDisposed: false, IsEnabled: true })
            tradeRequestHook.Disable();
        if (tradeStatusUpdateHook is { IsDisposed: false, IsEnabled: true })
            tradeStatusUpdateHook.Disable();
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = true;

        tradeAgentShowHook = TryCreateTradeAgentShowHook();
        tradeRequestHook = TryCreateHook<TradeRequestDelegate>(Sigs.TradeRequestSig, TradeRequestDetour, "TradeRequest");
        tradeStatusUpdateHook = TryCreateHook<TradeStatusUpdateDelegate>(Sigs.TradeStatusUpdateSig, TradeStatusUpdateDetour, "TradeStatusUpdate");
    }

    private Hook<T>? TryCreateHook<T>(string signature, T detour, string label)
        where T : Delegate
    {
        try
        {
            if (!sigScanner.TryScanText(signature, out var address) || address == nint.Zero)
            {
                log.Warning($"[XASlave] Auto Refuse Trade could not find {label}.");
                return null;
            }

            var hook = interopProvider.HookFromAddress<T>(address, detour);
            log.Information($"[XASlave] Auto Refuse Trade created {label} hook at 0x{address:X}.");
            return hook;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[XASlave] Auto Refuse Trade failed to create {label} hook.");
            return null;
        }
    }

    private Hook<AgentShowDelegate>? TryCreateTradeAgentShowHook()
    {
        try
        {
            var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Trade);
            if (agent == null)
            {
                log.Warning("[XASlave] Auto Refuse Trade could not resolve AgentTrade.");
                return null;
            }

            var address = GetVirtualFunctionAddress(agent->VirtualTable, "Show");
            if (address == nint.Zero)
            {
                log.Warning("[XASlave] Auto Refuse Trade could not resolve AgentTrade.Show.");
                return null;
            }

            var hook = interopProvider.HookFromAddress<AgentShowDelegate>(address, TradeAgentShowDetour);
            log.Information($"[XASlave] Auto Refuse Trade created AgentTradeShow hook at 0x{address:X}.");
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
            true when sendEcho => "notification + /echo",
            true => "notification only",
            _ when sendEcho => "/echo only",
            _ => "silent"
        };

        var surfaceCount = 0;
        surfaceCount += tradeAgentShowHook != null ? 1 : 0;
        surfaceCount += tradeStatusUpdateHook != null ? 1 : 0;
        var extraCommandLabel = CountExtraCommands() switch
        {
            0 => "no extra commands",
            1 => "1 extra command",
            var count => $"{count} extra commands"
        };

        StatusText = $"Enabled - incoming trades are refused automatically ({feedbackMode}, {surfaceCount} surfaces, {extraCommandLabel}).";
    }

    private nint TradeRequestDetour(InventoryManager* manager, uint entityId)
    {
        lastOutgoingTradeMs = Environment.TickCount64;
        return tradeRequestHook?.Original(manager, entityId) ?? 0;
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

            TryRefuseIncomingTrade();
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

    private bool TryRefuseIncomingTrade()
    {
        var nowMs = Environment.TickCount64;
        if (nowMs - lastAutoRefuseMs <= 1000)
            return false;

        lastAutoRefuseMs = nowMs;
        InventoryManager.Instance()->RefuseTrade();
        NotifyTradeRefused();
        ExecuteExtraCommands();
        return true;
    }

    private void NotifyTradeRefused()
    {
        if (!showNotification && !sendEcho)
            return;

        try
        {
            if (showNotification)
                Plugin.Framework.RunOnFrameworkThread(() => Plugin.ToastGui.ShowNormal(RefusedTradeMessage));

            if (sendEcho)
                ChatHelper.SendMessage($"/echo {RefusedTradeMessage}");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Refuse Trade failed while reporting a refused trade.");
        }
    }

    private void ExecuteExtraCommands()
    {
        if (string.IsNullOrWhiteSpace(extraCommands))
            return;

        try
        {
            foreach (var command in extraCommands.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.IsNullOrWhiteSpace(command))
                    continue;

                ChatHelper.SendMessage(command);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Auto Refuse Trade failed while running extra commands.");
        }
    }

    private int CountExtraCommands()
    {
        if (string.IsNullOrWhiteSpace(extraCommands))
            return 0;

        return extraCommands.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    private delegate void AgentShowDelegate(AgentInterface* agent);
    private delegate nint TradeRequestDelegate(InventoryManager* manager, uint entityId);

    private delegate nint TradeStatusUpdateDelegate(InventoryManager* manager, nint entityId, nint packet);
}
