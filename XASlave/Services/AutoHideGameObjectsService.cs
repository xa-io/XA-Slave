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
    internal const float DefaultSanctuaryDistance = 10f;
    internal const float DefaultMaxDistance = 50f;
    internal const float MinimumPlayerDistance = 1f;
    internal const float MaximumPlayerDistance = 300f;

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
    private bool usePlayerDistance;
    private bool honorSanctuaryMinimums;
    private float sanctuaryDistance = DefaultSanctuaryDistance;
    private float maxDistance = DefaultMaxDistance;
    private float activePlayerDistance = DefaultMaxDistance;
    private System.Numerics.Vector3? playerDistanceOrigin;
    private long nextDistanceRefresh;
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

    public string StatusText { get; private set; } = global::XASlave.Localization.UiLocale.Native("S75081B593D15CF6E");
    private global::XASlave.Localization.UiMessage? uiStatusText = global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E");
    internal string LocalizedStatusText => global::XASlave.Localization.UiMessage.Resolve(ref uiStatusText, StatusText);
    internal global::XASlave.Localization.UiMessage StatusTextMessage => global::XASlave.Localization.UiMessage.Capture(ref uiStatusText, StatusText);

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
        bool hidePartyAllianceMembers = false,
        bool usePlayerDistance = false,
        bool honorSanctuaryMinimums = false,
        float sanctuaryDistance = DefaultSanctuaryDistance,
        float maxDistance = DefaultMaxDistance)
    {
        this.hidePlayer = hidePlayer;
        this.hideFriends = hideFriends;
        this.hidePartyAllianceMembers = hidePartyAllianceMembers;
        NormalizePlayerDistances(ref sanctuaryDistance, ref maxDistance);
        this.usePlayerDistance = usePlayerDistance;
        this.honorSanctuaryMinimums = honorSanctuaryMinimums;
        this.sanctuaryDistance = sanctuaryDistance;
        this.maxDistance = maxDistance;
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
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, GetEnabledStatusText());
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
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S75081B593D15CF6E"));
            return false;
        }

        EnsureInitialized();
        if (updateObjectArraysHook == null)
        {
            StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, global::XASlave.Localization.UiMessage.Key("S2764068E098B9677"));
            return false;
        }

        enabled = true;
        zoneRefreshPassesRemaining = 3;
        UpdateHookState();
        UpdateSubscriptions(true);
        UpdateAllObjects(GameObjectManager.Instance());
        StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, GetEnabledStatusText());
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

    private global::XASlave.Localization.UiMessage GetEnabledStatusText()
    {
        var categories = new List<global::XASlave.Localization.UiMessage>(9);
        if (hideFriends)
            categories.Add(global::XASlave.Localization.UiMessage.Key("S2E4B9CC2428BEB7B"));
        if (hidePartyAllianceMembers)
            categories.Add(global::XASlave.Localization.UiMessage.Key("S38E5BB99EABDAE7B"));
        if (hidePlayer)
            categories.Add(global::XASlave.Localization.UiMessage.Key("S0018B9E22FDDF3CB"));
        if (hideUnimportantEnpc)
            categories.Add(global::XASlave.Localization.UiMessage.Key("S4971327C9B99F06D"));
        if (hidePet)
            categories.Add(global::XASlave.Localization.UiMessage.Key("S3BE1D422A46085E1"));
        if (hideChocobo)
            categories.Add(global::XASlave.Localization.UiMessage.Key("S9B469817E8601DCD"));
        if (hideBeasts)
            categories.Add((beastmasterJobId != 0 ? global::XASlave.Localization.UiMessage.Key("S2C58A3188494004D") : global::XASlave.Localization.UiMessage.Key("SBAB2E68D1A8F35C4")));
        if (hideOwnBeast)
            categories.Add((beastmasterJobId != 0 ? global::XASlave.Localization.UiMessage.Key("S76D9893B13CCEC25") : global::XASlave.Localization.UiMessage.Key("S31ABBED1442378D9")));
        if (hideFashionAccessories)
            categories.Add(global::XASlave.Localization.UiMessage.Key("SD8771D84DDB60932"));

        if (categories.Count == 0)
            return global::XASlave.Localization.UiMessage.Key("S742500AAD5DC6823");

        var protections = new List<global::XASlave.Localization.UiMessage>(3);
        if (disableInDuties)
            protections.Add(global::XASlave.Localization.UiMessage.Key("S7644693902D409AD"));
        if (disableInIslandSanctuary)
            protections.Add(global::XASlave.Localization.UiMessage.Key("S109D7293A44B2C0F"));
        if (useOccultCrescentRules)
            protections.Add(global::XASlave.Localization.UiMessage.Key("S0C118C5B4214893A"));

        var status = (protections.Count > 0 ? global::XASlave.Localization.UiMessage.Format("SAE3A617D2BF45BE5", global::XASlave.Localization.UiMessage.Join(", ", categories), global::XASlave.Localization.UiMessage.Join(", ", protections)) : global::XASlave.Localization.UiMessage.Format("SED9F1A9AAF5640F2", global::XASlave.Localization.UiMessage.Join(", ", categories)));
        if (usePlayerDistance && (hidePlayer || hideFriends || hidePartyAllianceMembers))
            status = global::XASlave.Localization.UiMessage.Concat(status, (honorSanctuaryMinimums ? global::XASlave.Localization.UiMessage.Format("S5657DDE279332879", sanctuaryDistance, maxDistance) : global::XASlave.Localization.UiMessage.Format("SDBD9B1315AD25D6A", maxDistance)));
        return status;
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

        // The native hook normally refreshes movement. Only scan here if it has gone quiet.
        if (zoneRefreshPassesRemaining <= 0 && (!usePlayerDistance || Environment.TickCount64 < nextDistanceRefresh))
            return;

        if (zoneRefreshPassesRemaining > 0)
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
        nextDistanceRefresh = Environment.TickCount64 + 250;
        RefreshPlayerDistanceState();
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
                    StatusText = global::XASlave.Localization.UiMessage.Store(ref uiStatusText, GetEnabledStatusText());
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

    internal static void NormalizePlayerDistances(ref float sanctuaryDistance, ref float maxDistance)
    {
        maxDistance = float.IsFinite(maxDistance)
            ? Math.Clamp(maxDistance, MinimumPlayerDistance, MaximumPlayerDistance)
            : DefaultMaxDistance;
        sanctuaryDistance = float.IsFinite(sanctuaryDistance)
            ? Math.Clamp(sanctuaryDistance, MinimumPlayerDistance, maxDistance)
            : Math.Min(DefaultSanctuaryDistance, maxDistance);
    }

    private void RefreshPlayerDistanceState()
    {
        playerDistanceOrigin = null;
        if (!usePlayerDistance)
            return;

        playerDistanceOrigin = Plugin.ObjectTable.LocalPlayer?.Position;
        var territoryInfo = TerritoryInfo.Instance();
        activePlayerDistance = SelectPlayerDistance(honorSanctuaryMinimums,
            territoryInfo != null && territoryInfo->InSanctuary, sanctuaryDistance, maxDistance);
    }

    internal static float SelectPlayerDistance(bool honorSanctuaryMinimums, bool inSanctuary, float sanctuaryDistance, float maxDistance)
    {
        return honorSanctuaryMinimums && inSanctuary ? sanctuaryDistance : maxDistance;
    }

    internal static bool IsBeyondPlayerDistance(float localX, float localY, float localZ, float playerX, float playerY, float playerZ, float distance)
    {
        // Unknown/invalid positions must leave players visible. Boundary players remain visible too.
        if (!float.IsFinite(localX) || !float.IsFinite(localY) || !float.IsFinite(localZ)
            || !float.IsFinite(playerX) || !float.IsFinite(playerY) || !float.IsFinite(playerZ))
            return false;

        var dx = (double)playerX - localX;
        var dy = (double)playerY - localY;
        var dz = (double)playerZ - localZ;
        return dx * dx + dy * dy + dz * dz > (double)distance * distance;
    }

    private bool IsPlayerBeyondDistance(GameObject* gameObject)
    {
        if (!usePlayerDistance)
            return true;

        if (playerDistanceOrigin is not { } origin)
            return false;

        return IsBeyondPlayerDistance(origin.X, origin.Y, origin.Z,
            gameObject->Position.X, gameObject->Position.Y, gameObject->Position.Z, activePlayerDistance);
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
            return ShouldHidePlayer((BattleChara*)gameObject) && IsPlayerBeyondDistance(gameObject);

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

            return playerCount >= 10 && ShouldHidePlayer(player) && IsPlayerBeyondDistance(gameObject);
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
