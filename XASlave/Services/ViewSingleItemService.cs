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

internal sealed record ViewSingleItemChoice(int Key, string Label, uint ItemId, string UnavailableReason = "")
{
    private sealed record Presentation(global::XASlave.Localization.UiMessage? LabelMessage, global::XASlave.Localization.UiMessage? UnavailableMessage);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ViewSingleItemChoice, Presentation> Presentations = new();
    private static readonly Presentation EmptyPresentation = new(null, null);
    private Presentation Messages => Presentations.TryGetValue(this, out var value) ? value : EmptyPresentation;
    internal global::XASlave.Localization.UiMessage? LabelMessage
    {
        get => Messages.LabelMessage;
        init => Presentations.AddOrUpdate(this, Messages with { LabelMessage = value });
    }
    internal global::XASlave.Localization.UiMessage? UnavailableMessage
    {
        get => Messages.UnavailableMessage;
        init => Presentations.AddOrUpdate(this, Messages with { UnavailableMessage = value });
    }
    private ViewSingleItemChoice(ViewSingleItemChoice original)
    {
        Key = original.Key;
        Label = original.Label;
        ItemId = original.ItemId;
        UnavailableReason = original.UnavailableReason;
        if (Presentations.TryGetValue(original, out var presentation)) Presentations.Add(this, presentation);
    }
    internal string LocalizedLabel => global::XASlave.Localization.UiMessage.Resolve(LabelMessage, Label);
    internal global::XASlave.Localization.UiMessage UnavailablePresentation => global::XASlave.Localization.UiMessage.Capture(UnavailableMessage, UnavailableReason);
    internal string LocalizedUnavailableReason => UnavailablePresentation.Display;
    internal static ViewSingleItemChoice Create(int key, global::XASlave.Localization.UiMessage label, uint itemId, global::XASlave.Localization.UiMessage? unavailable = null)
        => new(key, label.Original, itemId, unavailable?.Original ?? string.Empty) { LabelMessage = label, UnavailableMessage = unavailable };
}

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
    private global::XASlave.Localization.UiMessage? statusMessage = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? waitingMessage;
    internal global::XASlave.Localization.UiMessage StatusMessage => global::XASlave.Localization.UiMessage.Capture(statusMessage, StatusText);
    internal string LocalizedStatusText => StatusMessage.Display;
    internal string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    internal string PlayerName => source?.Identity.Name ?? expected?.Name ?? string.Empty;
    internal IReadOnlyList<ViewSingleItemChoice> Choices { get; private set; } = Array.Empty<ViewSingleItemChoice>();
    internal int? SelectedKey { get; private set; }
    internal long SelectionRevision { get; private set; }
    internal uint? FramerKitId => preview?.FramerKitId;
    internal bool IsEnabled => enabled;
    internal ViewSingleItemPreview? Preview => source == null ? null : preview;
    internal IReadOnlyList<ViewSingleItemHistoryEntry> History => history.Entries;
    internal string HistoryStatus => history.Status;
    internal string LocalizedHistoryStatus => history.LocalizedStatus;
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
                StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Format("S5367AFABD2DAF74F" , global::XASlave.Localization.UiMessage.FromException(error)));
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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Key("SD33B23E63E47561D"));
            history.Load();
        }
        else
        {
            enabled = false; Stop(global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
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

    private void OnLogout(int _, int __) => Stop(global::XASlave.Localization.UiMessage.Key("S355320765FC328AC"));
    private void OnTerritoryChanged(uint _) => Stop(global::XASlave.Localization.UiMessage.Key("SD39607D68B12483F"));

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
                    Name = new SeStringBuilder().AddText(global::XASlave.Localization.UiLocale.Get("S26AFC61257A5D865")).Build(),
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
                Name = new SeStringBuilder().AddText(global::XASlave.Localization.UiLocale.Get("S26AFC61257A5D865")).Build(),
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
            if (!ViewSingleItemShopSource.TryDescribeMessage(itemId, out var entry, out var reason))
            { Stop(global::XASlave.Localization.UiMessage.Format("SA47B395A131DAB32" , reason)); OpenWindow?.Invoke(); return; }
            var player = Plugin.ObjectTable.LocalPlayer;
            if (player == null) { Stop(global::XASlave.Localization.UiMessage.Key("SE37A60441BE9928F")); return; }
            var address = player.Address;
            var entity = player.EntityId;
            var owner = Plugin.PlayerState.ContentId;
            Stop(); loginOwner = owner;
            var capture = preview.PrepareCatalog(entry, (NativeCharacter*)address);
            var confirmed = Plugin.ObjectTable.LocalPlayer;
            if (!Ready() || Plugin.PlayerState.ContentId != owner || confirmed == null
                || confirmed.Address != address || confirmed.EntityId != entity)
            { Stop(global::XASlave.Localization.UiMessage.Key("SF06DD23D35E3FA40")); return; }
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
        var value = CaptureOrnamentMessage(identity, out var presentation);
        unavailable = presentation.Original;
        return value;
    }

    internal static unsafe ViewSingleItemOrnament? CaptureOrnamentMessage(InspectOutfitIdentity identity, out global::XASlave.Localization.UiMessage unavailable)
    {
        NearbyZoneNativeBinding.RequireFramework();
        unavailable = global::XASlave.Localization.UiMessage.Raw(string.Empty);
        try
        {
            // Examine can work for a player who is not nearby. Accessory capture
            // requires a unique, currently loaded match and never retains it.
            var player = ResolvePlayer(identity.Entity, identity.Name, identity.World);
            if (player == null || !identity.IsPlayer || player.EntityId != identity.Entity)
            {
                unavailable = global::XASlave.Localization.UiMessage.Key("SCE6DD9BEFCD07F14");
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
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SEF690E09DB62BE76"), originalMessageText => new InvalidOperationException(originalMessageText));
            var id = unchecked((ushort)Marshal.ReadInt16(address + 0x978));
            var ornamentObject = Marshal.ReadIntPtr(address + 0x970);
            ushort modelId = 0;
            byte modelType = 0;
            if (id != 0)
            {
                if (ornamentObject == 0)
                    throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S11350D5EE4E56D60"), originalMessageText => new InvalidOperationException(originalMessageText));
                NearbyZoneNativeBinding.RequireReadable(ornamentObject + 0x2370, sizeof(uint));
                if (unchecked((uint)Marshal.ReadInt32(ornamentObject + 0x2370)) != id)
                    throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S54B7D5CA00D203B3"), originalMessageText => new InvalidOperationException(originalMessageText));
                var row = Plugin.DataManager.GetExcelSheet<Ornament>().GetRowOrDefault(id);
                if (row == null || row.Value.Model == 0)
                    throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SB1B518338C789334"), originalMessageText => new InvalidOperationException(originalMessageText));
                modelId = row.Value.Model;
                var model = Plugin.DataManager.GetExcelSheet<ModelChara>().GetRowOrDefault(modelId);
                if (model == null || model.Value.Type is not (2 or 3))
                    throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SD805486CB3F1E03C"), originalMessageText => new InvalidOperationException(originalMessageText));
                modelType = model.Value.Type;
                if (!OrnamentModelReady(address, ornamentObject, modelId, modelType))
                    throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S59FCDF6ED95D0446"), originalMessageText => new InvalidOperationException(originalMessageText));
            }
            else if (ornamentObject != 0)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S223CFC9EA21BB4A2"), originalMessageText => new InvalidOperationException(originalMessageText));

            var confirmed = ResolvePlayer(objectId, identity.Name, identity.World);
            if (confirmed == null || confirmed.Address != address || confirmed.EntityId != entityId
                || Marshal.ReadIntPtr(address + 0x968) != address
                || unchecked((ushort)Marshal.ReadInt16(address + 0x978)) != id
                || Marshal.ReadIntPtr(address + 0x970) != ornamentObject)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S9E3B16D5E1894FE0"), originalMessageText => new InvalidOperationException(originalMessageText));
            if (id != 0)
            {
                NearbyZoneNativeBinding.RequireReadable(ornamentObject + 0x2370, sizeof(uint));
                if (unchecked((uint)Marshal.ReadInt32(ornamentObject + 0x2370)) != id
                    || !OrnamentModelReady(address, ornamentObject, modelId, modelType))
                    throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S6025BE1657345084"), originalMessageText => new InvalidOperationException(originalMessageText));
            }
            Plugin.Log.Information("[View Item] Fashion accessory captured: entity={Entity}; ornament={Ornament}; model={Model}; source=loaded-player.",
                entityId, id, modelId);
            return new(id, modelId);
        }
        catch (Exception error)
        {
            unavailable = global::XASlave.Localization.UiMessage.Format("S18EB1FC06B569F92" , global::XASlave.Localization.UiMessage.FromException(error));
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
            { Stop(global::XASlave.Localization.UiMessage.Key("S16CA952DFAC6010F")); return; }
            Begin(new(player.EntityId, checked((ushort)player.World), player.Name, 4));
            var agent = AgentInspect.Instance();
            if (agent == null || AgentInspect.MemberFunctionPointers.ExamineCharacter == null)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S2A3AC738955C09BA"), originalMessageText => new InvalidOperationException(originalMessageText));
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
            { Stop(global::XASlave.Localization.UiMessage.Key("SF9D9E5742F35AEB4")); OpenWindow?.Invoke(); return; }
            OpenInspection(identity.Value);
        }
        catch (Exception error) { Fail(error); }
    }

    private void OpenInspection(InspectOutfitIdentity identity)
    {
        if (world?.Identity() != identity || InspectOutfitControls.Resolve(generation) == null)
        { Stop(global::XASlave.Localization.UiMessage.Key("SE936556C289688DA")); return; }
        Begin(identity);
    }

    private void Begin(InspectOutfitIdentity identity)
    {
        Stop();
        expected = identity; loginOwner = Plugin.PlayerState.ContentId;
        waiting = true; deadline = Environment.TickCount64 + 15_000;
        WaitFor(global::XASlave.Localization.UiMessage.Key("S53247835EFD3490F"), "stage=inspection-request");
        OpenWindow?.Invoke();
    }

    private void WaitFor(global::XASlave.Localization.UiMessage reason, string diagnostic)
    {
        StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Format("SA049806B28DC9897" , reason));
        if (waitingReason == reason.Original && waitingDiagnostic == diagnostic) return;
        waitingReason = global::XASlave.Localization.UiMessage.Store(ref waitingMessage, reason); waitingDiagnostic = diagnostic;
        log.Information("[View Item] Waiting: {Reason} Details: {Diagnostic}", reason.Original, diagnostic);
    }

    private void Update(IFramework _)
    {
        frame++;
        if (!enabled || disposed || (!waiting && source == null)) return;
        try
        {
            if (!Ready() || Plugin.PlayerState.ContentId != loginOwner)
            { Stop(global::XASlave.Localization.UiMessage.Key("S9205CE38988B349F")); return; }
            if (source != null)
            {
                // The captured outfit and owned preview survive Examine closing
                // or switching players. Only initial capture reads its host.
                preview!.Tick(frame);
                StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, preview!.StatusMessage);
                return;
            }
            var identity = world!.Identity();
            var host = InspectOutfitControls.Resolve(generation);
            if (Environment.TickCount64 >= deadline)
            {
                var reason = string.IsNullOrEmpty(waitingReason) ? global::XASlave.Localization.UiMessage.Key("SF892E21C3E615CDB") : global::XASlave.Localization.UiMessage.Capture(waitingMessage, waitingReason);
                log.Warning("[View Item] Inspection timed out. Last blocker: {Reason} Details: {Diagnostic}", reason.Original, waitingDiagnostic);
                Stop(global::XASlave.Localization.UiMessage.Format("SBB7C8A3E082D3DEE" , reason));
                return;
            }
            if (identity is not { } currentIdentity)
            {
                observed = null;
                WaitFor(world!.UnavailableMessage, "stage=inspection-identity");
                return;
            }
            if (identity != expected)
            {
                observed = null;
                WaitFor(global::XASlave.Localization.UiMessage.Key("SBFE9FFAA78DD021F"), "stage=inspection-identity-mismatch");
                return;
            }
            if (host == null)
            {
                observed = null;
                WaitFor(global::XASlave.Localization.UiMessage.Key("S40022DC7FF8BEE0F"), "stage=inspection-window");
                return;
            }
            var snapshot = world!.Capture(host.Value, currentIdentity);
            if (snapshot == null)
            {
                observed = null;
                WaitFor(world!.UnavailableMessage, "stage=equipment-capture");
                return;
            }
            if (observed == null || !observed.SameSource(snapshot))
            {
                observed = snapshot;
                WaitFor(global::XASlave.Localization.UiMessage.Key("S82B4E59D1E596BF1"), "stage=equipment-stabilization");
                return;
            }
            var choices = preview!.Prepare(snapshot);
            if (choices.Count == 0)
            {
                WaitFor(preview!.UnavailableMessage, preview!.ReadinessDiagnostic);
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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Format("S8391DA809DD9CA6B" , StatusMessage, global::XASlave.Localization.UiMessage.Format("S9E4F847663D09B5F" , global::XASlave.Localization.UiMessage.FromException(error))));
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
        { StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, global::XASlave.Localization.UiMessage.Format("S936FA011B2E3075B" , entry.ErrorPresentation)); return; }
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
            { Stop(global::XASlave.Localization.UiMessage.Key("S9205CE38988B349F")); return; }
            if (preview!.Select(key, source))
            {
                SelectedKey = key;
                SelectionRevision++;
                StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, preview!.StatusMessage);
            }
            else
            {
                SelectedKey = null;
                StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, preview!.StatusMessage);
            }
        }
        catch (Exception error) { Fail(error); }
    }

    internal void Stop() => Stop(global::XASlave.Localization.UiMessage.Key("SB8E649EB36E4EDC5"));
    internal void Stop(string reason) => Stop(global::XASlave.Localization.UiMessage.Raw(reason));
    private void Stop(global::XASlave.Localization.UiMessage reason)
    {
        generation++; waiting = false; observed = source = null; expected = null;
        waitingReason = waitingDiagnostic = string.Empty;
        Choices = Array.Empty<ViewSingleItemChoice>(); SelectedKey = null; loginOwner = 0;
        try { preview?.Clear(); }
        catch (Exception error) { log.Warning(error, "[View Item] Preview cleanup failed."); }
        StatusText = global::XASlave.Localization.UiMessage.Store(ref statusMessage, reason);
    }

    private void Fail(Exception error)
    {
        Stop(global::XASlave.Localization.UiMessage.Format("S3EB902118C9BA996" , global::XASlave.Localization.UiMessage.FromException(error)));
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
