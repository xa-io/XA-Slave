using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using ModelChara = Lumina.Excel.Sheets.ModelChara;
using Ornament = Lumina.Excel.Sheets.Ornament;
using CharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace XASlave.Services;

internal sealed record ViewSingleItemChoice(int Key, string Label, uint ItemId, string UnavailableReason = "");

// Menu callbacks retain values only. Every native operation is re-admitted on
// the framework thread against the current login and captured request.
internal sealed class ViewSingleItemService : IDisposable
{
    private readonly IContextMenu contextMenu;
    private readonly IPluginLog log;
    private readonly ViewSingleItemHistory history;
    private ViewSingleItemChatTwo? chatTwo;
    private ViewSingleItemPreview? preview;
    private InspectOutfitWorld? world;
    private InspectOutfitSnapshot? observed, source;
    private InspectOutfitIdentity? expected;
    private long generation, frame, deadline;
    private long menuEpoch;
    private ulong loginOwner;
    private bool enabled, disposed, waiting;
    private string waitingReason = string.Empty, waitingDiagnostic = string.Empty;

    internal ViewSingleItemService(IContextMenu contextMenu, IPluginLog log, string databasePath)
    { this.contextMenu = contextMenu; this.log = log; history = new(databasePath); }

    internal Action? OpenWindow { get; set; }
    internal string StatusText { get; private set; } = "Disabled";
    internal string PlayerName => source?.Identity.Name ?? expected?.Name ?? string.Empty;
    internal IReadOnlyList<ViewSingleItemChoice> Choices { get; private set; } = Array.Empty<ViewSingleItemChoice>();
    internal int? SelectedKey { get; private set; }
    internal long SelectionRevision { get; private set; }
    internal uint? FramerKitId => preview?.FramerKitId;
    internal bool IsEnabled => enabled;
    internal ViewSingleItemPreview? Preview => source == null ? null : preview;
    internal IReadOnlyList<ViewSingleItemHistoryEntry> History => history.Entries;
    internal string HistoryStatus => history.Status;
    internal void RefreshHistory() => history.Load();
    internal void ShowWindow() => OpenWindow?.Invoke();

    internal bool SetEnabled(bool value)
    {
        if (disposed || value == enabled) return enabled;
        if (value)
        {
            try
            {
                preview = new ViewSingleItemPreview();
                preview.Validate();
                world = new InspectOutfitWorld(preview.Validate, () => frame);
            }
            catch (Exception error)
            {
                preview?.Dispose(); preview = null;
                StatusText = "Unavailable: " + error.Message;
                log.Warning(error, "[View Item] Preview capability unavailable.");
                return false;
            }
            enabled = true; generation++;
            try { chatTwo = new ViewSingleItemChatTwo(Plugin.PluginInterface, QueueCatalogItem); }
            catch (Exception error) { log.Warning(error, "[View Item] Optional Chat 2 integration unavailable."); }
            contextMenu.OnMenuOpened += OnMenuOpened;
            Plugin.Framework.Update += Update;
            Plugin.ClientState.Logout += OnLogout;
            Plugin.ClientState.TerritoryChanged += OnTerritoryChanged;
            StatusText = "Enabled - right-click a player or supported item and choose View Single Item.";
            history.Load();
        }
        else
        {
            enabled = false; Stop("Disabled");
            chatTwo?.Dispose(); chatTwo = null;
            contextMenu.OnMenuOpened -= OnMenuOpened;
            Plugin.Framework.Update -= Update;
            Plugin.ClientState.Logout -= OnLogout;
            Plugin.ClientState.TerritoryChanged -= OnTerritoryChanged;
            preview?.Dispose(); preview = null; world = null;
        }
        return enabled;
    }

    private static bool Ready() => Plugin.Framework.IsInFrameworkUpdateThread
        && Plugin.ClientState.IsLoggedIn && Plugin.PlayerState.IsLoaded && Plugin.PlayerState.ContentId != 0;

    private void OnLogout(int _, int __) => Stop("Logged out; preview stopped.");
    private void OnTerritoryChanged(uint _) => Stop("Area changed; reopen a player or item menu.");

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        var epoch = ++menuEpoch;
        if (!enabled || disposed || !Ready()) return;
        try
        {
            if (ViewSingleItemContextSource.TryResolve(args, out var itemId))
            {
                if (!ViewSingleItemShopSource.TryDescribe(itemId, out _, out var reason))
                {
                    log.Information("[View Item] Item unavailable: item={Item}; reason={Reason}.", itemId, reason);
                    return;
                }
                var shopOwner = Plugin.PlayerState.ContentId;
                var shopGeneration = generation;
                args.AddMenuItem(new MenuItem
                {
                    Name = new SeStringBuilder().AddText("View Single Item").Build(),
                    PrefixChar = 'X', PrefixColor = 539, UseDefaultPrefix = false,
                    OnClicked = _ =>
                    {
                        if (!enabled || disposed || generation != shopGeneration || menuEpoch != epoch || !Ready()
                            || Plugin.PlayerState.ContentId != shopOwner) return;
                        OpenCatalogItem(itemId);
                    },
                });
                return;
            }
            if (args.MenuType != ContextMenuType.Default || args.Target is not MenuTargetDefault target) return;
            if (target.TargetContentId == 0 && IsItemMenuHost(args.AddonName))
                log.Information("[View Item] Item menu has no confirmed item: addon={Addon}; handlers={Handlers}.",
                    args.AddonName ?? "<unknown>", args.EventInterfaces.Count);
            var name = target.TargetName;
            var homeworld = target.TargetHomeWorld.RowId;
            var objectId = target.TargetObjectId;
            var actor = ResolvePlayer(objectId, name, homeworld);
            var menuHost = InspectOutfitControls.Resolve(generation);
            var identity = args.AddonName == "CharacterInspect" && menuHost is { } currentHost
                && args.AddonPtr == currentHost.Addon ? world?.Identity() : null;
            if (identity is { } inspected && (!string.IsNullOrEmpty(name) || homeworld != 0)
                && (inspected.Name != name || inspected.World != homeworld)) identity = null;
            if (actor == null && identity == null) return;
            // Copy all native-backed menu data now; no wrapper survives menu-open.
            var player = actor == null ? null : new PlayerTarget(actor.GameObjectId, actor.EntityId,
                actor.HomeWorld.RowId, actor.Name.ToString());
            var owner = Plugin.PlayerState.ContentId;
            var menuGeneration = generation;
            args.AddMenuItem(new MenuItem
            {
                Name = new SeStringBuilder().AddText("View Single Item").Build(),
                PrefixChar = 'X', PrefixColor = 539, UseDefaultPrefix = false,
                OnClicked = _ =>
                {
                    if (!enabled || disposed || generation != menuGeneration || menuEpoch != epoch || !Ready()
                        || Plugin.PlayerState.ContentId != owner) return;
                    if (identity is { } current) OpenInspection(current);
                    else if (player != null) OpenPlayer(player);
                },
            });
        }
        catch (Exception error) { Fail(error); }
    }

    // Diagnostics only: item resolution itself is not restricted to this list.
    private static bool IsItemMenuHost(string? addon) => addon is
        "CharacterInspect" or "ChatLog" or "ColorantColoring" or "ContentsInfoDetail"
        or "DailyQuestSupply" or "FreeCompanyCreditShop" or "GrandCompanyExchange"
        or "HousingCatalogPreview" or "HousingGoods" or "InclusionShop" or "ItemSearch"
        or "Journal" or "MateriaAttach" or "MiragePrismPrismBoxCrystallize"
        or "RecipeMaterialList" or "RecipeNote" or "RecipeTree" or "ShopExchangeItem"
        or "ShopExchangeItemDialog" or "ShopExchangeCurrency" or "ShopExchangeCoin"
        or "SubmarinePartsMenu" or "Tryon" or "Shop" or "FreeShop" or "CollectablesShop";

    // Chat 2 and the item picker run during UI drawing. Defer native capture
    // until the next framework update, retaining only the clicked ID and session.
    internal void QueueCatalogItem(uint itemId)
    {
        if (!enabled || disposed || !Plugin.ClientState.IsLoggedIn || !Plugin.PlayerState.IsLoaded
            || Plugin.PlayerState.ContentId == 0) return;
        var owner = Plugin.PlayerState.ContentId;
        var queuedGeneration = generation;
        var epoch = ++menuEpoch;
        Plugin.Framework.RunOnTick(() =>
        {
            if (!enabled || disposed || generation != queuedGeneration || menuEpoch != epoch || !Ready()
                || Plugin.PlayerState.ContentId != owner) return;
            OpenCatalogItem(itemId);
        });
    }

    private unsafe void OpenCatalogItem(uint itemId)
    {
        try
        {
            preview!.Validate();
            if (!ViewSingleItemShopSource.TryDescribe(itemId, out var entry, out var reason))
            { Stop("Item preview unavailable: " + reason); OpenWindow?.Invoke(); return; }
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) { Stop("The local character is not ready for an item preview."); return; }
            var address = player.Address;
            var entity = player.EntityId;
            var owner = Plugin.PlayerState.ContentId;
            Stop(); loginOwner = owner;
            var capture = preview.PrepareCatalog(entry, (NativeCharacter*)address);
            var confirmed = Plugin.ObjectTable.LocalPlayer;
            if (!Ready() || Plugin.PlayerState.ContentId != owner || confirmed == null
                || confirmed.Address != address || confirmed.EntityId != entity)
            { Stop("The login session changed during item capture."); return; }
            source = capture.Snapshot();
            Choices = preview.Restore(capture, source);
            Select(Choices[0].Key);
            SaveCapture();
            log.Information("[View Item] Item capture: item={Item}; category={Category}; label={Label}; history=single-item.",
                itemId, entry.Key, capture.PlayerName);
            OpenWindow?.Invoke();
        }
        catch (Exception error) { Fail(error); OpenWindow?.Invoke(); }
    }

    private sealed record PlayerTarget(ulong ObjectId, uint EntityId, uint World, string Name);

    private static IPlayerCharacter? ResolvePlayer(ulong objectId, string name, uint homeworld)
    {
        if (string.IsNullOrWhiteSpace(name) || homeworld == 0 || homeworld >= ushort.MaxValue) return null;
        IPlayerCharacter? match = null;
        foreach (var actor in Plugin.ObjectTable)
        {
            if (actor is not IPlayerCharacter player || player.HomeWorld.RowId != homeworld
                || !string.Equals(player.Name.ToString(), name, StringComparison.Ordinal)
                || (objectId != 0 && objectId != 0xE0000000 && objectId != ulong.MaxValue
                    && objectId != player.GameObjectId && objectId != player.EntityId)) continue;
            if (match != null) return null;
            match = player;
        }
        return match;
    }

    internal static unsafe ViewSingleItemOrnament? CaptureOrnament(InspectOutfitIdentity identity, out string unavailable)
    {
        NearbyZoneNativeBinding.RequireFramework();
        unavailable = string.Empty;
        try
        {
            // Examine can work for a player who is not nearby. Accessory capture
            // requires a unique, currently loaded match and never retains it.
            var player = ResolvePlayer(identity.Entity, identity.Name, identity.World);
            if (player == null || !identity.IsPlayer || player.EntityId != identity.Entity)
            {
                unavailable = "Fashion accessories require the inspected player to be nearby and loaded when captured.";
                Plugin.Log.Information("[View Item] Fashion accessory capture unavailable: no matching loaded player; entity={Entity}; world={World}.",
                    identity.Entity, identity.World);
                return null;
            }
            var address = player.Address;
            var objectId = player.GameObjectId;
            var entityId = player.EntityId;
            NearbyZoneNativeBinding.RequireReadable(address, sizeof(NativeCharacter));
            // These offsets are admitted by the preview's exact SDK/layout gate.
            if (Marshal.ReadIntPtr(address + 0x968) != address)
                throw new InvalidOperationException("The fashion accessory owner changed.");
            var id = unchecked((ushort)Marshal.ReadInt16(address + 0x978));
            var ornamentObject = Marshal.ReadIntPtr(address + 0x970);
            ushort modelId = 0;
            byte modelType = 0;
            if (id != 0)
            {
                if (ornamentObject == 0)
                    throw new InvalidOperationException("The player's fashion accessory is still loading.");
                NearbyZoneNativeBinding.RequireReadable(ornamentObject + 0x2370, sizeof(uint));
                if (unchecked((uint)Marshal.ReadInt32(ornamentObject + 0x2370)) != id)
                    throw new InvalidOperationException("The player's fashion accessory changed during capture.");
                var row = Plugin.DataManager.GetExcelSheet<Ornament>().GetRowOrDefault(id);
                if (row == null || row.Value.Model == 0)
                    throw new InvalidOperationException("The fashion accessory has no supported model data.");
                modelId = row.Value.Model;
                var model = Plugin.DataManager.GetExcelSheet<ModelChara>().GetRowOrDefault(modelId);
                if (model == null || model.Value.Type is not (2 or 3))
                    throw new InvalidOperationException("This fashion accessory uses an unsupported model type.");
                modelType = model.Value.Type;
                if (!OrnamentModelReady(address, ornamentObject, modelId, modelType))
                    throw new InvalidOperationException("The player's fashion accessory model is still loading or changing.");
            }
            else if (ornamentObject != 0)
                throw new InvalidOperationException("The player's fashion accessory is changing.");

            var confirmed = ResolvePlayer(objectId, identity.Name, identity.World);
            if (confirmed == null || confirmed.Address != address || confirmed.EntityId != entityId
                || Marshal.ReadIntPtr(address + 0x968) != address
                || unchecked((ushort)Marshal.ReadInt16(address + 0x978)) != id
                || Marshal.ReadIntPtr(address + 0x970) != ornamentObject)
                throw new InvalidOperationException("The player or fashion accessory changed during capture.");
            if (id != 0)
            {
                NearbyZoneNativeBinding.RequireReadable(ornamentObject + 0x2370, sizeof(uint));
                if (unchecked((uint)Marshal.ReadInt32(ornamentObject + 0x2370)) != id
                    || !OrnamentModelReady(address, ornamentObject, modelId, modelType))
                    throw new InvalidOperationException("The fashion accessory model changed during capture.");
            }
            Plugin.Log.Information("[View Item] Fashion accessory captured: entity={Entity}; ornament={Ornament}; model={Model}; source=loaded-player.",
                entityId, id, modelId);
            return new(id, modelId);
        }
        catch (Exception error)
        {
            unavailable = "Fashion accessory unavailable: " + error.Message;
            Plugin.Log.Warning(error, "[View Item] Fashion accessory capture failed; ordinary equipment capture continues.");
            return null;
        }
    }

    private static unsafe bool OrnamentModelReady(nint player, nint accessory, ushort modelId, byte modelType)
    {
        NearbyZoneNativeBinding.RequireReadable(player, sizeof(NativeCharacter));
        NearbyZoneNativeBinding.RequireReadable(accessory, sizeof(NativeCharacter));
        // The native ornament uses the player's companion index. Read and verify
        // that relationship; we never allocate or mutate this companion.
        if (unchecked((ushort)Marshal.ReadInt16(accessory + 0x8C))
                != unchecked((ushort)Marshal.ReadInt16(player + 0x8C)) + 1
            || Marshal.ReadIntPtr(accessory + 0x1B30) != accessory
            || Marshal.ReadInt32(accessory + 0x1B38) != modelId) return false;
        var draw = (CharacterBase*)((NativeCharacter*)accessory)->DrawObject;
        if (draw == null) return false;
        NearbyZoneNativeBinding.RequireReadable((nint)draw, sizeof(CharacterBase));
        if ((byte)draw->GetModelType() != modelType || draw->SlotCount <= 0
            || draw->SlotCount > 32 || draw->Models == null
            || (Marshal.ReadInt32((nint)draw + 0x2F0) & 0x1F) != 0
            || Marshal.ReadInt32((nint)draw + 0x920) != 0
            || Marshal.ReadInt32((nint)draw + 0x3C8) != 0) return false;
        NearbyZoneNativeBinding.RequireReadable((nint)draw->Models, checked(draw->SlotCount * sizeof(nint)));
        for (var index = 0; index < draw->SlotCount; index++)
            if (draw->Models[index] != null) return true;
        return false;
    }

    private unsafe void OpenPlayer(PlayerTarget player)
    {
        try
        {
            preview!.Validate();
            var actor = ResolvePlayer(player.ObjectId, player.Name, player.World);
            if (actor == null || actor.EntityId != player.EntityId)
            { Stop("That player is no longer loaded. Reopen their context menu."); return; }
            Begin(new(player.EntityId, checked((ushort)player.World), player.Name, 4));
            var agent = AgentInspect.Instance();
            if (agent == null || AgentInspect.MemberFunctionPointers.ExamineCharacter == null)
                throw new InvalidOperationException("The game's player inspection is unavailable.");
            agent->ExamineCharacter(player.EntityId);
        }
        catch (Exception error) { Fail(error); }
    }

    internal void OpenCurrentInspection()
    {
        if (!enabled || disposed || !Ready()) return;
        try
        {
            var identity = world?.Identity();
            if (identity == null || InspectOutfitControls.Resolve(generation) == null)
            { Stop("Inspect a player first, then open View Item."); OpenWindow?.Invoke(); return; }
            OpenInspection(identity.Value);
        }
        catch (Exception error) { Fail(error); }
    }

    private void OpenInspection(InspectOutfitIdentity identity)
    {
        if (world?.Identity() != identity || InspectOutfitControls.Resolve(generation) == null)
        { Stop("The inspected player changed. Reopen their context menu."); return; }
        Begin(identity);
    }

    private void Begin(InspectOutfitIdentity identity)
    {
        Stop();
        expected = identity; loginOwner = Plugin.PlayerState.ContentId;
        waiting = true; deadline = Environment.TickCount64 + 15_000;
        WaitFor("The player's inspection is being prepared.", "stage=inspection-request");
        OpenWindow?.Invoke();
    }

    private void WaitFor(string reason, string diagnostic)
    {
        StatusText = "Waiting: " + reason;
        if (waitingReason == reason && waitingDiagnostic == diagnostic) return;
        waitingReason = reason; waitingDiagnostic = diagnostic;
        log.Information("[View Item] Waiting: {Reason} Details: {Diagnostic}", reason, diagnostic);
    }

    private void Update(IFramework _)
    {
        frame++;
        if (!enabled || disposed || (!waiting && source == null)) return;
        try
        {
            if (!Ready() || Plugin.PlayerState.ContentId != loginOwner)
            { Stop("The login session changed; preview stopped."); return; }
            if (source != null)
            {
                // The captured outfit and owned preview survive Examine closing
                // or switching players. Only initial capture reads its host.
                preview!.Tick(frame);
                StatusText = preview!.StatusText;
                return;
            }
            var identity = world!.Identity();
            var host = InspectOutfitControls.Resolve(generation);
            if (Environment.TickCount64 >= deadline)
            {
                var reason = string.IsNullOrEmpty(waitingReason) ? "No ready inspection was observed." : waitingReason;
                log.Warning("[View Item] Inspection timed out. Last blocker: {Reason} Details: {Diagnostic}", reason, waitingDiagnostic);
                Stop("The inspection did not become ready. Last blocker: " + reason + " Inspect the player again and reopen View Item.");
                return;
            }
            if (identity is not { } currentIdentity)
            {
                observed = null;
                WaitFor(world!.UnavailableReason, "stage=inspection-identity");
                return;
            }
            if (identity != expected)
            {
                observed = null;
                WaitFor("The inspection is showing a different player.", "stage=inspection-identity-mismatch");
                return;
            }
            if (host == null)
            {
                observed = null;
                WaitFor("Keep the player's Examine window open and visible.", "stage=inspection-window");
                return;
            }
            var snapshot = world!.Capture(host.Value, currentIdentity);
            if (snapshot == null)
            {
                observed = null;
                WaitFor(world!.UnavailableReason, "stage=equipment-capture");
                return;
            }
            if (observed == null || !observed.SameSource(snapshot))
            {
                observed = snapshot;
                WaitFor("The inspected equipment is settling.", "stage=equipment-stabilization");
                return;
            }
            var choices = preview!.Prepare(snapshot);
            if (choices.Count == 0)
            {
                WaitFor(preview!.UnavailableReason, preview!.ReadinessDiagnostic);
                return;
            }
            source = snapshot; waiting = false; observed = null; Choices = choices;
            waitingReason = waitingDiagnostic = string.Empty;
            Select(choices[0].Key);
            SaveCapture();
        }
        catch (Exception error) { Fail(error); }
    }

    private void SaveCapture()
    {
        try { history.Save(preview!.ExportCapture()); }
        catch (Exception error)
        {
            // A storage failure must not destroy a usable captured preview.
            log.Warning(error, "[View Item] The captured outfit could not be saved to history.");
            history.ReportFailure(error);
            StatusText += " History save failed: " + error.Message;
        }
    }

    internal void OpenHistory(long id)
    {
        if (!enabled || disposed || !Ready()) return;
        ViewSingleItemHistoryEntry? entry = null;
        foreach (var candidate in history.Entries)
            if (candidate.Id == id) { entry = candidate; break; }
        if (entry == null) return;
        if (entry.Capture == null)
        { StatusText = "Cannot open this saved outfit: " + entry.Error; return; }
        try
        {
            entry.Capture.Validate();
            Stop();
            loginOwner = Plugin.PlayerState.ContentId;
            source = entry.Capture.Snapshot();
            Choices = preview!.Restore(entry.Capture, source);
            Select(Choices[0].Key);
            OpenWindow?.Invoke();
        }
        catch (Exception error) { Fail(error); }
    }

    internal void Select(int key)
    {
        if (!enabled || disposed || source == null || !Ready()) return;
        try
        {
            if (Plugin.PlayerState.ContentId != loginOwner)
            { Stop("The login session changed; preview stopped."); return; }
            if (preview!.Select(key, source))
            {
                SelectedKey = key;
                SelectionRevision++;
                StatusText = preview!.StatusText;
            }
            else
            {
                SelectedKey = null;
                StatusText = preview!.StatusText;
            }
        }
        catch (Exception error) { Fail(error); }
    }

    internal void Stop(string reason = "Preview stopped.")
    {
        generation++; waiting = false; observed = source = null; expected = null;
        waitingReason = waitingDiagnostic = string.Empty;
        Choices = Array.Empty<ViewSingleItemChoice>(); SelectedKey = null; loginOwner = 0;
        try { preview?.Clear(); }
        catch (Exception error) { log.Warning(error, "[View Item] Preview cleanup failed."); }
        StatusText = reason;
    }

    private void Fail(Exception error)
    {
        Stop("View Item unavailable: " + error.Message);
        log.Warning(error, "[View Item] Request stopped.");
    }

    internal void ReportPreviewFailure(Exception error) => Fail(error);

    public void Dispose()
    {
        if (disposed) return;
        SetEnabled(false); disposed = true; OpenWindow = null;
        history.Dispose();
    }
}
