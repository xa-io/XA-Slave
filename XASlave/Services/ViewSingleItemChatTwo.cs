using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace XASlave.Services;

// Chat 2 draws its own menus. Its item payload supplies the subject without
// consulting native context agents or retaining the chat message.
internal sealed class ViewSingleItemChatTwo : IDisposable
{
    private readonly object gate = new();
    private readonly Action<uint> onItem;
    private readonly ICallGateSubscriber<string> register;
    private readonly ICallGateSubscriber<string, object?> unregister;
    private readonly ICallGateSubscriber<object?> available;
    private readonly ICallGateSubscriber<string, PlayerPayload?, ulong, Payload?, SeString?, SeString?, object?> invoke;
    private string? registration;
    private bool availableSubscribed;
    private bool invokeSubscribed;
    private bool disposed;

    internal ViewSingleItemChatTwo(IDalamudPluginInterface pluginInterface, Action<uint> onItem)
    {
        this.onItem = onItem;
        register = pluginInterface.GetIpcSubscriber<string>("ChatTwo.Register");
        unregister = pluginInterface.GetIpcSubscriber<string, object?>("ChatTwo.Unregister");
        available = pluginInterface.GetIpcSubscriber<object?>("ChatTwo.Available");
        invoke = pluginInterface.GetIpcSubscriber<string, PlayerPayload?, ulong, Payload?, SeString?, SeString?, object?>("ChatTwo.Invoke");
        try
        {
            available.Subscribe(OnAvailable);
            availableSubscribed = true;
            invoke.Subscribe(DrawMenu);
            invokeSubscribed = true;
            OnAvailable();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void OnAvailable()
    {
        lock (gate)
        {
            if (disposed || !ReleaseRegistration()) return;
            try
            {
                var id = register.InvokeFunc();
                if (!string.IsNullOrEmpty(id)) registration = id;
            }
            catch (IpcNotReadyError) { } // Chat 2 is optional and may load later.
            catch (Exception error)
            {
                Plugin.Log.Warning(error, "[View Item] Chat 2 menu registration failed.");
            }
        }
    }

    private bool ReleaseRegistration()
    {
        if (registration == null) return true;
        try
        {
            unregister.InvokeAction(registration);
            registration = null;
            return true;
        }
        catch (IpcNotReadyError)
        {
            // The old provider has unloaded; no old registration remains there.
            registration = null;
            return true;
        }
        catch (Exception error)
        {
            Plugin.Log.Warning(error, "[View Item] Chat 2 menu unregistration failed.");
            // Do not add another registration while this one is unresolved.
            return false;
        }
    }

    private void DrawMenu(string id, PlayerPayload? sender, ulong contentId,
        Payload? payload, SeString? senderString, SeString? content)
    {
        lock (gate)
        {
            if (disposed || registration == null || registration != id) return;
        }
        if (payload is not ItemPayload item) return;
        try
        {
            var itemId = ViewSingleItemShopSource.NormalizeItemId(item.ItemId);
            if (itemId == 0 || !ViewSingleItemShopSource.TryDescribe(itemId, out _, out _)) return;
            if (ImGui.MenuItem("View Single Item")) onItem(itemId);
        }
        catch (Exception error)
        {
            Plugin.Log.Warning(error, "[View Item] Chat 2 item menu failed.");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (availableSubscribed)
            {
                try { available.Unsubscribe(OnAvailable); }
                catch (Exception error) { Plugin.Log.Warning(error, "[View Item] Chat 2 availability cleanup failed."); }
                availableSubscribed = false;
            }
            if (invokeSubscribed)
            {
                try { invoke.Unsubscribe(DrawMenu); }
                catch (Exception error) { Plugin.Log.Warning(error, "[View Item] Chat 2 menu cleanup failed."); }
                invokeSubscribed = false;
            }
            ReleaseRegistration();
        }
    }
}
