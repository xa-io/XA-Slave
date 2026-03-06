using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

// List<Vector3> needed for vnavmesh.Path.MoveTo IPC

namespace XASlave.Services;

/// <summary>
/// IPC client for XA Slave — provides access to XA Database and all external plugin IPC channels.
/// All channel names verified against each plugin's source code.
///
/// Integrated plugins:
///   XA Database    — Save, Refresh, IsReady, GetDbPath, GetVersion, GetCharacterName, GetGil,
///                    GetRetainerGil, GetFcInfo, GetPlotInfo, GetPersonalPlotInfo, SearchItems
///   vnavmesh       — Nav mesh pathfinding, movement, rebuild
///   AutoRetainer   — Suppressed/busy check, multi mode toggle, character/retainer post-processing
///   Lifestream     — IsBusy, Abort, ExecuteCommand, ChangeWorld, teleport shortcuts
///   YesAlready     — IsEnabled, SetEnabled, PausePlugin
///   Deliveroo      — GC turn-in running check
///   PandorasBox    — Feature get/set enabled, pause feature
///   Dropbox        — Item trading queue management (unverified channel names)
///   TextAdvance    — IsEnabled, IsBusy, IsPaused, Stop
///   Artisan        — IsBusy, endurance, crafting lists, stop request
///   Splatoon       — IsLoaded check
/// </summary>
public sealed class IpcClient
{
    private readonly IPluginLog log;

    // ── XA Database ──
    private readonly ICallGateSubscriber<object> xaSaveSubscriber;
    private readonly ICallGateSubscriber<object> xaRefreshSubscriber;
    private readonly ICallGateSubscriber<bool> xaIsReadySubscriber;
    private readonly ICallGateSubscriber<string> xaGetDbPathSubscriber;
    private readonly ICallGateSubscriber<string> xaGetVersionSubscriber;
    private readonly ICallGateSubscriber<string> xaGetCharacterNameSubscriber;
    private readonly ICallGateSubscriber<int> xaGetGilSubscriber;
    private readonly ICallGateSubscriber<int> xaGetRetainerGilSubscriber;
    private readonly ICallGateSubscriber<string> xaGetFcInfoSubscriber;
    private readonly ICallGateSubscriber<string> xaGetPlotInfoSubscriber;
    private readonly ICallGateSubscriber<string> xaGetPersonalPlotInfoSubscriber;
    private readonly ICallGateSubscriber<string, string> xaSearchItemsSubscriber;

    // ── vnavmesh (source: vnavmesh/IPCProvider.cs — prefixes "vnavmesh." + name) ──
    private readonly ICallGateSubscriber<bool> vnavIsReadySubscriber;
    private readonly ICallGateSubscriber<bool> vnavRebuildSubscriber;
    private readonly ICallGateSubscriber<Vector3, bool, bool> vnavPathfindAndMoveToSubscriber;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> vnavPathfindAndMoveCloseToSubscriber;
    private readonly ICallGateSubscriber<bool> vnavPathIsRunningSubscriber;
    private readonly ICallGateSubscriber<bool> vnavNavPathfindInProgressSubscriber;
    private readonly ICallGateSubscriber<bool> vnavSimpleMovePathfindInProgressSubscriber;
    private readonly ICallGateSubscriber<object> vnavStopSubscriber;
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> vnavMoveToSubscriber;

    // ── AutoRetainer (source: AutoRetainer/Modules/IPC.cs + AutoRetainerAPI/ApiConsts.cs — explicit channel names) ──
    private readonly ICallGateSubscriber<bool> arGetSuppressedSubscriber;
    private readonly ICallGateSubscriber<bool, object> arSetSuppressedSubscriber;
    private readonly ICallGateSubscriber<bool> arGetMultiModeEnabledSubscriber;
    private readonly ICallGateSubscriber<bool, object> arSetMultiModeEnabledSubscriber;
    // Character post-processing — runs after all retainers done for a character, before relog
    private readonly ICallGateSubscriber<string, object> arRequestCharacterPostProcessSubscriber;
    private readonly ICallGateSubscriber<object> arFinishCharacterPostProcessSubscriber;
    private readonly ICallGateSubscriber<string, object> arOnCharacterReadyForPostprocessSubscriber;
    private readonly ICallGateSubscriber<object> arOnCharacterAdditionalTaskSubscriber;
    // Retainer post-processing — runs after each retainer is done
    private readonly ICallGateSubscriber<string, object> arRequestRetainerPostProcessSubscriber;
    private readonly ICallGateSubscriber<object> arFinishRetainerPostProcessSubscriber;
    private readonly ICallGateSubscriber<string, string, object> arOnRetainerReadyForPostprocessSubscriber;
    private readonly ICallGateSubscriber<string, object> arOnRetainerAdditionalTaskSubscriber;

    // ── Lifestream (source: Lifestream/IPC/IPCProvider.cs — EzIPC prefix "Lifestream.") ──
    private readonly ICallGateSubscriber<bool> lsIsBusySubscriber;
    private readonly ICallGateSubscriber<object> lsAbortSubscriber;
    private readonly ICallGateSubscriber<string, object> lsExecuteCommandSubscriber;
    private readonly ICallGateSubscriber<string, bool> lsChangeWorldSubscriber;
    private readonly ICallGateSubscriber<bool> lsTeleportToFCSubscriber;
    private readonly ICallGateSubscriber<bool> lsTeleportToHomeSubscriber;
    private readonly ICallGateSubscriber<bool> lsTeleportToApartmentSubscriber;
    private readonly ICallGateSubscriber<string, bool> lsAethernetTeleportSubscriber;

    // ── YesAlready (source: YesAlready/IPC/YesAlreadyIPC.cs — EzIPC prefix "YesAlready.") ──
    private readonly ICallGateSubscriber<bool> yaIsEnabledSubscriber;
    private readonly ICallGateSubscriber<bool, object> yaSetEnabledSubscriber;
    private readonly ICallGateSubscriber<int, object> yaPausePluginSubscriber;
    private readonly ICallGateSubscriber<string, bool> yaIsBotherEnabledSubscriber;
    private readonly ICallGateSubscriber<string, bool, object> yaSetBotherEnabledSubscriber;
    private readonly ICallGateSubscriber<string, int, bool> yaPauseBotherSubscriber;

    // ── Deliveroo (source: Deliveroo/External/DeliverooIpc.cs — explicit channel names) ──
    private readonly ICallGateSubscriber<bool> deliverooIsTurnInRunningSubscriber;

    // ── PandorasBox (source: PandorasBox/IPC/PandoraIPC.cs — explicit channel names) ──
    private readonly ICallGateSubscriber<string, bool?> pandoraGetFeatureSubscriber;
    private readonly ICallGateSubscriber<string, bool, object> pandoraSetFeatureSubscriber;
    private readonly ICallGateSubscriber<string, int, object> pandoraPauseFeatureSubscriber;

    // ── Dropbox (channel names from SND Lua usage — not verified from source) ──
    private readonly ICallGateSubscriber<uint, bool, int, object> dropboxSetItemQuantitySubscriber;
    private readonly ICallGateSubscriber<bool> dropboxIsBusySubscriber;
    private readonly ICallGateSubscriber<object> dropboxBeginTradingSubscriber;

    // ── TextAdvance (source: TextAdvance/Services/IPCProvider.cs — EzIPC prefix "TextAdvance.") ──
    private readonly ICallGateSubscriber<bool> taIsEnabledSubscriber;
    private readonly ICallGateSubscriber<bool> taIsBusySubscriber;
    private readonly ICallGateSubscriber<bool> taIsPausedSubscriber;
    private readonly ICallGateSubscriber<object> taStopSubscriber;

    // ── Artisan (source: Artisan/IPC/IPC.cs — explicit channel names) ──
    private readonly ICallGateSubscriber<bool> artIsBusySubscriber;
    private readonly ICallGateSubscriber<bool> artGetEnduranceStatusSubscriber;
    private readonly ICallGateSubscriber<bool, object> artSetEnduranceStatusSubscriber;
    private readonly ICallGateSubscriber<bool> artIsListRunningSubscriber;
    private readonly ICallGateSubscriber<bool> artIsListPausedSubscriber;
    private readonly ICallGateSubscriber<bool, object> artSetListPauseSubscriber;
    private readonly ICallGateSubscriber<bool> artGetStopRequestSubscriber;
    private readonly ICallGateSubscriber<bool, object> artSetStopRequestSubscriber;
    private readonly ICallGateSubscriber<ushort, int, object> artCraftItemSubscriber;

    // ── Splatoon (source: Splatoon/Modules/SplatoonIPC.cs — explicit channel names) ──
    private readonly ICallGateSubscriber<bool> splatIsLoadedSubscriber;

    public IpcClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;

        // XA Database
        xaSaveSubscriber = pluginInterface.GetIpcSubscriber<object>("XA.Database.Save");
        xaRefreshSubscriber = pluginInterface.GetIpcSubscriber<object>("XA.Database.Refresh");
        xaIsReadySubscriber = pluginInterface.GetIpcSubscriber<bool>("XA.Database.IsReady");
        xaGetDbPathSubscriber = pluginInterface.GetIpcSubscriber<string>("XA.Database.GetDbPath");
        xaGetVersionSubscriber = pluginInterface.GetIpcSubscriber<string>("XA.Database.GetVersion");
        xaGetCharacterNameSubscriber = pluginInterface.GetIpcSubscriber<string>("XA.Database.GetCharacterName");
        xaGetGilSubscriber = pluginInterface.GetIpcSubscriber<int>("XA.Database.GetGil");
        xaGetRetainerGilSubscriber = pluginInterface.GetIpcSubscriber<int>("XA.Database.GetRetainerGil");
        xaGetFcInfoSubscriber = pluginInterface.GetIpcSubscriber<string>("XA.Database.GetFcInfo");
        xaGetPlotInfoSubscriber = pluginInterface.GetIpcSubscriber<string>("XA.Database.GetPlotInfo");
        xaGetPersonalPlotInfoSubscriber = pluginInterface.GetIpcSubscriber<string>("XA.Database.GetPersonalPlotInfo");
        xaSearchItemsSubscriber = pluginInterface.GetIpcSubscriber<string, string>("XA.Database.SearchItems");

        // vnavmesh — channel names from RegisterFunc/RegisterAction("X") → "vnavmesh.X"
        vnavIsReadySubscriber = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        vnavRebuildSubscriber = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.Rebuild");
        vnavPathfindAndMoveToSubscriber = pluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        vnavPathfindAndMoveCloseToSubscriber = pluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        vnavPathIsRunningSubscriber = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        vnavNavPathfindInProgressSubscriber = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.PathfindInProgress");
        vnavSimpleMovePathfindInProgressSubscriber = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        vnavStopSubscriber = pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        vnavMoveToSubscriber = pluginInterface.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");

        // AutoRetainer — explicit IPC channel registration in IPC.Init()
        arGetSuppressedSubscriber = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed");
        arSetSuppressedSubscriber = pluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed");
        arGetMultiModeEnabledSubscriber = pluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetMultiModeEnabled");
        arSetMultiModeEnabledSubscriber = pluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetMultiModeEnabled");
        // Character post-processing (source: AutoRetainerAPI/ApiConsts.cs)
        arRequestCharacterPostProcessSubscriber = pluginInterface.GetIpcSubscriber<string, object>("AutoRetainer.RequestCharacterPostprocess");
        arFinishCharacterPostProcessSubscriber = pluginInterface.GetIpcSubscriber<object>("AutoRetainer.FinishCharacterPostprocessRequest");
        arOnCharacterReadyForPostprocessSubscriber = pluginInterface.GetIpcSubscriber<string, object>("AutoRetainer.OnCharacterReadyForPostprocess");
        arOnCharacterAdditionalTaskSubscriber = pluginInterface.GetIpcSubscriber<object>("AutoRetainer.OnCharacterAdditionalTask");
        // Retainer post-processing
        arRequestRetainerPostProcessSubscriber = pluginInterface.GetIpcSubscriber<string, object>("AutoRetainer.RequestPostprocess");
        arFinishRetainerPostProcessSubscriber = pluginInterface.GetIpcSubscriber<object>("AutoRetainer.FinishPostprocessRequest");
        arOnRetainerReadyForPostprocessSubscriber = pluginInterface.GetIpcSubscriber<string, string, object>("AutoRetainer.OnRetainerReadyForPostprocess");
        arOnRetainerAdditionalTaskSubscriber = pluginInterface.GetIpcSubscriber<string, object>("AutoRetainer.OnRetainerAdditionalTask");

        // Lifestream — EzIPC prefix "Lifestream." + method name
        lsIsBusySubscriber = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        lsAbortSubscriber = pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
        lsExecuteCommandSubscriber = pluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand");
        lsChangeWorldSubscriber = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
        lsTeleportToFCSubscriber = pluginInterface.GetIpcSubscriber<bool>("Lifestream.TeleportToFC");
        lsTeleportToHomeSubscriber = pluginInterface.GetIpcSubscriber<bool>("Lifestream.TeleportToHome");
        lsTeleportToApartmentSubscriber = pluginInterface.GetIpcSubscriber<bool>("Lifestream.TeleportToApartment");
        lsAethernetTeleportSubscriber = pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.AethernetTeleport");

        // YesAlready — EzIPC prefix "YesAlready." + method name
        yaIsEnabledSubscriber = pluginInterface.GetIpcSubscriber<bool>("YesAlready.IsPluginEnabled");
        yaSetEnabledSubscriber = pluginInterface.GetIpcSubscriber<bool, object>("YesAlready.SetPluginEnabled");
        yaPausePluginSubscriber = pluginInterface.GetIpcSubscriber<int, object>("YesAlready.PausePlugin");
        yaIsBotherEnabledSubscriber = pluginInterface.GetIpcSubscriber<string, bool>("YesAlready.IsBotherEnabled");
        yaSetBotherEnabledSubscriber = pluginInterface.GetIpcSubscriber<string, bool, object>("YesAlready.SetBotherEnabled");
        yaPauseBotherSubscriber = pluginInterface.GetIpcSubscriber<string, int, bool>("YesAlready.PauseBother");

        // Deliveroo — explicit const channel names
        deliverooIsTurnInRunningSubscriber = pluginInterface.GetIpcSubscriber<bool>("Deliveroo.IsTurnInRunning");

        // PandorasBox — explicit channel names in PandoraIPC.Init()
        pandoraGetFeatureSubscriber = pluginInterface.GetIpcSubscriber<string, bool?>("PandorasBox.GetFeatureEnabled");
        pandoraSetFeatureSubscriber = pluginInterface.GetIpcSubscriber<string, bool, object>("PandorasBox.SetFeatureEnabled");
        pandoraPauseFeatureSubscriber = pluginInterface.GetIpcSubscriber<string, int, object>("PandorasBox.PauseFeature");

        // Dropbox — channel names from SND Lua IPC usage (not verified from plugin source)
        dropboxSetItemQuantitySubscriber = pluginInterface.GetIpcSubscriber<uint, bool, int, object>("Dropbox.SetItemQuantity");
        dropboxIsBusySubscriber = pluginInterface.GetIpcSubscriber<bool>("Dropbox.IsBusy");
        dropboxBeginTradingSubscriber = pluginInterface.GetIpcSubscriber<object>("Dropbox.BeginTradingQueue");

        // TextAdvance — EzIPC prefix "TextAdvance." + method name
        taIsEnabledSubscriber = pluginInterface.GetIpcSubscriber<bool>("TextAdvance.IsEnabled");
        taIsBusySubscriber = pluginInterface.GetIpcSubscriber<bool>("TextAdvance.IsBusy");
        taIsPausedSubscriber = pluginInterface.GetIpcSubscriber<bool>("TextAdvance.IsPaused");
        taStopSubscriber = pluginInterface.GetIpcSubscriber<object>("TextAdvance.Stop");

        // Artisan — explicit channel names in IPC.Init()
        artIsBusySubscriber = pluginInterface.GetIpcSubscriber<bool>("Artisan.IsBusy");
        artGetEnduranceStatusSubscriber = pluginInterface.GetIpcSubscriber<bool>("Artisan.GetEnduranceStatus");
        artSetEnduranceStatusSubscriber = pluginInterface.GetIpcSubscriber<bool, object>("Artisan.SetEnduranceStatus");
        artIsListRunningSubscriber = pluginInterface.GetIpcSubscriber<bool>("Artisan.IsListRunning");
        artIsListPausedSubscriber = pluginInterface.GetIpcSubscriber<bool>("Artisan.IsListPaused");
        artSetListPauseSubscriber = pluginInterface.GetIpcSubscriber<bool, object>("Artisan.SetListPause");
        artGetStopRequestSubscriber = pluginInterface.GetIpcSubscriber<bool>("Artisan.GetStopRequest");
        artSetStopRequestSubscriber = pluginInterface.GetIpcSubscriber<bool, object>("Artisan.SetStopRequest");
        artCraftItemSubscriber = pluginInterface.GetIpcSubscriber<ushort, int, object>("Artisan.CraftItem");

        // Splatoon — explicit channel names
        splatIsLoadedSubscriber = pluginInterface.GetIpcSubscriber<bool>("Splatoon.IsLoaded");

        log.Information("[XASlave] IPC client initialized (11 plugins).");
    }

    // ═══════════════════════════════════════════════════
    //  Connectivity Checks
    // ═══════════════════════════════════════════════════

    public bool IsAutoRetainerAvailable()
    {
        try { arGetSuppressedSubscriber.InvokeFunc(); return true; }
        catch { return false; }
    }

    public bool IsLifestreamAvailable()
    {
        try { lsIsBusySubscriber.InvokeFunc(); return true; }
        catch { return false; }
    }

    public bool IsYesAlreadyAvailable()
    {
        try { yaIsEnabledSubscriber.InvokeFunc(); return true; }
        catch { return false; }
    }

    public bool IsDeliverooAvailable()
    {
        try { deliverooIsTurnInRunningSubscriber.InvokeFunc(); return true; }
        catch { return false; }
    }

    public bool IsPandorasBoxAvailable()
    {
        try { pandoraGetFeatureSubscriber.InvokeFunc("_ping"); return true; }
        catch (Dalamud.Plugin.Ipc.Exceptions.IpcNotReadyError) { return false; }
        catch { return true; }
    }

    public bool IsDropboxAvailable()
    {
        try { dropboxIsBusySubscriber.InvokeFunc(); return true; }
        catch { return false; }
    }

    public bool IsTextAdvanceAvailable()
    {
        try { taIsEnabledSubscriber.InvokeFunc(); return true; }
        catch { return false; }
    }

    public bool IsArtisanAvailable()
    {
        try { artIsBusySubscriber.InvokeFunc(); return true; }
        catch { return false; }
    }

    public bool IsVnavAvailable()
    {
        try { vnavIsReadySubscriber.InvokeFunc(); return true; }
        catch { return false; }
    }

    public bool IsXaDatabaseAvailable()
    {
        try { xaIsReadySubscriber.InvokeFunc(); return true; }
        catch { return false; }
    }

    public bool IsSplatoonAvailable()
    {
        try { splatIsLoadedSubscriber.InvokeFunc(); return true; }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════
    //  XA Database
    // ═══════════════════════════════════════════════════

    public bool Save()
    {
        try { xaSaveSubscriber.InvokeAction(); return true; }
        catch (Exception ex) { log.Warning($"[XASlave] IPC: XA.Database.Save failed — {ex.Message}"); return false; }
    }

    public bool Refresh()
    {
        try { xaRefreshSubscriber.InvokeAction(); return true; }
        catch (Exception ex) { log.Warning($"[XASlave] IPC: XA.Database.Refresh failed — {ex.Message}"); return false; }
    }

    public bool IsReady()
    {
        try { return xaIsReadySubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public string GetDbPath()
    {
        try { return xaGetDbPathSubscriber.InvokeFunc(); }
        catch { return string.Empty; }
    }

    public string GetVersion()
    {
        try { return xaGetVersionSubscriber.InvokeFunc(); }
        catch { return "N/A"; }
    }

    public string GetCharacterName()
    {
        try { return xaGetCharacterNameSubscriber.InvokeFunc(); }
        catch { return string.Empty; }
    }

    public int GetGil()
    {
        try { return xaGetGilSubscriber.InvokeFunc(); }
        catch { return 0; }
    }

    public int GetRetainerGil()
    {
        try { return xaGetRetainerGilSubscriber.InvokeFunc(); }
        catch { return 0; }
    }

    public string GetFcInfo()
    {
        try { return xaGetFcInfoSubscriber.InvokeFunc(); }
        catch { return string.Empty; }
    }

    public string GetPlotInfo()
    {
        try { return xaGetPlotInfoSubscriber.InvokeFunc(); }
        catch { return string.Empty; }
    }

    public string GetPersonalPlotInfo()
    {
        try { return xaGetPersonalPlotInfoSubscriber.InvokeFunc(); }
        catch { return string.Empty; }
    }

    public string SearchItems(string query)
    {
        try { return xaSearchItemsSubscriber.InvokeFunc(query); }
        catch { return string.Empty; }
    }

    // ═══════════════════════════════════════════════════
    //  vnavmesh
    // ═══════════════════════════════════════════════════

    public bool VnavIsReady()
    {
        try { return vnavIsReadySubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool VnavRebuild()
    {
        try { vnavRebuildSubscriber.InvokeFunc(); return true; }
        catch (Exception ex) { log.Error($"[XASlave] IPC: vnavmesh.Nav.Rebuild failed — {ex.Message}"); return false; }
    }

    public bool VnavPathfindAndMoveCloseTo(Vector3 pos, bool fly, float range)
    {
        try { return vnavPathfindAndMoveCloseToSubscriber.InvokeFunc(pos, fly, range); }
        catch (Exception ex) { log.Error($"[XASlave] IPC: vnavmesh.SimpleMove.PathfindAndMoveCloseTo failed — {ex.Message}"); return false; }
    }

    public bool VnavPathfindAndMoveTo(Vector3 pos, bool fly)
    {
        try { return vnavPathfindAndMoveToSubscriber.InvokeFunc(pos, fly); }
        catch { return false; }
    }

    public bool VnavPathIsRunning()
    {
        try { return vnavPathIsRunningSubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool VnavNavPathfindInProgress()
    {
        try { return vnavNavPathfindInProgressSubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool VnavSimpleMovePathfindInProgress()
    {
        try { return vnavSimpleMovePathfindInProgressSubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool VnavStop()
    {
        try { vnavStopSubscriber.InvokeAction(); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Direct waypoint movement — moves in a straight line to the exact coordinates.
    /// This is vnavmesh.Path.MoveTo (NOT PathfindAndMoveTo which uses navmesh pathfinding).
    /// Critical for jump puzzles where exact stop positions are required.
    /// Matches SND's IPC.vnavmesh.MoveTo(vectorList, false) used by pot0to's scripts.
    /// </summary>
    public void VnavMoveTo(Vector3 destination, bool fly)
    {
        try
        {
            var waypoints = new List<Vector3> { destination };
            vnavMoveToSubscriber.InvokeAction(waypoints, fly);
        }
        catch (Exception ex) { log.Error($"[XASlave] IPC: vnavmesh.Path.MoveTo failed — {ex.Message}"); }
    }

    // ═══════════════════════════════════════════════════
    //  AutoRetainer
    // ═══════════════════════════════════════════════════

    public bool AutoRetainerGetSuppressed()
    {
        try { return arGetSuppressedSubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool AutoRetainerSetSuppressed(bool suppressed)
    {
        try { arSetSuppressedSubscriber.InvokeAction(suppressed); return true; }
        catch { return false; }
    }

    public bool AutoRetainerGetMultiModeEnabled()
    {
        try { return arGetMultiModeEnabledSubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool AutoRetainerSetMultiModeEnabled(bool enabled)
    {
        try { arSetMultiModeEnabledSubscriber.InvokeAction(enabled); return true; }
        catch { return false; }
    }

    // ── Character Post-Processing ──

    /// <summary>Register XA Slave for character-level post-processing in AR multi-mode.
    /// AR will pause before relogging and fire OnCharacterReadyForPostprocess.</summary>
    public bool AutoRetainerRequestCharacterPostProcess(string pluginName)
    {
        try { arRequestCharacterPostProcessSubscriber.InvokeAction(pluginName); return true; }
        catch (Exception ex) { log.Error($"[XASlave] IPC: AR.RequestCharacterPostprocess failed — {ex.Message}"); return false; }
    }

    /// <summary>Signal AR that character post-processing is done. AR will resume (relog to next character).</summary>
    public bool AutoRetainerFinishCharacterPostProcess()
    {
        try { arFinishCharacterPostProcessSubscriber.InvokeAction(); return true; }
        catch (Exception ex) { log.Error($"[XASlave] IPC: AR.FinishCharacterPostprocessRequest failed — {ex.Message}"); return false; }
    }

    /// <summary>Subscribe to the character post-processing event.
    /// AR fires this with pluginName when it's ready for your post-processing.</summary>
    public void AutoRetainerSubscribeCharacterPostProcess(Action<string> callback)
    {
        arOnCharacterReadyForPostprocessSubscriber.Subscribe(callback);
    }

    /// <summary>Unsubscribe from the character post-processing event.</summary>
    public void AutoRetainerUnsubscribeCharacterPostProcess(Action<string> callback)
    {
        arOnCharacterReadyForPostprocessSubscriber.Unsubscribe(callback);
    }

    /// <summary>Subscribe to OnCharacterAdditionalTask — AR fires this PER CHARACTER before
    /// checking the postprocess list. Plugins must call RequestCharacterPostProcess in response
    /// to get into the list for this character.</summary>
    public void AutoRetainerSubscribeCharacterAdditionalTask(Action callback)
    {
        arOnCharacterAdditionalTaskSubscriber.Subscribe(callback);
    }

    /// <summary>Unsubscribe from OnCharacterAdditionalTask.</summary>
    public void AutoRetainerUnsubscribeCharacterAdditionalTask(Action callback)
    {
        arOnCharacterAdditionalTaskSubscriber.Unsubscribe(callback);
    }

    // ── Retainer Post-Processing ──

    /// <summary>Register for retainer-level post-processing in AR. AR will pause after each retainer.</summary>
    public bool AutoRetainerRequestRetainerPostProcess(string pluginName)
    {
        try { arRequestRetainerPostProcessSubscriber.InvokeAction(pluginName); return true; }
        catch (Exception ex) { log.Error($"[XASlave] IPC: AR.RequestPostprocess failed — {ex.Message}"); return false; }
    }

    /// <summary>Signal AR that retainer post-processing is done.</summary>
    public bool AutoRetainerFinishRetainerPostProcess()
    {
        try { arFinishRetainerPostProcessSubscriber.InvokeAction(); return true; }
        catch (Exception ex) { log.Error($"[XASlave] IPC: AR.FinishPostprocessRequest failed — {ex.Message}"); return false; }
    }

    /// <summary>Subscribe to the retainer post-processing event.
    /// AR fires this with (pluginName, retainerName) when a retainer is ready.</summary>
    public void AutoRetainerSubscribeRetainerPostProcess(Action<string, string> callback)
    {
        arOnRetainerReadyForPostprocessSubscriber.Subscribe(callback);
    }

    /// <summary>Unsubscribe from the retainer post-processing event.</summary>
    public void AutoRetainerUnsubscribeRetainerPostProcess(Action<string, string> callback)
    {
        arOnRetainerReadyForPostprocessSubscriber.Unsubscribe(callback);
    }

    // ═══════════════════════════════════════════════════
    //  Lifestream
    // ═══════════════════════════════════════════════════

    public bool LifestreamIsBusy()
    {
        try { return lsIsBusySubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool LifestreamAbort()
    {
        try { lsAbortSubscriber.InvokeAction(); return true; }
        catch { return false; }
    }

    public bool LifestreamExecuteCommand(string args)
    {
        try { lsExecuteCommandSubscriber.InvokeAction(args); return true; }
        catch { return false; }
    }

    public bool LifestreamChangeWorld(string world)
    {
        try { return lsChangeWorldSubscriber.InvokeFunc(world); }
        catch { return false; }
    }

    public bool LifestreamTeleportToFC()
    {
        try { return lsTeleportToFCSubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool LifestreamTeleportToHome()
    {
        try { return lsTeleportToHomeSubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool LifestreamTeleportToApartment()
    {
        try { return lsTeleportToApartmentSubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool LifestreamAethernetTeleport(string destination)
    {
        try { return lsAethernetTeleportSubscriber.InvokeFunc(destination); }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════
    //  YesAlready
    // ═══════════════════════════════════════════════════

    public bool YesAlreadyIsEnabled()
    {
        try { return yaIsEnabledSubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool YesAlreadySetEnabled(bool enabled)
    {
        try { yaSetEnabledSubscriber.InvokeAction(enabled); return true; }
        catch { return false; }
    }

    public bool YesAlreadyPause(int milliseconds)
    {
        try { yaPausePluginSubscriber.InvokeAction(milliseconds); return true; }
        catch { return false; }
    }

    public bool YesAlreadyIsBotherEnabled(string featureName)
    {
        try { return yaIsBotherEnabledSubscriber.InvokeFunc(featureName); }
        catch (Exception ex) { Plugin.Log.Error($"[XASlave] YA.IsBotherEnabled('{featureName}') error: {ex.Message}"); return false; }
    }

    public bool YesAlreadySetBotherEnabled(string featureName, bool enabled)
    {
        try { yaSetBotherEnabledSubscriber.InvokeAction(featureName, enabled); return true; }
        catch (Exception ex) { Plugin.Log.Error($"[XASlave] YA.SetBotherEnabled('{featureName}', {enabled}) error: {ex.Message}"); return false; }
    }

    public bool YesAlreadyPauseBother(string featureName, int milliseconds)
    {
        try { return yaPauseBotherSubscriber.InvokeFunc(featureName, milliseconds); }
        catch (Exception ex) { Plugin.Log.Error($"[XASlave] YA.PauseBother('{featureName}', {milliseconds}) error: {ex.Message}"); return false; }
    }

    // ═══════════════════════════════════════════════════
    //  Deliveroo
    // ═══════════════════════════════════════════════════

    public bool DeliverooIsTurnInRunning()
    {
        try { return deliverooIsTurnInRunningSubscriber.InvokeFunc(); }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════
    //  PandorasBox
    // ═══════════════════════════════════════════════════

    public bool? PandoraGetFeatureEnabled(string featureName)
    {
        try { return pandoraGetFeatureSubscriber.InvokeFunc(featureName); }
        catch { return null; }
    }

    public bool PandoraSetFeatureEnabled(string featureName, bool enabled)
    {
        try { pandoraSetFeatureSubscriber.InvokeAction(featureName, enabled); return true; }
        catch { return false; }
    }

    public bool PandoraPauseFeature(string featureName, int pauseMs)
    {
        try { pandoraPauseFeatureSubscriber.InvokeAction(featureName, pauseMs); return true; }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════
    //  Dropbox
    // ═══════════════════════════════════════════════════

    public bool DropboxSetItemQuantity(uint itemId, bool isHq, int quantity)
    {
        try { dropboxSetItemQuantitySubscriber.InvokeAction(itemId, isHq, quantity); return true; }
        catch { return false; }
    }

    public bool DropboxIsBusy()
    {
        try { return dropboxIsBusySubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool DropboxBeginTrading()
    {
        try { dropboxBeginTradingSubscriber.InvokeAction(); return true; }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════
    //  TextAdvance
    // ═══════════════════════════════════════════════════

    public bool TextAdvanceIsEnabled()
    {
        try { return taIsEnabledSubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool TextAdvanceIsBusy()
    {
        try { return taIsBusySubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool TextAdvanceIsPaused()
    {
        try { return taIsPausedSubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool TextAdvanceStop()
    {
        try { taStopSubscriber.InvokeAction(); return true; }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════
    //  Artisan
    // ═══════════════════════════════════════════════════

    public bool ArtisanIsBusy()
    {
        try { return artIsBusySubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool ArtisanGetEnduranceStatus()
    {
        try { return artGetEnduranceStatusSubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool ArtisanSetEnduranceStatus(bool enabled)
    {
        try { artSetEnduranceStatusSubscriber.InvokeAction(enabled); return true; }
        catch { return false; }
    }

    public bool ArtisanIsListRunning()
    {
        try { return artIsListRunningSubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool ArtisanIsListPaused()
    {
        try { return artIsListPausedSubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool ArtisanSetListPause(bool paused)
    {
        try { artSetListPauseSubscriber.InvokeAction(paused); return true; }
        catch { return false; }
    }

    public bool ArtisanGetStopRequest()
    {
        try { return artGetStopRequestSubscriber.InvokeFunc(); }
        catch { return false; }
    }

    public bool ArtisanSetStopRequest(bool stop)
    {
        try { artSetStopRequestSubscriber.InvokeAction(stop); return true; }
        catch { return false; }
    }

    public bool ArtisanCraftItem(ushort recipeId, int amount)
    {
        try { artCraftItemSubscriber.InvokeAction(recipeId, amount); return true; }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════
    //  Splatoon
    // ═══════════════════════════════════════════════════

    public bool SplatoonIsLoaded()
    {
        try { return splatIsLoadedSubscriber.InvokeFunc(); }
        catch { return false; }
    }
}
