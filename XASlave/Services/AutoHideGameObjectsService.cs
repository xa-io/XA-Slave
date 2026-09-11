using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using ClassJob = Lumina.Excel.Sheets.ClassJob;
using IPlayerCharacter = Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter;
using ObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;

namespace XASlave.Services;

public unsafe sealed class AutoHideGameObjectsService : IDisposable
{
    private const int HiddenRenderFlag = 256;

    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly ITargetManager targetManager;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;
    private readonly Dictionary<nint, uint> processedObjects = [];
    private readonly Dictionary<uint, uint> visiblePlayerJobs = [];

    private Hook<UpdateObjectArraysDelegate>? updateObjectArraysHook;

    private bool initialized;
    private bool enabled;
    private bool detourFaulted;
    private bool subscribed;
    private bool hidePlayer = true;
    private bool hideFriends;
    private bool hidePartyAllianceMembers;
    private bool hideUnimportantEnpc = true;
    private bool hidePet = true;
    private bool hideChocobo = true;
    private bool hideBeasts;
    private bool hideOwnBeast;
    private bool hideFashionAccessories;
    private uint beastmasterJobId;
    private long nextBeastmasterLookup;
    private bool disableInDuties = true;
    private bool disableInIslandSanctuary = true;
    private bool useOccultCrescentRules = true;
    private int zoneRefreshPassesRemaining;

    public AutoHideGameObjectsService(
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        ITargetManager targetManager,
        ISigScanner sigScanner,
        IGameInteropProvider interopProvider,
        IPluginLog log)
    {
        this.framework = framework;
        this.clientState = clientState;
        this.condition = condition;
        this.targetManager = targetManager;
        this.sigScanner = sigScanner;
        this.interopProvider = interopProvider;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";

    public void ApplyConfiguration(
        bool hidePlayer,
        bool hideUnimportantEnpc,
        bool hidePet,
        bool hideChocobo,
        bool disableInDuties,
        bool disableInIslandSanctuary,
        bool useOccultCrescentRules,
        bool hideBeasts = false,
        bool hideFashionAccessories = false,
        bool hideOwnBeast = false,
        bool hideFriends = false,
        bool hidePartyAllianceMembers = false)
    {
        this.hidePlayer = hidePlayer;
        this.hideFriends = hideFriends;
        this.hidePartyAllianceMembers = hidePartyAllianceMembers;
        this.hideUnimportantEnpc = hideUnimportantEnpc;
        this.hidePet = hidePet;
        this.hideChocobo = hideChocobo;
        this.hideBeasts = hideBeasts;
        this.hideOwnBeast = hideOwnBeast;
        this.hideFashionAccessories = hideFashionAccessories;
        this.disableInDuties = disableInDuties;
        this.disableInIslandSanctuary = disableInIslandSanctuary;
        this.useOccultCrescentRules = useOccultCrescentRules;

        if (!enabled)
            return;

        // Restore before changing the scan range, including beasts/accessories outside the legacy slots.
        ResetAllObjects();
        zoneRefreshPassesRemaining = 3;
        UpdateAllObjects(GameObjectManager.Instance());
        StatusText = GetEnabledStatusText();
    }

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            UpdateHookState();
            UpdateSubscriptions(false);
            ResetAllObjects();
            StatusText = "Disabled";
            return false;
        }

        EnsureInitialized();
        if (updateObjectArraysHook == null)
        {
            StatusText = "Unavailable - object update hook missing.";
            return false;
        }

        enabled = true;
        zoneRefreshPassesRemaining = 3;
        UpdateHookState();
        UpdateSubscriptions(true);
        UpdateAllObjects(GameObjectManager.Instance());
        StatusText = GetEnabledStatusText();
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        UpdateHookState();
        UpdateSubscriptions(false);
        ResetAllObjects();
        DisposeHook(ref updateObjectArraysHook);
    }

    private string GetEnabledStatusText()
    {
        var categories = new List<string>(9);
        if (hideFriends)
            categories.Add("friends");
        if (hidePartyAllianceMembers)
            categories.Add("party and alliance members");
        if (hidePlayer)
            categories.Add("non-friends");
        if (hideUnimportantEnpc)
            categories.Add("unimportant NPCs");
        if (hidePet)
            categories.Add("pets");
        if (hideChocobo)
            categories.Add("chocobos");
        if (hideBeasts)
            categories.Add(beastmasterJobId != 0 ? "other players' beasts" : "beasts (waiting for BST game data)");
        if (hideOwnBeast)
            categories.Add(beastmasterJobId != 0 ? "your beast" : "your beast (waiting for BST game data)");
        if (hideFashionAccessories)
            categories.Add("fashion accessories");

        if (categories.Count == 0)
            return "Enabled - hook is armed, but no hide categories are selected.";

        var protections = new List<string>(3);
        if (disableInDuties)
            protections.Add("duty guard");
        if (disableInIslandSanctuary)
            protections.Add("island guard");
        if (useOccultCrescentRules)
            protections.Add("Occult Crescent rules");

        return protections.Count > 0
            ? $"Enabled - hiding {string.Join(", ", categories)} locally with {string.Join(", ", protections)}."
            : $"Enabled - hiding {string.Join(", ", categories)} locally.";
    }

    private void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = true;

        try
        {
            if (!sigScanner.TryScanText(Sigs.UpdateObjectArraysSig, out var address) || address == nint.Zero)
                return;

            updateObjectArraysHook = interopProvider.HookFromAddress<UpdateObjectArraysDelegate>(address, UpdateObjectArraysDetour);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Failed to create AutoHideGameObjects hook.");
        }
    }

    private void UpdateHookState()
    {
        ToggleHook(updateObjectArraysHook, enabled, "AutoHideGameObjects");
    }

    private void UpdateSubscriptions(bool targetEnabled)
    {
        if (subscribed == targetEnabled)
            return;

        if (targetEnabled)
        {
            framework.Update += OnFrameworkUpdate;
            clientState.TerritoryChanged += OnTerritoryChanged;
        }
        else
        {
            framework.Update -= OnFrameworkUpdate;
            clientState.TerritoryChanged -= OnTerritoryChanged;
        }

        subscribed = targetEnabled;
    }

    private void ToggleHook<T>(Hook<T>? hook, bool targetEnabled, string label)
        where T : Delegate
    {
        if (hook == null || hook.IsDisposed)
            return;

        try
        {
            if (targetEnabled)
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
            log.Warning(ex, $"[XASlave] Failed to {(targetEnabled ? "enable" : "disable")} {label} hook.");
        }
    }

    private static void DisposeHook<T>(ref Hook<T>? hook)
        where T : Delegate
    {
        if (hook is { IsDisposed: false })
            hook.Dispose();

        hook = null;
    }

    private void* UpdateObjectArraysDetour(GameObjectManager* objectManager)
    {
        void* original = null;
        if (updateObjectArraysHook != null)
            original = updateObjectArraysHook.OriginalDisposeSafe(objectManager);

        if (enabled)
        {
            try
            {
                UpdateAllObjects(objectManager);
            }
            catch (Exception ex)
            {
                // This detour runs inside the game's native object-array update. A managed
                // exception escaping into the native caller would crash the client, so swallow it
                // and keep the original result. Log only once to avoid per-tick log spam.
                if (!detourFaulted)
                {
                    detourFaulted = true;
                    log.Error(ex, "[XASlave] AutoHideGameObjects object update faulted; suppressing further updates' errors this session.");
                }
            }
        }

        return original;
    }

    private void OnTerritoryChanged(uint _)
    {
        ResetAllObjects();
        zoneRefreshPassesRemaining = 3;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (!enabled)
            return;

        if (!CanProcessObjects())
        {
            ResetAllObjects();
            return;
        }

        if (zoneRefreshPassesRemaining <= 0)
            return;

        zoneRefreshPassesRemaining--;
        UpdateAllObjects(GameObjectManager.Instance());
    }

    private bool CanProcessObjects()
    {
        if (!clientState.IsLoggedIn || clientState.IsPvPExcludingDen)
            return false;

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
            return false;

        var gameMain = GameMain.Instance();
        if (gameMain == null)
            return false;

        if (disableInDuties && gameMain->CurrentContentFinderConditionId != 0)
            return false;

        if (disableInIslandSanctuary && gameMain->CurrentTerritoryIntendedUseId == TerritoryIntendedUse.IslandSanctuary)
            return false;

        if (useOccultCrescentRules
            && gameMain->CurrentTerritoryIntendedUseId == TerritoryIntendedUse.OccultCrescent
            && (Plugin.ObjectTable.LocalPlayer?.Position.Y ?? -100f) < 0f)
            return false;

        return true;
    }

    private void UpdateAllObjects(GameObjectManager* manager)
    {
        if (manager == null)
            return;

        if (!CanProcessObjects())
            return;

        var gameMain = GameMain.Instance();
        var useOccultCrescentFilter = useOccultCrescentRules
            && gameMain != null
            && gameMain->CurrentTerritoryIntendedUseId == TerritoryIntendedUse.OccultCrescent;
        var targetAddress = targetManager.Target?.Address ?? nint.Zero;
        var playerCount = 0;
        RefreshBeastOwners();

        for (var index = 0; index < manager->Objects.IndexSorted.Length; index++)
        {
            if (!hideBeasts && !hideOwnBeast && !hideFashionAccessories && index > 629)
                break;

            if (!hideBeasts && !hideOwnBeast && !hideFashionAccessories && index is > 200 and < 489)
            {
                index = 488;
                continue;
            }

            var gameObject = manager->Objects.IndexSorted[index].Value;
            if (gameObject == null)
                continue;

            var address = (nint)gameObject;
            // A reused table slot is a different actor; never restore its visibility for the old one.
            if (processedObjects.TryGetValue(address, out var previousEntityId) && previousEntityId != gameObject->EntityId)
                processedObjects.Remove(address);

            if ((useOccultCrescentFilter
                    ? ShouldHideOccultCrescent(gameObject, targetAddress, ref playerCount, (uint)index)
                    : ShouldHide(gameObject, (uint)index)))
            {
                gameObject->RenderFlags |= (VisibilityFlags)HiddenRenderFlag;
                processedObjects[address] = gameObject->EntityId;
            }
            else if (processedObjects.Remove(address))
            {
                gameObject->RenderFlags &= ~(VisibilityFlags)HiddenRenderFlag;
            }
        }
    }

    private void RefreshBeastOwners()
    {
        visiblePlayerJobs.Clear();
        if (!hideBeasts && !hideOwnBeast)
            return;

        if (beastmasterJobId == 0 && Environment.TickCount64 >= nextBeastmasterLookup)
        {
            nextBeastmasterLookup = Environment.TickCount64 + 5000;
            try
            {
                foreach (var job in Plugin.DataManager.GetExcelSheet<ClassJob>())
                {
                    if (job.RowId == 0 || !string.Equals(job.Abbreviation.ToString(), "BST", StringComparison.OrdinalIgnoreCase))
                        continue;

                    beastmasterJobId = job.RowId;
                    StatusText = GetEnabledStatusText();
                    break;
                }
            }
            catch (Exception ex)
            {
                log.Debug(ex, "[XASlave] Hide Beasts is waiting for ClassJob game data.");
            }
        }

        if (beastmasterJobId == 0)
            return;

        // One owner snapshot per existing object-update pass; no per-pet table searches.
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is IPlayerCharacter player)
                visiblePlayerJobs[player.EntityId] = player.ClassJob.RowId;
        }
    }

    private bool ShouldHideBeast(GameObject* gameObject, uint localEntityId)
    {
        return beastmasterJobId != 0
            && gameObject->ObjectKind == ObjectKind.BattleNpc
            && (BattleNpcSubKind)gameObject->SubKind == BattleNpcSubKind.Pet
            && (gameObject->OwnerId == localEntityId ? hideOwnBeast : hideBeasts)
            && gameObject->OwnerId != 0
            && gameObject->OwnerId != 0xE0000000
            && visiblePlayerJobs.TryGetValue(gameObject->OwnerId, out var ownerJobId)
            && ownerJobId == beastmasterJobId;
    }

    private bool ShouldHidePlayer(BattleChara* player)
    {
        // Group membership takes priority when a party/alliance member is also a friend.
        if (player->IsPartyMember || player->IsAllianceMember)
            return hidePartyAllianceMembers;

        if (player->IsFriend)
            return hideFriends;

        return hidePlayer;
    }

    private bool ShouldHide(GameObject* gameObject, uint index)
    {
        if (gameObject == null)
            return false;

        var playerState = PlayerState.Instance();
        if (playerState == null)
            return false;

        var localEntityId = playerState->EntityId;
        if (localEntityId == 0)
            return false;

        if ((((uint)gameObject->RenderFlags) & HiddenRenderFlag) != 0 && !processedObjects.ContainsKey((nint)gameObject))
            return false;

        if (gameObject->NamePlateIconId != 0)
            return false;

        // Apply the ornament switch directly, including local accessories with no usable owner.
        if (gameObject->ObjectKind == ObjectKind.Ornament)
            return hideFashionAccessories;

        if (gameObject->EntityId == localEntityId)
            return false;

        if (ShouldHideBeast(gameObject, localEntityId))
            return true;

        if (index <= 200
            && index % 2 == 0
            && gameObject->ObjectKind == ObjectKind.Pc)
            return ShouldHidePlayer((BattleChara*)gameObject);

        if (hidePet
            && index <= 200
            && index % 2 == 1
            && (gameObject->ObjectKind == ObjectKind.Companion
                || (gameObject->ObjectKind == ObjectKind.BattleNpc
                    && (BattleNpcSubKind)gameObject->SubKind == BattleNpcSubKind.Pet))
            && gameObject->OwnerId != localEntityId)
            return true;

        if (hideChocobo
            && index <= 200
            && index % 2 == 0
            && gameObject->ObjectKind == ObjectKind.BattleNpc
            && (BattleNpcSubKind)gameObject->SubKind == BattleNpcSubKind.Buddy
            && gameObject->OwnerId != localEntityId)
            return true;

        return hideUnimportantEnpc
            && index is >= 489 and <= 629
            && (gameObject->TargetableStatus & ObjectTargetableFlags.IsTargetable) == 0
            && gameObject->EventHandler == null;
    }

    private bool ShouldHideOccultCrescent(GameObject* gameObject, nint targetAddress, ref int playerCount, uint index)
    {
        if (gameObject == null)
            return false;

        var playerState = PlayerState.Instance();
        if (playerState == null)
            return false;

        var localEntityId = playerState->EntityId;
        if (localEntityId == 0)
            return false;

        if ((((uint)gameObject->RenderFlags) & HiddenRenderFlag) != 0 && !processedObjects.ContainsKey((nint)gameObject))
            return false;

        if (gameObject->NamePlateIconId != 0)
            return false;

        if (gameObject->ObjectKind == ObjectKind.Ornament)
            return hideFashionAccessories;

        if (gameObject->EntityId == localEntityId)
            return false;

        if (ShouldHideBeast(gameObject, localEntityId))
            return true;

        if (index <= 200 && index % 2 == 0 && gameObject->ObjectKind == ObjectKind.Pc)
        {
            var player = (BattleChara*)gameObject;
            playerCount++;

            if (player->IsDead() || (nint)gameObject == targetAddress)
                return false;

            return playerCount >= 10 && ShouldHidePlayer(player);
        }

        if (hideUnimportantEnpc
            && index is >= 489 and <= 629
            && (gameObject->TargetableStatus & ObjectTargetableFlags.IsTargetable) == 0
            && gameObject->EventHandler == null)
            return true;

        return gameObject->ObjectKind == ObjectKind.BattleNpc
            && index <= 200
            && index % 2 == 0
            && gameObject->OwnerId != localEntityId
            && gameObject->OwnerId != 0
            && gameObject->OwnerId != 0xE0000000;
    }

    private void ResetAllObjects()
    {
        visiblePlayerJobs.Clear();
        if (processedObjects.Count == 0)
            return;

        var manager = GameObjectManager.Instance();
        if (manager == null)
        {
            processedObjects.Clear();
            return;
        }

        foreach (ref var entry in manager->Objects.IndexSorted)
        {
            if (entry.Value == null)
                continue;

            var address = (nint)entry.Value;
            if (!processedObjects.TryGetValue(address, out var entityId) || entityId != entry.Value->EntityId)
                continue;

            entry.Value->RenderFlags &= ~(VisibilityFlags)HiddenRenderFlag;
        }

        processedObjects.Clear();
    }

    private delegate void* UpdateObjectArraysDelegate(GameObjectManager* objectManager);
}
