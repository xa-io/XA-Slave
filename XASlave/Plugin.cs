using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using XASlave.Data;
using XASlave.Services;
using XASlave.Windows;

namespace XASlave;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IDataManager DataManager { get; private set; } = null!;
    [PluginService] public static IDutyState DutyState { get; private set; } = null!;
    [PluginService] public static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] public static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] public static IPartyList PartyList { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] public static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static INamePlateGui NamePlateGui { get; private set; } = null!;
    [PluginService] public static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] public static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] public static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;

    private const string CommandName = "/xa";
    private enum XAModsRestoreScope
    {
        Game,
        Graphic,
        Player,
        Plugin,
        Eureka,
        Illegal,
    }

    private readonly record struct XAModCommandDefinition(
        string Key,
        string DisplayName,
        XAModsRestoreScope Scope,
        Func<bool> GetCurrent,
        Func<bool, bool> Apply,
        Action<bool> Store,
        Func<string> GetStatusText);

    private readonly record struct XAModToggleCommandDefinition(
        string Subcommand,
        string Usage,
        XAModCommandDefinition Definition);

    private readonly record struct StartupSurfaceStatus(
        string Name,
        bool Requested,
        string StatusText,
        bool IsReady);

    private readonly record struct DeferredStartupAction(
        Action Action,
        string? Name,
        int LineNumber);

    public readonly record struct TitleBarFavXAModInfo(
        string Key,
        string DisplayName,
        string ScopeLabel);

    public static Plugin Instance { get; private set; } = null!;
    public string InstanceId { get; }
    public int ProcessId { get; }

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("XASlave");
    private SlaveWindow SlaveWindow { get; init; }
    public UpdatesWindow UpdatesWindow { get; init; }
    public XAPeepWindow XAPeepWindow { get; init; }
    public XAPeepHistoryWindow XAPeepHistoryWindow { get; init; }
    public EurekaLogogramCreatorFavoritesOverlayWindow EurekaLogogramCreatorFavoritesOverlayWindow { get; init; }
    public EurekaLogogramCreatorAutomationOverlayWindow EurekaLogogramCreatorAutomationOverlayWindow { get; init; }

    // Services
    public IpcClient IpcClient { get; init; }
    public DropboxQueueService DropboxQueue { get; init; }
    public IpcProvider IpcProvider { get; init; }
    public AutoCollectionService AutoCollector { get; init; }
    public TaskRunner TaskRunner { get; init; }
    public AutoRetainerConfigReader ArConfigReader { get; init; }
    public ExternalTaskLoader ExternalTaskLoader { get; init; }
    public WindowRenamerService WindowRenamer { get; init; }
    public AutoSkipCutsceneService AutoSkipCutscenes { get; init; }
    public BuddyFeedCutsceneSkipService BuddyFeedCutsceneSkip { get; init; }
    public PopupCleanerService PopupCleaner { get; init; }
    public SystemWindowModsService SystemWindowMods { get; init; }
    public LobbyErrorAutoCloseService LobbyErrorAutoClose { get; init; }
    public QueuePositionDisplayService QueuePositionDisplay { get; init; }
    public MsqProgressDisplayService MsqProgressDisplay { get; init; }
    public AutoHideGameObjectsService AutoHideGameObjects { get; init; }
    public DialogueSkipService DialogueSkip { get; init; }
    public CopyItemNameContextMenuService CopyItemNameContextMenu { get; init; }
    public SightDistanceService SightDistance { get; init; }
    public PlayerSearchContextMenuService PlayerSearchContextMenu { get; init; }
    public NameplatePrivacyService NameplatePrivacy { get; init; }
    public AutoUnlockExpertDeliveryService AutoUnlockExpertDelivery { get; init; }
    public ExpertDeliveryUnlockService ExpertDeliveryUnlock { get; init; }
    public AutoRefuseTradeService AutoRefuseTrade { get; init; }
    public TargetCommandFixService TargetCommandFix { get; init; }
    public AntiAfkService AntiAfk { get; init; }
    public AutoLeaveDutyService AutoLeaveDuty { get; init; }
    public EurekaInstanceIdService EurekaInstanceId { get; init; }
    public EurekaLogogramCreatorService EurekaLogogramCreator { get; init; }
    public AutoMergeService AutoMerge { get; init; }
    public ItemCommandsService ItemCommands { get; init; }
    public QuickReturnService QuickReturn { get; init; }
    public PlayerModsService PlayerMods { get; init; }
    public DozeSitAnywhereService DozeSitAnywhere { get; init; }
    public InstantLogoutService InstantLogout { get; init; }
    public TeleportLockClearService TeleportLockClear { get; init; }
    public EscMenuBailoutService EscMenuBailout { get; init; }
    public XAPeepService XAPeep { get; init; }
    public PeepingTomIntegrationService PeepingTomIntegration { get; init; }
    public ArPostProcessService ArPostProcessor { get; init; }
    public SlaveDatabaseService SlaveDatabase { get; init; }
    public XagmanPeerService XagmanPeers { get; private set; }

    private UiFlags appliedSpecialRenderUiFlags;
    private bool hasAppliedSpecialRenderUiFlags;
    private bool isDisposed;
    private readonly Queue<DeferredStartupAction> deferredStartupActions = new();
    private bool deferredStartupQueueScheduled;
    private const double DeferredStartupActionDebugThresholdMilliseconds = 5.0;
    private const double DeferredStartupActionWarningThresholdMilliseconds = 25.0;

    public Plugin()
    {
        Instance = this;
        ProcessId = Process.GetCurrentProcess().Id;
        InstanceId = Guid.NewGuid().ToString("N");

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var livePullsWereEnabled = Configuration.IpcLivePullsEnabled;
        Configuration.IpcLivePullsEnabled = false;
        var protectedRiskyToonModsReset = Configuration.UnlockExpertDeliveryEnabled || Configuration.MoveableAfterDeathEnabled;
        Configuration.UnlockExpertDeliveryEnabled = false;
        Configuration.MoveableAfterDeathEnabled = false;
        Configuration.InitializeFloorderDefaults();
        var autoGlamDefaultsInitialized = Configuration.InitializeAutoGlamWeatherDefaults();
        var showVersionInWindowTitleDefaultChanged = false;
        if (!Configuration.ShowVersionInWindowTitleDefaultApplied)
        {
            Configuration.ShowVersionInUpdatesTitle = true;
            Configuration.ShowVersionInWindowTitleDefaultApplied = true;
            showVersionInWindowTitleDefaultChanged = true;
        }
        var xagmanPreflightChanged = !Configuration.XagmanUsePreflightOnFirstCharacter;
        Configuration.XagmanUsePreflightOnFirstCharacter = true;
        var xagmanItemsChanged = false;
        var xagmanItemsMigrationChanged = false;
        var characterListAnonymizeMigrationChanged = MigrateLegacyCharacterListAnonymizeState(Configuration);
        var xaPeepSoundMigrationChanged = false;
        var normalizedXAPeepSoundEffectId = XAPeepData.ClampSoundEffectId(Configuration.XAPeepSoundEffectId);
        if (Configuration.XAPeepPlaySound && normalizedXAPeepSoundEffectId == 0)
        {
            normalizedXAPeepSoundEffectId = 2;
            xaPeepSoundMigrationChanged = true;
        }

        if (Configuration.XAPeepSoundEffectId != normalizedXAPeepSoundEffectId)
        {
            Configuration.XAPeepSoundEffectId = normalizedXAPeepSoundEffectId;
            xaPeepSoundMigrationChanged = true;
        }

        var unlockExpertDeliveryRankFloorChanged = false;
        var normalizedUnlockExpertDeliveryRankFloor = ExpertDeliveryUnlockService.NormalizeForcedRankFloor(Configuration.UnlockExpertDeliveryForcedRankFloor);
        if (Configuration.UnlockExpertDeliveryForcedRankFloor != normalizedUnlockExpertDeliveryRankFloor)
        {
            Configuration.UnlockExpertDeliveryForcedRankFloor = normalizedUnlockExpertDeliveryRankFloor;
            unlockExpertDeliveryRankFloorChanged = true;
        }

        var titleBarFavCustomItemsMigrationChanged = false;
        foreach (var item in Configuration.TitleBarFavCustomItems)
        {
            var normalizedSelectionKey = TitleBarFavSelectionKeys.Normalize(item.SelectionKey, item.MenuTarget);
            if (string.Equals(item.SelectionKey, normalizedSelectionKey, StringComparison.Ordinal))
                continue;

            item.SelectionKey = normalizedSelectionKey;
            titleBarFavCustomItemsMigrationChanged = true;
        }
        var eurekaInstanceIdMigrationChanged = MigrateLegacyEurekaInstanceIdState(Configuration);
        var eurekaLogogramCreatorDefaultsChanged = ApplyEurekaLogogramCreatorDefaultSettings(Configuration);
        if (!Configuration.XagmanSharedItemsMigrationComplete)
        {
            if (Configuration.XagmanItems.Count == 0 && (Configuration.XagmanTonyItems.Count > 0 || Configuration.XagmanFranchiseItems.Count > 0))
            {
                Configuration.XagmanItems = MergeXagmanItems(Configuration.XagmanTonyItems, Configuration.XagmanFranchiseItems);
                xagmanItemsChanged = true;
            }
            Configuration.XagmanTonyItems.Clear();
            Configuration.XagmanFranchiseItems.Clear();
            Configuration.XagmanSharedItemsMigrationComplete = true;
            xagmanItemsMigrationChanged = true;
        }
        var normalizedXagmanHubAddress = XagmanPeerService.NormalizeHubAddress(Configuration.XagmanHubAddress);
        var xagmanHubAddressChanged = !string.Equals(Configuration.XagmanHubAddress, normalizedXagmanHubAddress, StringComparison.Ordinal);
        Configuration.XagmanHubAddress = normalizedXagmanHubAddress;
        var normalizedXagmanHubPort = Configuration.XagmanHubPort == 47786
            ? XagmanPeerService.DefaultHubPort
            : XagmanPeerService.NormalizePort(Configuration.XagmanHubPort);
        var xagmanHubPortChanged = Configuration.XagmanHubPort != normalizedXagmanHubPort;
        Configuration.XagmanHubPort = normalizedXagmanHubPort;
        if (livePullsWereEnabled
            || protectedRiskyToonModsReset
            || autoGlamDefaultsInitialized
            || showVersionInWindowTitleDefaultChanged
            || xagmanPreflightChanged
            || xagmanItemsChanged
            || xagmanItemsMigrationChanged
            || characterListAnonymizeMigrationChanged
            || xaPeepSoundMigrationChanged
            || unlockExpertDeliveryRankFloorChanged
            || titleBarFavCustomItemsMigrationChanged
            || eurekaInstanceIdMigrationChanged
            || eurekaLogogramCreatorDefaultsChanged
            || xagmanHubAddressChanged
            || xagmanHubPortChanged)
            Configuration.Save();

        IpcClient = new IpcClient(PluginInterface, Log);
        DropboxQueue = new DropboxQueueService(PluginInterface, IpcClient, Log);
        SlaveDatabase = new SlaveDatabaseService(PluginInterface, Log);
        AutoCollector = new AutoCollectionService(this, Condition, Framework, ObjectTable, Log);
        TaskRunner = new TaskRunner(Condition, Framework, Log, DtrBar, ToastGui);
        ArConfigReader = new AutoRetainerConfigReader(PluginInterface, Log);
        IpcProvider = new IpcProvider(PluginInterface, this, Log);
        ExternalTaskLoader = new ExternalTaskLoader(this, PluginInterface, Log);
        WindowRenamer = new WindowRenamerService(Log);
        AutoSkipCutscenes = new AutoSkipCutsceneService(Condition, Framework, SigScanner, GameInterop, Log);
        BuddyFeedCutsceneSkip = new BuddyFeedCutsceneSkipService(SigScanner, GameInterop, ClientState, Log);
        PopupCleaner = new PopupCleanerService(AddonLifecycle, Log);
        SystemWindowMods = new SystemWindowModsService(SigScanner, GameInterop, Log, Framework, GameConfig, ClientState, () => IpcClient.AutoRetainerGetMultiModeEnabled());
        LobbyErrorAutoClose = new LobbyErrorAutoCloseService(AddonLifecycle, Log);
        QueuePositionDisplay = new QueuePositionDisplayService(SigScanner, GameInterop, Log);
        MsqProgressDisplay = new MsqProgressDisplayService(AddonLifecycle, DataManager, Log);
        AutoHideGameObjects = new AutoHideGameObjectsService(Framework, ClientState, Condition, TargetManager, SigScanner, GameInterop, Log);
        DialogueSkip = new DialogueSkipService(AddonLifecycle, SigScanner, GameInterop, Log);
        CopyItemNameContextMenu = new CopyItemNameContextMenuService(ContextMenu, DataManager, Log);
        SightDistance = new SightDistanceService(Framework, SigScanner, GameInterop, Log);
        PlayerSearchContextMenu = new PlayerSearchContextMenuService(ContextMenu, DataManager, Log);
        NameplatePrivacy = new NameplatePrivacyService(NamePlateGui, Log);
        AutoUnlockExpertDelivery = new AutoUnlockExpertDeliveryService(Framework, DataManager, Log);
        ExpertDeliveryUnlock = new ExpertDeliveryUnlockService(GameInterop, Log);
        ExpertDeliveryUnlock.ApplyConfiguration(Configuration.UnlockExpertDeliveryForcedRankFloor);
        AutoRefuseTrade = new AutoRefuseTradeService(SigScanner, GameInterop, Log);
        TargetCommandFix = new TargetCommandFixService(ChatGui, Log);
        AntiAfk = new AntiAfkService(Framework, ClientState, Log);
        AutoLeaveDuty = new AutoLeaveDutyService(DutyState, ClientState, PlayerState, Condition, Framework, Log);
        EurekaInstanceId = new EurekaInstanceIdService(Configuration, ClientState, PlayerState, Condition, Framework, Log, DtrBar);
        EurekaLogogramCreator = new EurekaLogogramCreatorService(Configuration);
        AutoMerge = new AutoMergeService(AddonLifecycle, Framework, ClientState, Condition, DataManager, Log);
        ItemCommands = new ItemCommandsService(DataManager, PlayerState);
        QuickReturn = new QuickReturnService(ClientState, GameInterop, Log);
        PlayerMods = new PlayerModsService(Framework, Condition, SigScanner, GameInterop, Log);
        DozeSitAnywhere = new DozeSitAnywhereService(ClientState, ObjectTable, SigScanner, GameInterop, Log);
        InstantLogout = new InstantLogoutService(ClientState, Framework, SigScanner, Log, LobbyErrorAutoClose);
        TeleportLockClear = new TeleportLockClearService(ChatGui, Log);
        EscMenuBailout = new EscMenuBailoutService(Framework, Log);
        XAPeep = new XAPeepService(Framework, ClientState, ObjectTable, GameGui, Log, SlaveDatabase, Configuration);
        PeepingTomIntegration = new PeepingTomIntegrationService(PluginInterface, Framework, Log);
        ArPostProcessor = new ArPostProcessService(this, ClientState, Condition, Framework, ObjectTable, Log, DtrBar);
        XagmanPeers = new XagmanPeerService(Log, InstanceId, Configuration.XagmanHubAddress, Configuration.XagmanHubPort, _ => { });
        if (Configuration.XagmanPeerConnectionsEnabled)
            XagmanPeers.Start();

        var normalizedAutoLeaveDutyDelay = AutoLeaveDutyService.ClampDelaySeconds(Configuration.AutoLeaveDutyDelaySeconds);
        if (Configuration.AutoLeaveDutyDelaySeconds != normalizedAutoLeaveDutyDelay)
        {
            Configuration.AutoLeaveDutyDelaySeconds = normalizedAutoLeaveDutyDelay;
            Configuration.Save();
        }
        if (NormalizeEurekaInstanceIdConfiguration(Configuration))
            Configuration.Save();
        EurekaInstanceId.ApplyConfiguration();
        var normalizedBailoutEscMenuSeconds = EscMenuBailoutService.NormalizeTimeoutSeconds(Configuration.BailoutEscMenuSeconds);
        if (Configuration.BailoutEscMenuSeconds != normalizedBailoutEscMenuSeconds)
        {
            Configuration.BailoutEscMenuSeconds = normalizedBailoutEscMenuSeconds;
            Configuration.Save();
        }
        var normalizedInfiniteSprintDelay = PlayerModsService.ClampInfiniteSprintDelaySeconds(Configuration.InfiniteSprintDelaySeconds);
        if (Math.Abs(Configuration.InfiniteSprintDelaySeconds - normalizedInfiniteSprintDelay) > 0.001f)
        {
            Configuration.InfiniteSprintDelaySeconds = normalizedInfiniteSprintDelay;
            Configuration.Save();
        }
        var normalizedLowResolutionScale = SystemWindowModsService.ClampLowResolutionScale(Configuration.LowResolutionScale);
        if (Math.Abs(Configuration.LowResolutionScale - normalizedLowResolutionScale) > 0.001f)
        {
            Configuration.LowResolutionScale = normalizedLowResolutionScale;
            Configuration.Save();
        }
        DozeSitAnywhere.ApplyConfiguration(
            Configuration.DozeSitAnywhereAllowDoze,
            Configuration.DozeSitAnywhereAllowSit);
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.AutoAllowMultipleGameInstancesEnabled && !SystemWindowMods.SetAllowMultipleGameInstancesEnabled(true))
            {
                Configuration.AutoAllowMultipleGameInstancesEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("AutoCancelLoginCooldownEnabled", () =>
        {
            if (Configuration.AutoCancelLoginCooldownEnabled && !SystemWindowMods.SetCancelLoginCooldownEnabled(true))
            {
                Configuration.AutoCancelLoginCooldownEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.AutoDisplayMsqProgressEnabled && !MsqProgressDisplay.SetEnabled(true))
            {
                Configuration.AutoDisplayMsqProgressEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.CopyItemNameForAllEnabled && !CopyItemNameContextMenu.SetEnabled(true))
            {
                Configuration.CopyItemNameForAllEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("AutoSkipCutscenesEnabled", () =>
        {
            if (Configuration.AutoSkipCutscenesEnabled && !AutoSkipCutscenes.SetEnabled(true))
            {
                Configuration.AutoSkipCutscenesEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("AutoSkipCutscenesFeedingChocoboEnabled", () =>
        {
            if (!Configuration.AutoSkipCutscenesFeedingChocoboEnabled)
                return;

            BuddyFeedCutsceneSkip.RestoreEnabledOnStartup();
        });
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.AutoIgnoreMinimumWindowSizeEnabled && !SystemWindowMods.SetIgnoreMinimumWindowSizeEnabled(true))
            {
                Configuration.AutoIgnoreMinimumWindowSizeEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            ApplyStoredXAModConfiguration("auto-hide-unnecessary-popups");
            if (Configuration.AutoHideUnnecessaryPopupsEnabled && !PopupCleaner.SetEnabled(true))
            {
                Configuration.AutoHideUnnecessaryPopupsEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("AutoPreventGameExitingFromLobbyErrorsEnabled", () =>
        {
            if (Configuration.AutoPreventGameExitingFromLobbyErrorsEnabled && !SystemWindowMods.SetPreventLobbyExitEnabled(true))
            {
                Configuration.AutoPreventGameExitingFromLobbyErrorsEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.AutoCloseLobbyErrorsEnabled && !LobbyErrorAutoClose.SetEnabled(true))
            {
                Configuration.AutoCloseLobbyErrorsEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("DisplayActualQueuePositionEnabled", () =>
        {
            if (Configuration.DisplayActualQueuePositionEnabled && !QueuePositionDisplay.SetEnabled(true))
            {
                Configuration.DisplayActualQueuePositionEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("AutoHideGameObjectsEnabled", () =>
        {
            if (!Configuration.AutoHideGameObjectsEnabled)
                return;

            ApplyStoredXAModConfiguration("auto-hide-game-objects");
            if (!AutoHideGameObjects.SetEnabled(true))
            {
                Configuration.AutoHideGameObjectsEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("AutoSkipDialogueEnabled", () =>
        {
            if (Configuration.AutoSkipDialogueEnabled && !DialogueSkip.SetEnabled(true))
            {
                Configuration.AutoSkipDialogueEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.CustomResolutionsEnabled && !SystemWindowMods.SetCustomResolutionsEnabled(true))
            {
                Configuration.CustomResolutionsEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("DisableBackgroundGameRenderingEnabled", () =>
        {
            if (!Configuration.DisableBackgroundGameRenderingEnabled)
                return;

            ApplyStoredXAModConfiguration("disable-background-game-rendering");
            if (!SystemWindowMods.SetDisableBackgroundRenderingEnabled(true))
            {
                Configuration.DisableBackgroundGameRenderingEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (!Configuration.LowResolutionEnabled)
                return;

            ApplyStoredXAModConfiguration("low-resolution");
            if (!SystemWindowMods.SetLowResolutionEnabled(true))
            {
                Configuration.LowResolutionEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("CustomSightDistanceEnabled", () =>
        {
            if (!Configuration.CustomSightDistanceEnabled)
                return;

            ApplyStoredXAModConfiguration("custom-sight-distance");
            if (!SightDistance.SetEnabled(true))
            {
                Configuration.CustomSightDistanceEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (!Configuration.ExpandedPlayerRightClickMenuSearchEnabled)
                return;

            ApplyStoredXAModConfiguration("expanded-player-right-click-menu-search");
            if (!PlayerSearchContextMenu.SetEnabled(true))
            {
                Configuration.ExpandedPlayerRightClickMenuSearchEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.LiveAnonymousModeEnabled && !NameplatePrivacy.SetAnonymousModeEnabled(true))
            {
                Configuration.LiveAnonymousModeEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (!Configuration.AutoUnlockExpertDeliveryEnabled)
                return;

            ApplyStoredXAModConfiguration("auto-expert-delivery");
            if (!AutoUnlockExpertDelivery.SetEnabled(true))
            {
                Configuration.AutoUnlockExpertDeliveryEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.AntiAfkEnabled && !AntiAfk.SetEnabled(true))
            {
                Configuration.AntiAfkEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (!Configuration.AutoLeaveDutyEnabled)
                return;

            ApplyStoredXAModConfiguration("auto-leave-duty");
            if (!AutoLeaveDuty.SetEnabled(true))
            {
                Configuration.AutoLeaveDutyEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (!Configuration.EurekaInstanceIdEnabled)
                return;

            ApplyStoredXAModConfiguration("eureka-instance-id");
            if (!EurekaInstanceId.SetEnabled(true))
            {
                Configuration.EurekaInstanceIdEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.AutoMergeEnabled && !AutoMerge.SetEnabled(true))
            {
                Configuration.AutoMergeEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("QuickReturnEnabled", () =>
        {
            if (Configuration.QuickReturnEnabled && !QuickReturn.SetEnabled(true))
            {
                Configuration.QuickReturnEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (!Configuration.UnlockExpertDeliveryEnabled)
                return;

            ApplyStoredXAModConfiguration("auto-unlock-expert-delivery");
            if (!ExpertDeliveryUnlock.SetEnabled(true))
            {
                Configuration.UnlockExpertDeliveryEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("AutoRefuseTradeRequestEnabled", () =>
        {
            if (!Configuration.AutoRefuseTradeRequestEnabled)
                return;

            ApplyStoredXAModConfiguration("auto-refuse-trade-request");
            if (!AutoRefuseTrade.SetEnabled(true))
            {
                Configuration.AutoRefuseTradeRequestEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("TargetCommandFixEnabled", () =>
        {
            if (Configuration.TargetCommandFixEnabled && !TargetCommandFix.SetEnabled(true))
            {
                Configuration.TargetCommandFixEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("AutoRevealUndiscoveredAreasEnabled", () =>
        {
            if (Configuration.AutoRevealUndiscoveredAreasEnabled && !SystemWindowMods.SetRevealUndiscoveredAreasEnabled(true))
            {
                Configuration.AutoRevealUndiscoveredAreasEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.AutoClearTeleportationLockEnabled && !TeleportLockClear.SetEnabled(true))
            {
                Configuration.AutoClearTeleportationLockEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (!Configuration.BailoutEscMenuEnabled)
                return;

            ApplyStoredXAModConfiguration("bailout-esc-menu");
            if (!EscMenuBailout.SetEnabled(true))
            {
                Configuration.BailoutEscMenuEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.SpecialRenderModesEnabled)
                ApplySpecialRenderModesConfiguration();
        });
        QueueDeferredStartupAction("DozeSitAnywhereEnabled", () =>
        {
            if (Configuration.DozeSitAnywhereEnabled && !DozeSitAnywhere.SetEnabled(true))
            {
                Configuration.DozeSitAnywhereEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (!Configuration.InfiniteSprintEnabled)
                return;

            ApplyStoredXAModConfiguration("infinite-sprint");
            if (!PlayerMods.SetInfiniteSprintEnabled(true))
            {
                Configuration.InfiniteSprintEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("InstantLogoutEnabled", () =>
        {
            if (Configuration.InstantLogoutEnabled && !InstantLogout.SetEnabled(true))
            {
                Configuration.InstantLogoutEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.ItemCommandsEnabled && !ItemCommands.SetEnabled(true))
            {
                Configuration.ItemCommandsEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (!Configuration.XAPeepEnabled)
                return;

            if (XAPeep.SetEnabled(true))
                return;

            Configuration.XAPeepEnabled = false;
            Configuration.Save();
        });
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.TitleBarFavKillGameEnabled && !Configuration.InstantLogoutEnabled)
            {
                var instantLogoutApplied = InstantLogout.SetEnabled(true);
                if (instantLogoutApplied)
                {
                    Configuration.InstantLogoutEnabled = true;
                }
                else
                {
                    Configuration.TitleBarFavKillGameEnabled = false;
                }

                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.MoveableAfterDeathEnabled && !PlayerMods.SetMoveableAfterDeathEnabled(true))
            {
                Configuration.MoveableAfterDeathEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.ForcePeepingTomEnabled && !PeepingTomIntegration.SetForceEnabled(true))
            {
                Configuration.ForcePeepingTomEnabled = false;
                Configuration.Save();
            }
        });

        SlaveWindow = new SlaveWindow(this);
        WindowSystem.AddWindow(SlaveWindow);

        XAPeepWindow = new XAPeepWindow(this);
        WindowSystem.AddWindow(XAPeepWindow);
        XAPeepHistoryWindow = new XAPeepHistoryWindow(this);
        WindowSystem.AddWindow(XAPeepHistoryWindow);
        EurekaLogogramCreatorFavoritesOverlayWindow = new EurekaLogogramCreatorFavoritesOverlayWindow(this);
        WindowSystem.AddWindow(EurekaLogogramCreatorFavoritesOverlayWindow);
        EurekaLogogramCreatorAutomationOverlayWindow = new EurekaLogogramCreatorAutomationOverlayWindow(this);
        WindowSystem.AddWindow(EurekaLogogramCreatorAutomationOverlayWindow);

        UpdatesWindow = new UpdatesWindow();
        WindowSystem.AddWindow(UpdatesWindow);

        if (Configuration.OpenPluginOnLoad)
        {
            QueueDeferredStartupAction(() =>
            {
                SlaveWindow.IsOpen = true;
            });
        }

        if (Configuration.XAPeepAutoOpenWindowOnPluginLoad || Configuration.XAPeepWindowOpen)
        {
            QueueDeferredStartupAction(() =>
            {
                XAPeepWindow.IsOpen = true;
            });
        }

        if (Configuration.XAPeepHistoryWindowOpen)
        {
            QueueDeferredStartupAction(() =>
            {
                XAPeepHistoryWindow.IsOpen = true;
            });
        }

        // Apply window rename on plugin load after the initial startup burst.
        if (Configuration.WindowRenamerEnabled)
        {
            QueueDeferredStartupAction(() =>
            {
                WindowRenamer.ApplyFromConfig(Configuration);
            });
        }

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open XA Slave. Subcommands include xamods/mods, peep, updates, db, preset save/load/list, XA Mods toggle on/off commands, res, lowres, sprintdelay, and the section restore commands."
        });

        PluginInterface.UiBuilder.Draw += UpdateEurekaLogogramCreatorOverlayWindows;
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw += XAPeep.DrawOverlay;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;

        ScheduleDeferredStartupQueue();
    }

    private void QueueDeferredStartupAction(Action action, [CallerLineNumber] int lineNumber = 0)
    {
        deferredStartupActions.Enqueue(new DeferredStartupAction(action, null, lineNumber));
    }

    private void QueueDeferredStartupAction(string name, Action action, [CallerLineNumber] int lineNumber = 0)
    {
        deferredStartupActions.Enqueue(new DeferredStartupAction(action, name, lineNumber));
    }

    private void ScheduleDeferredStartupQueue()
    {
        if (deferredStartupQueueScheduled || isDisposed)
            return;

        deferredStartupQueueScheduled = true;
        Framework.RunOnTick(ProcessDeferredStartupQueue, delayTicks: 1);
    }

    private void ProcessDeferredStartupQueue()
    {
        deferredStartupQueueScheduled = false;
        if (isDisposed)
            return;

        if (deferredStartupActions.Count == 0)
        {
            LogStartupSummary();
            return;
        }

        try
        {
            var deferredAction = deferredStartupActions.Dequeue();
            var stopwatch = Stopwatch.StartNew();
            deferredAction.Action();
            stopwatch.Stop();

            var elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            if (elapsedMilliseconds >= DeferredStartupActionDebugThresholdMilliseconds)
            {
                var label = string.IsNullOrWhiteSpace(deferredAction.Name)
                    ? $"Plugin.cs:{deferredAction.LineNumber}"
                    : deferredAction.Name;
                var message = $"[XASlave] Deferred startup action '{label}' took {elapsedMilliseconds:F1}ms.";
                if (elapsedMilliseconds >= DeferredStartupActionWarningThresholdMilliseconds)
                    Log.Warning(message);
                else
                    Log.Debug(message);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[XASlave] Deferred startup action failed.");
        }

        ScheduleDeferredStartupQueue();
    }

    private void LogStartupSummary()
    {
        var requestedSurfaces = GetStartupSurfaceStatuses()
            .Where(surface => surface.Requested)
            .ToList();

        if (requestedSurfaces.Count == 0)
        {
            Log.Information("[XASlave] Plugin loaded successfully. No hook-backed startup XA Mods were armed.");
            return;
        }

        var armedCount = requestedSurfaces.Count(surface => surface.IsReady);
        var unavailable = requestedSurfaces
            .Where(surface => !surface.IsReady)
            .ToList();

        var summary = unavailable.Count == 0
            ? $"[XASlave] Plugin loaded successfully. {armedCount} hook-backed startup XA Mods armed."
            : $"[XASlave] Plugin loaded successfully. {armedCount} hook-backed startup XA Mods armed, {unavailable.Count} unavailable. Open XA Mods for current status.";
        Log.Information(summary);

        if (unavailable.Count > 0)
            Log.Warning($"[XASlave] Startup unavailable: {string.Join("; ", unavailable.Select(surface => $"{surface.Name}: {surface.StatusText}"))}");
    }

    private IEnumerable<StartupSurfaceStatus> GetStartupSurfaceStatuses()
    {
        yield return CreateStartupSurfaceStatus("Cancel Login Cooldown", Configuration.AutoCancelLoginCooldownEnabled, SystemWindowMods.CancelLoginCooldownStatusText);
        yield return CreateStartupSurfaceStatus("Auto Skip Cutscenes", Configuration.AutoSkipCutscenesEnabled, AutoSkipCutscenes.StatusText);
        yield return CreateStartupSurfaceStatus("Prevent Lobby Exit", Configuration.AutoPreventGameExitingFromLobbyErrorsEnabled, SystemWindowMods.PreventLobbyExitStatusText);
        yield return CreateStartupSurfaceStatus("Queue Position Display", Configuration.DisplayActualQueuePositionEnabled, QueuePositionDisplay.StatusText);
        yield return CreateStartupSurfaceStatus("Auto Hide Game Objects", Configuration.AutoHideGameObjectsEnabled, AutoHideGameObjects.StatusText);
        yield return CreateStartupSurfaceStatus("Skip Dialogue", Configuration.AutoSkipDialogueEnabled, DialogueSkip.StatusText);
        yield return CreateStartupSurfaceStatus("Background Rendering Pause", Configuration.DisableBackgroundGameRenderingEnabled, SystemWindowMods.DisableBackgroundRenderingStatusText);
        yield return CreateStartupSurfaceStatus("Custom Sight Distance", Configuration.CustomSightDistanceEnabled, SightDistance.StatusText);
        yield return CreateStartupSurfaceStatus("Instant Return", Configuration.QuickReturnEnabled, QuickReturn.StatusText);
        yield return CreateStartupSurfaceStatus("Auto Refuse Trade", Configuration.AutoRefuseTradeRequestEnabled, AutoRefuseTrade.StatusText);
        yield return CreateStartupSurfaceStatus("Fix /target Command", Configuration.TargetCommandFixEnabled, TargetCommandFix.StatusText);
        yield return CreateStartupSurfaceStatus("Reveal Undiscovered Areas", Configuration.AutoRevealUndiscoveredAreasEnabled, SystemWindowMods.RevealUndiscoveredAreasStatusText);
        yield return CreateStartupSurfaceStatus("Special Render Modes", Configuration.SpecialRenderModesEnabled, SystemWindowMods.SpecialRenderModesStatusText);
        yield return CreateStartupSurfaceStatus("Doze & Sit Anywhere", Configuration.DozeSitAnywhereEnabled, DozeSitAnywhere.StatusText);
        yield return CreateStartupSurfaceStatus("Infinite Sprint", Configuration.InfiniteSprintEnabled, PlayerMods.InfiniteSprintStatusText);
        yield return CreateStartupSurfaceStatus("Instant Logout", Configuration.InstantLogoutEnabled, InstantLogout.StatusText);
        yield return CreateStartupSurfaceStatus("Unlock Expert Delivery", Configuration.UnlockExpertDeliveryEnabled, ExpertDeliveryUnlock.StatusText);
        yield return CreateStartupSurfaceStatus("Moveable After Death", Configuration.MoveableAfterDeathEnabled, PlayerMods.MoveableAfterDeathStatusText);
    }

    private static StartupSurfaceStatus CreateStartupSurfaceStatus(string name, bool requested, string statusText)
    {
        return new StartupSurfaceStatus(name, requested, statusText, IsStartupSurfaceReady(statusText));
    }

    private static bool IsStartupSurfaceReady(string statusText)
    {
        return statusText.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase)
            || statusText.StartsWith("Ready", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateEurekaLogogramCreatorOverlayWindows()
    {
        var showFavoritesOverlay = Configuration.ShowFavoritesOverlay
            && Configuration.FavoritePlates.Count > 0
            && EurekaLogogramCreator.IsManipulatorVisible()
            && EurekaLogogramCreator.TryGetSynthesisOverlayAnchor(out _);
        EurekaLogogramCreatorFavoritesOverlayWindow.IsOpen = showFavoritesOverlay;

        var showAutomationOverlay = EurekaLogogramCreator.HasActiveOrQueuedAutoLogoAction
            && EurekaLogogramCreator.TryGetSynthesisAutomationOverlayAnchor(out _);
        EurekaLogogramCreatorAutomationOverlayWindow.IsOpen = showAutomationOverlay;
    }

    public void Dispose()
    {
        if (isDisposed)
            return;

        isDisposed = true;

        // Detach public-facing callbacks first so a reload cannot re-enter partially disposed UI or services.
        TryCleanup("UiBuilder.Draw -= UpdateEurekaLogogramCreatorOverlayWindows", () => PluginInterface.UiBuilder.Draw -= UpdateEurekaLogogramCreatorOverlayWindows);
        TryCleanup("UiBuilder.Draw -= WindowSystem.Draw", () => PluginInterface.UiBuilder.Draw -= WindowSystem.Draw);
        TryCleanup("UiBuilder.Draw -= XAPeep.DrawOverlay", () => PluginInterface.UiBuilder.Draw -= XAPeep.DrawOverlay);
        TryCleanup("UiBuilder.OpenConfigUi -= ToggleMainUi", () => PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi);
        TryCleanup("UiBuilder.OpenMainUi -= ToggleMainUi", () => PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi);
        TryCleanup("ClientState.Login -= OnLogin", () => ClientState.Login -= OnLogin);
        TryCleanup("ClientState.Logout -= OnLogout", () => ClientState.Logout -= OnLogout);
        TryCleanup($"CommandManager.RemoveHandler({CommandName})", () => CommandManager.RemoveHandler(CommandName));
        TryCleanup("WindowSystem.RemoveAllWindows", WindowSystem.RemoveAllWindows);

        TryDispose("SlaveWindow", SlaveWindow);
        TryCleanup("XagmanPeers.Stop", XagmanPeers.Stop);
        TryDispose("TaskRunner", TaskRunner);
        TryDispose("AutoCollector", AutoCollector);

        TryDispose("AutoSkipCutscenes", AutoSkipCutscenes);
        TryDispose("BuddyFeedCutsceneSkip", BuddyFeedCutsceneSkip);
        TryDispose("PopupCleaner", PopupCleaner);
        TryDispose("SystemWindowMods", SystemWindowMods);
        TryDispose("LobbyErrorAutoClose", LobbyErrorAutoClose);
        TryDispose("QueuePositionDisplay", QueuePositionDisplay);
        TryDispose("MsqProgressDisplay", MsqProgressDisplay);
        TryDispose("AutoHideGameObjects", AutoHideGameObjects);
        TryDispose("DialogueSkip", DialogueSkip);
        TryDispose("CopyItemNameContextMenu", CopyItemNameContextMenu);
        TryDispose("SightDistance", SightDistance);
        TryDispose("PlayerSearchContextMenu", PlayerSearchContextMenu);
        TryDispose("NameplatePrivacy", NameplatePrivacy);
        TryDispose("AutoUnlockExpertDelivery", AutoUnlockExpertDelivery);
        TryDispose("ExpertDeliveryUnlock", ExpertDeliveryUnlock);
        TryDispose("AutoRefuseTrade", AutoRefuseTrade);
        TryDispose("TargetCommandFix", TargetCommandFix);
        TryDispose("AntiAfk", AntiAfk);
        TryDispose("AutoLeaveDuty", AutoLeaveDuty);
        TryDispose("EurekaInstanceId", EurekaInstanceId);
        TryDispose("EurekaLogogramCreator", EurekaLogogramCreator);
        TryDispose("AutoMerge", AutoMerge);
        TryDispose("ItemCommands", ItemCommands);
        TryDispose("QuickReturn", QuickReturn);
        TryDispose("PlayerMods", PlayerMods);
        TryDispose("DozeSitAnywhere", DozeSitAnywhere);
        TryDispose("InstantLogout", InstantLogout);
        TryDispose("TeleportLockClear", TeleportLockClear);
        TryDispose("EscMenuBailout", EscMenuBailout);
        TryDispose("XAPeep", XAPeep);
        TryDispose("PeepingTomIntegration", PeepingTomIntegration);
        TryDispose("ArPostProcessor", ArPostProcessor);
        TryDispose("WindowRenamer", WindowRenamer);
        TryDispose("IpcProvider", IpcProvider);
        TryDispose("ExternalTaskLoader", ExternalTaskLoader);
        TryDispose("XagmanPeers", XagmanPeers);
    }

    private void TryDispose(string label, IDisposable? disposable)
    {
        if (disposable == null)
            return;

        TryCleanup(label, disposable.Dispose);
    }

    private void TryCleanup(string label, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            Log.Warning($"[XASlave] Dispose cleanup failed for {label}: {ex}");
        }
    }

    public bool SaveToXaDatabaseAndRecordSync()
    {
        return SaveToXaDatabaseAndRecordSync(PlayerState.ContentId, PlayerState.CharacterName.ToString());
    }

    public bool SaveToXaDatabaseAndRecordSync(ulong contentId, string characterName)
    {
        var success = IpcClient.Save();
        if (success && contentId != 0)
            SlaveDatabase.RecordLastSyncedToXaDb(contentId, characterName, DateTime.UtcNow);

        return success;
    }

    public void RecordCurrentCharacterSync()
    {
        if (!PlayerState.IsLoaded || PlayerState.ContentId == 0)
            return;

        SlaveDatabase.RecordLastSyncedToXaDb(PlayerState.ContentId, PlayerState.CharacterName.ToString(), DateTime.UtcNow);
    }

    public DateTime? GetCurrentCharacterLastSyncedToXaDbUtc()
    {
        if (!PlayerState.IsLoaded || PlayerState.ContentId == 0)
            return null;

        return SlaveDatabase.GetLastSyncedToXaDbUtc(PlayerState.ContentId);
    }

    public bool IsCurrentCharacterSyncDue(int everyHours)
    {
        if (everyHours <= 0 || !PlayerState.IsLoaded || PlayerState.ContentId == 0)
            return true;

        return SlaveDatabase.IsSyncDue(PlayerState.ContentId, everyHours);
    }

    private void OnLogin()
    {
        Log.Information("[XASlave] Character logged in.");
        if (TaskRunner.IsRunning)
            TaskRunner.AddLog("EVENT: Character logged in.");

        if (Configuration.WindowRenamerEnabled && Configuration.WindowRenamerShowCurrentCharacter)
            WindowRenamer.ApplyFromConfig(Configuration, PlayerState.CharacterName.ToString());

        if (Configuration.OpenPluginOnLoad)
            SlaveWindow.IsOpen = true;

        // Trigger auto-collection if enabled
        if (Configuration.AutoCollectOnLogin)
        {
            SlaveWindow.ScheduleAutoCollection();
        }
    }

    private void OnLogout(int type, int code)
    {
        SlaveWindow.CancelScheduledAutoCollection(true);
        var contentId = PlayerState.ContentId;
        var characterName = PlayerState.CharacterName.ToString();

        if (Configuration.WindowRenamerEnabled && Configuration.WindowRenamerShowCurrentCharacter)
            WindowRenamer.ApplyFromConfig(Configuration, string.Empty);

        // Cancel any running task on logout BUT skip if relogger suppresses it
        // (logout is expected during /ays relog character switches)
        if (TaskRunner.IsRunning && !TaskRunner.SuppressLogoutCancel)
        {
            Log.Information("[XASlave] Character logged out, cancelling running task.");
            TaskRunner.AddLog("EVENT: Character logged out, cancelling running task.");
            TaskRunner.Cancel();
        }
        else if (TaskRunner.IsRunning)
        {
            Log.Information("[XASlave] Character logged out, relogger active, not cancelling.");
            TaskRunner.AddLog("EVENT: Character logged out, relogger active, not cancelling.");
        }

        Log.Information("[XASlave] Character logged out, sending final save to XA Database.");
        if (TaskRunner.IsRunning)
            TaskRunner.AddLog("EVENT: Character logged out, sending final save to XA Database.");
        SaveToXaDatabaseAndRecordSync(contentId, characterName);
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            SlaveWindow.Toggle();
            return;
        }

        var firstSpaceIndex = trimmed.IndexOf(' ');
        var subcommand = firstSpaceIndex >= 0 ? trimmed[..firstSpaceIndex].Trim() : trimmed;
        var subcommandArgs = firstSpaceIndex >= 0 ? trimmed[(firstSpaceIndex + 1)..].Trim() : string.Empty;

#if false
        // /xa run <task> test IPC RunTask locally
        if (subcommand.Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            var taskName = subcommandArgs;
            if (string.IsNullOrEmpty(taskName))
            {
                Log.Information("[XASlave] Usage: /xa run <taskName>  (e.g. /xa run save)");
                return;
            }
            Log.Information($"[XASlave] /xa run: invoking RunTask('{taskName}')...");
            IpcProvider.InvokeRunTask(taskName);
            return;
        }
#endif

        if (IsXaModsSubcommand(subcommand))
        {
            SlaveWindow.OpenXAModsTask();
            return;
        }

        if (subcommand.Equals("commands", StringComparison.OrdinalIgnoreCase))
        {
            SlaveWindow.OpenCommandsReferenceTask();
            return;
        }

        if (subcommand.Equals("updates", StringComparison.OrdinalIgnoreCase))
        {
            UpdatesWindow.Toggle();
            return;
        }

        if (subcommand.Equals("peep", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(TryHandleXAPeepCommand(subcommandArgs, out var message), message);
            return;
        }

        if (subcommand.Equals("db", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(DropboxQueue.TryExecute(subcommandArgs, out var message), message);
            return;
        }

        if (subcommand.Equals("sit", StringComparison.OrdinalIgnoreCase))
        {
            DozeSitAnywhere.RequestSit();
            return;
        }

        if (subcommand.Equals("doze", StringComparison.OrdinalIgnoreCase))
        {
            DozeSitAnywhere.RequestDoze();
            return;
        }

        if (subcommand.Equals("logout", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(TryRequestLogoutAction(out var message), message);
            return;
        }

        if (subcommand.Equals("killgame", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(TryRequestKillGameAction(out var message), message);
            return;
        }

        if (subcommand.Equals("preset", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(TryHandlePresetCommand(subcommandArgs, out var message), message);
            return;
        }

        if (subcommand.Equals("lowres", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(TryApplyLowResolutionCommand(subcommandArgs, out var message), message);
            return;
        }

        if (subcommand.Equals("res", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(TryHandleResolutionCommand(subcommandArgs, out var message), message);
            return;
        }

        if (subcommand.Equals("equip", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(ItemCommands.TryExecuteEquipCommand(subcommandArgs, out var message), message);
            return;
        }

        if (subcommand.Equals("sprintdelay", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(TryApplySprintDelayCommand(subcommandArgs, out var message), message);
            return;
        }

        if (subcommand.Equals("allrestore", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(RestoreAllXAMods(out var message), message);
            return;
        }

        // Unknown subcommand - toggle window
        if (subcommand.Equals("resrestore", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(RestoreXAModsSection(XAModsRestoreScope.Graphic, out var message), message);
            return;
        }

        if (subcommand.Equals("gamerestore", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(RestoreXAModsSection(XAModsRestoreScope.Game, out var message), message);
            return;
        }

        if (subcommand.Equals("playerrestore", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(RestoreXAModsSection(XAModsRestoreScope.Player, out var message), message);
            return;
        }

        if (subcommand.Equals("pluginrestore", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(RestoreXAModsSection(XAModsRestoreScope.Plugin, out var message), message);
            return;
        }

        if (subcommand.Equals("eurekarestore", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(RestoreXAModsSection(XAModsRestoreScope.Eureka, out var message), message);
            return;
        }

        if (subcommand.Equals("imlegit", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(RestoreXAModsSection(XAModsRestoreScope.Illegal, out var message), message);
            return;
        }

        if (TryHandleXAModToggleCommand(subcommand, subcommandArgs))
            return;

        ChatGui.PrintError("[XASlave] That is not a command, please read `/xa commands`.");
    }

    public bool TryExecuteXaCommandFromIpc(string rawCommand, out string message)
    {
        var trimmed = NormalizeXaCommandInput(rawCommand);
        if (string.IsNullOrEmpty(trimmed))
        {
            message = "Usage: pass the same text you would enter after /xa, for example `commands`, `mods`, `db clear`, `logout`, `killgame`, `sprint on`, or `res 500x345`.";
            return false;
        }

        var firstSpaceIndex = trimmed.IndexOf(' ');
        var subcommand = firstSpaceIndex >= 0 ? trimmed[..firstSpaceIndex].Trim() : trimmed;
        var subcommandArgs = firstSpaceIndex >= 0 ? trimmed[(firstSpaceIndex + 1)..].Trim() : string.Empty;

        if (IsXaModsSubcommand(subcommand))
        {
            SlaveWindow.OpenXAModsTask();
            message = "Opened XA Mods.";
            return true;
        }

        if (subcommand.Equals("commands", StringComparison.OrdinalIgnoreCase))
        {
            SlaveWindow.OpenCommandsReferenceTask();
            message = "Opened Commands reference.";
            return true;
        }

        if (subcommand.Equals("peep", StringComparison.OrdinalIgnoreCase))
            return TryHandleXAPeepCommand(subcommandArgs, out message);

        if (subcommand.Equals("db", StringComparison.OrdinalIgnoreCase))
            return DropboxQueue.TryExecute(subcommandArgs, out message);

        if (subcommand.Equals("sit", StringComparison.OrdinalIgnoreCase))
        {
            var success = DozeSitAnywhere.RequestSit();
            message = success ? "Triggered Sit Anywhere." : DozeSitAnywhere.StatusText;
            return success;
        }

        if (subcommand.Equals("doze", StringComparison.OrdinalIgnoreCase))
        {
            var success = DozeSitAnywhere.RequestDoze();
            message = success ? "Triggered Doze Anywhere." : DozeSitAnywhere.StatusText;
            return success;
        }

        if (subcommand.Equals("logout", StringComparison.OrdinalIgnoreCase))
        {
            var success = TryRequestLogoutAction(out message);
            if (success)
                message = "Triggered XA hard logout.";
            return success;
        }

        if (subcommand.Equals("killgame", StringComparison.OrdinalIgnoreCase))
        {
            var success = TryRequestKillGameAction(out message);
            if (success)
                message = "Triggered XA kill-game flow.";
            return success;
        }

        if (subcommand.Equals("preset", StringComparison.OrdinalIgnoreCase))
            return TryHandlePresetCommandFromIpc(subcommandArgs, out message);

        if (subcommand.Equals("lowres", StringComparison.OrdinalIgnoreCase))
            return TryApplyLowResolutionCommand(subcommandArgs, out message);

        if (subcommand.Equals("res", StringComparison.OrdinalIgnoreCase))
            return TryHandleResolutionCommand(subcommandArgs, out message);

        if (subcommand.Equals("equip", StringComparison.OrdinalIgnoreCase))
            return ItemCommands.TryExecuteEquipCommand(subcommandArgs, out message);

        if (subcommand.Equals("sprintdelay", StringComparison.OrdinalIgnoreCase))
            return TryApplySprintDelayCommand(subcommandArgs, out message);

        if (subcommand.Equals("allrestore", StringComparison.OrdinalIgnoreCase))
            return RestoreAllXAMods(out message);

        if (subcommand.Equals("resrestore", StringComparison.OrdinalIgnoreCase))
            return RestoreXAModsSection(XAModsRestoreScope.Graphic, out message);

        if (subcommand.Equals("gamerestore", StringComparison.OrdinalIgnoreCase))
            return RestoreXAModsSection(XAModsRestoreScope.Game, out message);

        if (subcommand.Equals("playerrestore", StringComparison.OrdinalIgnoreCase))
            return RestoreXAModsSection(XAModsRestoreScope.Player, out message);

        if (subcommand.Equals("pluginrestore", StringComparison.OrdinalIgnoreCase))
            return RestoreXAModsSection(XAModsRestoreScope.Plugin, out message);

        if (subcommand.Equals("eurekarestore", StringComparison.OrdinalIgnoreCase))
            return RestoreXAModsSection(XAModsRestoreScope.Eureka, out message);

        if (subcommand.Equals("imlegit", StringComparison.OrdinalIgnoreCase))
            return RestoreXAModsSection(XAModsRestoreScope.Illegal, out message);

        if (TryGetXAModToggleCommandDefinition(subcommand, out var commandDefinition))
        {
            if (string.IsNullOrWhiteSpace(subcommandArgs))
            {
                var currentState = commandDefinition.Definition.GetCurrent() ? "On" : "Off";
                message = $"{commandDefinition.Definition.DisplayName}: {currentState}. {commandDefinition.Definition.GetStatusText()} Usage: {commandDefinition.Usage}";
                return true;
            }

            if (!TryParseToggleCommandState(subcommandArgs, out var enabled, out var toggleMessage))
            {
                message = $"{toggleMessage} Usage: {commandDefinition.Usage}";
                return false;
            }

            return SetXAModEnabled(commandDefinition.Definition, enabled, out message);
        }

        message = "That is not a command, please read `/xa commands`.";
        return false;
    }

    private static string NormalizeXaCommandInput(string rawCommand)
    {
        if (string.IsNullOrWhiteSpace(rawCommand))
            return string.Empty;

        var trimmed = rawCommand.Trim();
        if (trimmed.StartsWith("/xa", StringComparison.OrdinalIgnoreCase)
            && (trimmed.Length == 3 || char.IsWhiteSpace(trimmed[3])))
        {
            return trimmed.Length == 3 ? string.Empty : trimmed[3..].Trim();
        }

        return trimmed;
    }

    private static bool TryParseResolutionCommand(string value, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Split(['x', 'X'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        return int.TryParse(parts[0], out width)
            && int.TryParse(parts[1], out height);
    }

    private bool TryApplyLowResolutionCommand(string value, out string message)
    {
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            SystemWindowMods.SetLowResolutionEnabled(false);
            Configuration.LowResolutionEnabled = false;
            Configuration.Save();
            message = "Low Resolution disabled.";
            return true;
        }

        if (!TryParseLowResolutionCommand(value, out var scale, out message))
            return false;

        if (!Configuration.LowResolutionEnabled)
        {
            message = "Enable `Low Resolution` in XA Mods first.";
            return false;
        }

        Configuration.LowResolutionScale = scale;
        SystemWindowMods.ApplyLowResolutionConfiguration(scale);
        Configuration.Save();
        message = $"Low Resolution scale set to {scale:0.00}.";
        return true;
    }

    private static bool TryParseLowResolutionCommand(string value, out float scale, out string message)
    {
        scale = 0f;
        if (string.IsNullOrWhiteSpace(value))
        {
            message = "Usage: /xa lowres <scale> or /xa lowres off.";
            return false;
        }

        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out scale))
        {
            message = "Usage: /xa lowres <scale> or /xa lowres off.";
            return false;
        }

        if (scale < 0.01f || scale > 1.00f)
        {
            message = "Low Resolution scale must be between 0.01 and 1.00.";
            return false;
        }

        scale = SystemWindowModsService.ClampLowResolutionScale(scale);
        message = string.Empty;
        return true;
    }

    private bool TryHandlePresetCommand(string value, out string message)
    {
        var firstSpaceIndex = value.IndexOf(' ');
        var action = firstSpaceIndex >= 0 ? value[..firstSpaceIndex].Trim() : value.Trim();
        var actionArgs = firstSpaceIndex >= 0 ? value[(firstSpaceIndex + 1)..].Trim() : string.Empty;

        if (string.IsNullOrWhiteSpace(action))
        {
            message = "Usage: /xa preset save <name>, /xa preset load <name>, or /xa preset list.";
            return false;
        }

        if (action.Equals("save", StringComparison.OrdinalIgnoreCase))
            return SaveCurrentXAModsPreset(actionArgs, out message);

        if (action.Equals("load", StringComparison.OrdinalIgnoreCase))
            return LoadSavedXAModsPreset(actionArgs, out message);

        if (action.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            PrintSavedXAModsPresetList();
            message = string.Empty;
            return true;
        }

        message = "Usage: /xa preset save <name>, /xa preset load <name>, or /xa preset list.";
        return false;
    }

    private bool SaveCurrentXAModsPreset(string name, out string message)
    {
        var trimmedName = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            message = "Usage: /xa preset save <name>.";
            return false;
        }

        var modKeys = GetCurrentXAModKeys();
        var modSettings = CaptureXAModSettingsForKeys(modKeys);
        var savedList = Configuration.ToonModsSavedLists.FirstOrDefault(entry => entry.Name.Equals(trimmedName, StringComparison.OrdinalIgnoreCase));
        if (savedList == null)
        {
            Configuration.ToonModsSavedLists.Add(new ToonModSavedList
            {
                Name = trimmedName,
                ModKeys = modKeys,
                ModSettings = modSettings,
            });
        }
        else
        {
            savedList.Name = trimmedName;
            savedList.ModKeys = modKeys;
            savedList.ModSettings = modSettings;
        }

        Configuration.Save();
        message = $"Saved XA Mods preset '{trimmedName}' with {modKeys.Count} enabled mod(s).";
        return true;
    }

    private bool LoadSavedXAModsPreset(string name, out string message)
    {
        var trimmedName = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            message = "Usage: /xa preset load <name>.";
            return false;
        }

        var savedList = Configuration.ToonModsSavedLists.FirstOrDefault(entry => entry.Name.Equals(trimmedName, StringComparison.OrdinalIgnoreCase));
        if (savedList == null)
        {
            message = $"No XA Mods preset named '{trimmedName}' was found.";
            return false;
        }

        return ApplyXAModsPreset(savedList.Name, savedList.ModKeys, savedList.ModSettings, out message);
    }

    private void PrintSavedXAModsPresetList()
    {
        var savedNames = Configuration.ToonModsSavedLists
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Name)
            .ToList();

        if (savedNames.Count == 0)
        {
            ChatGui.Print("[XASlave] No XA Mods presets are currently saved.");
            return;
        }

        ChatGui.Print($"[XASlave] Saved XA Mods presets ({savedNames.Count}):");
        foreach (var savedName in savedNames)
            ChatGui.Print($"[XASlave] - {savedName}");
    }

    private bool TryHandlePresetCommandFromIpc(string value, out string message)
    {
        var firstSpaceIndex = value.IndexOf(' ');
        var action = firstSpaceIndex >= 0 ? value[..firstSpaceIndex].Trim() : value.Trim();
        var actionArgs = firstSpaceIndex >= 0 ? value[(firstSpaceIndex + 1)..].Trim() : string.Empty;

        if (string.IsNullOrWhiteSpace(action))
        {
            message = "Usage: preset save <name>, preset load <name>, or preset list.";
            return false;
        }

        if (action.Equals("save", StringComparison.OrdinalIgnoreCase))
            return SaveCurrentXAModsPreset(actionArgs, out message);

        if (action.Equals("load", StringComparison.OrdinalIgnoreCase))
            return LoadSavedXAModsPreset(actionArgs, out message);

        if (action.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            message = BuildSavedXAModsPresetListSummary();
            return true;
        }

        message = "Usage: preset save <name>, preset load <name>, or preset list.";
        return false;
    }

    private string BuildSavedXAModsPresetListSummary()
    {
        var savedNames = Configuration.ToonModsSavedLists
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Name)
            .ToList();

        if (savedNames.Count == 0)
            return "No XA Mods presets are currently saved.";

        return $"Saved XA Mods presets ({savedNames.Count}): {string.Join(", ", savedNames)}";
    }

    private List<string> GetCurrentXAModKeys()
    {
        return GetAllXAModDefinitions()
            .Where(entry => entry.GetCurrent())
            .Select(entry => entry.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal Dictionary<string, JsonElement> CaptureXAModSettingsForKeys(IEnumerable<string> modKeys)
    {
        var snapshots = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in modKeys
                     .Where(key => !string.IsNullOrWhiteSpace(key))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryCreateXAModSettingsSnapshot(key, out var snapshot))
                snapshots[key] = snapshot;
        }

        return snapshots;
    }

    internal bool ApplySavedXAModsPreset(string title, IEnumerable<string> modKeys, IReadOnlyDictionary<string, JsonElement>? modSettings, out string message)
        => ApplyXAModsPreset(title, modKeys, modSettings, out message);

    private bool ApplyXAModsPreset(string title, IEnumerable<string> modKeys, out string message)
        => ApplyXAModsPreset(title, modKeys, null, out message);

    private bool ApplyXAModsPreset(string title, IEnumerable<string> modKeys, IReadOnlyDictionary<string, JsonElement>? modSettings, out string message)
    {
        var requestedKeys = modKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var definitionsByKey = GetAllXAModDefinitions().ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);

        DisableXAModDefinitions(definitionsByKey.Values);

        if (requestedKeys.Count == 0)
        {
            Configuration.Save();
            message = $"Loaded XA Mods preset '{title}' with all mods disabled.";
            return true;
        }

        var appliedCount = 0;
        var unavailableCount = 0;
        var unknownCount = 0;

        foreach (var key in requestedKeys)
        {
            if (!definitionsByKey.TryGetValue(key, out var definition))
            {
                unknownCount++;
                continue;
            }

            var applied = definition.Apply(true);
            definition.Store(applied);
            if (applied)
                appliedCount++;
            else
                unavailableCount++;
        }

        ApplyImportedXAModSettings(modSettings);
        Configuration.Save();
        message = unknownCount > 0 || unavailableCount > 0
            ? $"Loaded XA Mods preset '{title}' ({appliedCount} applied, {unavailableCount} unavailable, {unknownCount} unknown)."
            : $"Loaded XA Mods preset '{title}' ({appliedCount} mod(s)).";
        return unknownCount == 0 && unavailableCount == 0;
    }

    private bool TryCreateXAModSettingsSnapshot(string key, out JsonElement snapshot)
    {
        switch (key)
        {
            case "disable-background-game-rendering":
                snapshot = JsonSerializer.SerializeToElement(new XAModDisableBackgroundRenderingSettings
                {
                    OnlyWhenMinimized = Configuration.DisableBackgroundGameRenderingOnlyWhenMinimized,
                    DisableWhenArMultiIsOn = Configuration.DisableBackgroundGameRenderingDisableWhenArMultiIsOn,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "auto-hide-game-objects":
                snapshot = JsonSerializer.SerializeToElement(new XAModAutoHideGameObjectsSettings
                {
                    HidePlayer = Configuration.AutoHideGameObjectsHidePlayer,
                    HideUnimportantEnpc = Configuration.AutoHideGameObjectsHideUnimportantEnpc,
                    HidePet = Configuration.AutoHideGameObjectsHidePet,
                    HideChocobo = Configuration.AutoHideGameObjectsHideChocobo,
                    DisableInDuties = Configuration.AutoHideGameObjectsDisableInDuties,
                    DisableInIslandSanctuary = Configuration.AutoHideGameObjectsDisableInIslandSanctuary,
                    UseOccultCrescentRules = Configuration.AutoHideGameObjectsUseOccultCrescentRules,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "custom-resolutions":
                snapshot = JsonSerializer.SerializeToElement(new XAModCustomResolutionsSettings
                {
                    Presets = Configuration.CustomResolutionPresets
                        .Select(entry => new XAModResolutionPreset
                        {
                            Width = entry.Width,
                            Height = entry.Height,
                        })
                        .ToList(),
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "low-resolution":
                snapshot = JsonSerializer.SerializeToElement(new XAModLowResolutionSettings
                {
                    Scale = Configuration.LowResolutionScale,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "special-rendering-modes":
                snapshot = JsonSerializer.SerializeToElement(new XAModSpecialRenderModesSettings
                {
                    BackgroundColorR = Configuration.SpecialRenderModeBackgroundColorR,
                    BackgroundColorG = Configuration.SpecialRenderModeBackgroundColorG,
                    BackgroundColorB = Configuration.SpecialRenderModeBackgroundColorB,
                    BackgroundColorA = Configuration.SpecialRenderModeBackgroundColorA,
                    HideAddonsKeepNameplates = Configuration.SpecialRenderHideAddonsKeepNameplatesEnabled,
                    HideAddonsKeepChat = Configuration.SpecialRenderHideAddonsKeepChatEnabled,
                    HideChat = Configuration.SpecialRenderHideChatEnabled,
                    HideActionBars = Configuration.SpecialRenderHideActionBarsEnabled,
                    HideTargetInfo = Configuration.SpecialRenderHideTargetInfoEnabled,
                    HideNameplates = Configuration.SpecialRenderHideNameplatesEnabled,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "expanded-player-right-click-menu-search":
                snapshot = JsonSerializer.SerializeToElement(new XAModPlayerSearchSettings
                {
                    FflogsEnabled = Configuration.ExpandedPlayerRightClickMenuSearchFflogsEnabled,
                    LodestoneEnabled = Configuration.ExpandedPlayerRightClickMenuSearchLodestoneEnabled,
                    LalachievementsEnabled = Configuration.ExpandedPlayerRightClickMenuSearchLalachievementsEnabled,
                    OpenAllEnabled = Configuration.ExpandedPlayerRightClickMenuSearchOpenAllEnabled,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "auto-expert-delivery":
                snapshot = JsonSerializer.SerializeToElement(new XAModExpertDeliverySettings
                {
                    AutoSwitchWhenOpen = Configuration.AutoUnlockExpertDeliveryAutoSwitchWhenOpen,
                    DefaultPage = Configuration.AutoUnlockExpertDeliveryDefaultPage,
                    SkipHq = Configuration.AutoUnlockExpertDeliverySkipHq,
                    SkipMateria = Configuration.AutoUnlockExpertDeliverySkipMateria,
                    IgnoreSealCap = Configuration.AutoUnlockExpertDeliveryIgnoreSealCap,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "auto-unlock-expert-delivery":
                snapshot = JsonSerializer.SerializeToElement(new XAModUnlockExpertDeliverySettings
                {
                    ForcedRankFloor = Configuration.UnlockExpertDeliveryForcedRankFloor,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "bailout-esc-menu":
                snapshot = JsonSerializer.SerializeToElement(new XAModBailoutEscMenuSettings
                {
                    TimeoutSeconds = Configuration.BailoutEscMenuSeconds,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "auto-hide-unnecessary-popups":
                snapshot = JsonSerializer.SerializeToElement(new XAModPopupCleanerSettings
                {
                    HideHowToNotice = Configuration.AutoHideUnnecessaryPopupsHideHowToNoticeEnabled,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "auto-leave-duty":
                snapshot = JsonSerializer.SerializeToElement(new XAModAutoLeaveDutySettings
                {
                    DelaySeconds = Configuration.AutoLeaveDutyDelaySeconds,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "eureka-instance-id":
                snapshot = JsonSerializer.SerializeToElement(new XAModEurekaInstanceIdSettings
                {
                    Zone = Configuration.EurekaInstanceIdZone,
                    BaselineInstanceId = Configuration.EurekaInstanceIdBaselineInstanceId,
                    ShowInDtr = Configuration.EurekaInstanceIdShowInDtr,
                    LeaveDutyDelaySeconds = Configuration.EurekaInstanceIdLeaveDutyDelaySeconds,
                    AnemosEnabled = Configuration.EurekaInstanceIdAnemosEnabled,
                    AnemosBaselineInstanceId = Configuration.EurekaInstanceIdAnemosBaselineInstanceId,
                    PagosEnabled = Configuration.EurekaInstanceIdPagosEnabled,
                    PagosBaselineInstanceId = Configuration.EurekaInstanceIdPagosBaselineInstanceId,
                    PyrosEnabled = Configuration.EurekaInstanceIdPyrosEnabled,
                    PyrosBaselineInstanceId = Configuration.EurekaInstanceIdPyrosBaselineInstanceId,
                    HydatosEnabled = Configuration.EurekaInstanceIdHydatosEnabled,
                    HydatosBaselineInstanceId = Configuration.EurekaInstanceIdHydatosBaselineInstanceId,
                    PlaySound = Configuration.EurekaInstanceIdPlaySound,
                    SoundEffectId = Configuration.EurekaInstanceIdSoundEffectId,
                    SoundVolume = Configuration.EurekaInstanceIdSoundVolume,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "auto-refuse-trade-request":
                snapshot = JsonSerializer.SerializeToElement(new XAModTradeRefusalSettings
                {
                    ShowNotification = Configuration.AutoRefuseTradeShowNotification,
                    SendEcho = Configuration.AutoRefuseTradeSendEcho,
                    ExtraCommands = Configuration.AutoRefuseTradeExtraCommands,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "custom-sight-distance":
                snapshot = JsonSerializer.SerializeToElement(new XAModCustomSightDistanceSettings
                {
                    MaxDistance = Configuration.CustomSightDistanceMaxDistance,
                    MinDistance = Configuration.CustomSightDistanceMinDistance,
                    MaxRotation = Configuration.CustomSightDistanceMaxRotation,
                    MinRotation = Configuration.CustomSightDistanceMinRotation,
                    MaxFoV = Configuration.CustomSightDistanceMaxFoV,
                    MinFoV = Configuration.CustomSightDistanceMinFoV,
                    CurrentFoV = Configuration.CustomSightDistanceFoV,
                    IgnoreCollision = Configuration.CustomSightDistanceIgnoreCollision,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "infinite-sprint":
                snapshot = JsonSerializer.SerializeToElement(new XAModInfiniteSprintSettings
                {
                    DelaySeconds = Configuration.InfiniteSprintDelaySeconds,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "xa-peep":
                snapshot = JsonSerializer.SerializeToElement(new XAModXAPeepSettings
                {
                    AutoOpenWindowOnPluginLoad = Configuration.XAPeepAutoOpenWindowOnPluginLoad,
                    WindowLocked = Configuration.XAPeepWindowLocked,
                    LogParty = Configuration.XAPeepLogParty,
                    LogAlliance = Configuration.XAPeepLogAlliance,
                    LogInCombat = Configuration.XAPeepLogInCombat,
                    ShowCardWhenTargeted = Configuration.XAPeepDisplayLineWhenTargetingMe,
                    ShowTargeterLine = Configuration.XAPeepShowTargeterLine,
                    TargeterLineColor = CreateColorSettings(
                        Configuration.XAPeepTargeterLineColor.X,
                        Configuration.XAPeepTargeterLineColor.Y,
                        Configuration.XAPeepTargeterLineColor.Z,
                        Configuration.XAPeepTargeterLineColor.W),
                    ShowTargeterDot = Configuration.XAPeepShowTargeterDot,
                    TargeterDotColor = CreateColorSettings(
                        Configuration.XAPeepTargeterDotColor.X,
                        Configuration.XAPeepTargeterDotColor.Y,
                        Configuration.XAPeepTargeterDotColor.Z,
                        Configuration.XAPeepTargeterDotColor.W),
                    TargeterDotSize = Configuration.XAPeepTargeterDotSize,
                    ShowTargetersCard = Configuration.XAPeepShowTargetersCard,
                    ShowCenterNotification = Configuration.XAPeepShowCenterNotification,
                    ShowChatNotification = Configuration.XAPeepShowChatNotification,
                    PlaySound = Configuration.XAPeepPlaySound,
                    SoundEffectId = Configuration.XAPeepSoundEffectId,
                    SoundVolume = Configuration.XAPeepSoundVolume,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            default:
                snapshot = default;
                return false;
        }
    }

    private void ApplyImportedXAModSettings(IReadOnlyDictionary<string, JsonElement>? modSettings)
    {
        if (modSettings == null || modSettings.Count == 0)
            return;

        if (TryDeserializeXAModSettings(modSettings, "disable-background-game-rendering", out XAModDisableBackgroundRenderingSettings? backgroundRenderingSettings)
            && backgroundRenderingSettings != null)
        {
            Configuration.DisableBackgroundGameRenderingOnlyWhenMinimized = backgroundRenderingSettings.OnlyWhenMinimized;
            Configuration.DisableBackgroundGameRenderingDisableWhenArMultiIsOn = backgroundRenderingSettings.DisableWhenArMultiIsOn;
            if (Configuration.DisableBackgroundGameRenderingEnabled)
            {
                SystemWindowMods.SetDisableBackgroundRenderingOnlyWhenMinimized(Configuration.DisableBackgroundGameRenderingOnlyWhenMinimized);
                SystemWindowMods.SetDisableBackgroundRenderingDisableWhenArMultiIsOn(Configuration.DisableBackgroundGameRenderingDisableWhenArMultiIsOn);
            }
        }

        if (TryDeserializeXAModSettings(modSettings, "auto-hide-game-objects", out XAModAutoHideGameObjectsSettings? autoHideSettings)
            && autoHideSettings != null)
        {
            Configuration.AutoHideGameObjectsHidePlayer = autoHideSettings.HidePlayer;
            Configuration.AutoHideGameObjectsHideUnimportantEnpc = autoHideSettings.HideUnimportantEnpc;
            Configuration.AutoHideGameObjectsHidePet = autoHideSettings.HidePet;
            Configuration.AutoHideGameObjectsHideChocobo = autoHideSettings.HideChocobo;
            Configuration.AutoHideGameObjectsDisableInDuties = autoHideSettings.DisableInDuties;
            Configuration.AutoHideGameObjectsDisableInIslandSanctuary = autoHideSettings.DisableInIslandSanctuary;
            Configuration.AutoHideGameObjectsUseOccultCrescentRules = autoHideSettings.UseOccultCrescentRules;
            if (Configuration.AutoHideGameObjectsEnabled)
            {
                AutoHideGameObjects.ApplyConfiguration(
                    Configuration.AutoHideGameObjectsHidePlayer,
                    Configuration.AutoHideGameObjectsHideUnimportantEnpc,
                    Configuration.AutoHideGameObjectsHidePet,
                    Configuration.AutoHideGameObjectsHideChocobo,
                    Configuration.AutoHideGameObjectsDisableInDuties,
                    Configuration.AutoHideGameObjectsDisableInIslandSanctuary,
                    Configuration.AutoHideGameObjectsUseOccultCrescentRules);
            }
        }

        if (TryDeserializeXAModSettings(modSettings, "auto-hide-unnecessary-popups", out XAModPopupCleanerSettings? popupCleanerSettings)
            && popupCleanerSettings != null)
        {
            Configuration.AutoHideUnnecessaryPopupsHideHowToNoticeEnabled = popupCleanerSettings.HideHowToNotice;
            PopupCleaner.ApplyConfiguration(Configuration.AutoHideUnnecessaryPopupsHideHowToNoticeEnabled);
        }

        if (TryDeserializeXAModSettings(modSettings, "custom-resolutions", out XAModCustomResolutionsSettings? customResolutionSettings)
            && customResolutionSettings != null)
        {
            Configuration.CustomResolutionPresets = NormalizeCustomResolutionPresets(customResolutionSettings.Presets);
        }

        if (TryDeserializeXAModSettings(modSettings, "low-resolution", out XAModLowResolutionSettings? lowResolutionSettings)
            && lowResolutionSettings != null)
        {
            Configuration.LowResolutionScale = SystemWindowModsService.ClampLowResolutionScale(lowResolutionSettings.Scale);
            if (Configuration.LowResolutionEnabled)
                SystemWindowMods.ApplyLowResolutionConfiguration(Configuration.LowResolutionScale);
        }

        if (TryDeserializeXAModSettings(modSettings, "special-rendering-modes", out XAModSpecialRenderModesSettings? specialRenderSettings)
            && specialRenderSettings != null)
        {
            Configuration.SpecialRenderModeBackgroundColorR = ClampUnitFloat(specialRenderSettings.BackgroundColorR);
            Configuration.SpecialRenderModeBackgroundColorG = ClampUnitFloat(specialRenderSettings.BackgroundColorG);
            Configuration.SpecialRenderModeBackgroundColorB = ClampUnitFloat(specialRenderSettings.BackgroundColorB);
            Configuration.SpecialRenderModeBackgroundColorA = ClampUnitFloat(specialRenderSettings.BackgroundColorA);
            Configuration.SpecialRenderHideAddonsKeepNameplatesEnabled = specialRenderSettings.HideAddonsKeepNameplates;
            Configuration.SpecialRenderHideAddonsKeepChatEnabled = specialRenderSettings.HideAddonsKeepChat;
            Configuration.SpecialRenderHideChatEnabled = specialRenderSettings.HideChat;
            Configuration.SpecialRenderHideActionBarsEnabled = specialRenderSettings.HideActionBars;
            Configuration.SpecialRenderHideTargetInfoEnabled = specialRenderSettings.HideTargetInfo;
            Configuration.SpecialRenderHideNameplatesEnabled = specialRenderSettings.HideNameplates;
            if (Configuration.SpecialRenderModesEnabled)
                ApplySpecialRenderModesConfiguration();
        }

        if (TryDeserializeXAModSettings(modSettings, "expanded-player-right-click-menu-search", out XAModPlayerSearchSettings? playerSearchSettings)
            && playerSearchSettings != null)
        {
            Configuration.ExpandedPlayerRightClickMenuSearchFflogsEnabled = playerSearchSettings.FflogsEnabled;
            Configuration.ExpandedPlayerRightClickMenuSearchLodestoneEnabled = playerSearchSettings.LodestoneEnabled;
            Configuration.ExpandedPlayerRightClickMenuSearchLalachievementsEnabled = playerSearchSettings.LalachievementsEnabled;
            Configuration.ExpandedPlayerRightClickMenuSearchOpenAllEnabled = playerSearchSettings.OpenAllEnabled;
            if (Configuration.ExpandedPlayerRightClickMenuSearchEnabled)
            {
                PlayerSearchContextMenu.ApplyConfiguration(
                    Configuration.ExpandedPlayerRightClickMenuSearchFflogsEnabled,
                    Configuration.ExpandedPlayerRightClickMenuSearchLodestoneEnabled,
                    Configuration.ExpandedPlayerRightClickMenuSearchLalachievementsEnabled,
                    Configuration.ExpandedPlayerRightClickMenuSearchOpenAllEnabled);
            }
        }

        if (TryDeserializeXAModSettings(modSettings, "auto-expert-delivery", out XAModExpertDeliverySettings? expertDeliverySettings)
            && expertDeliverySettings != null)
        {
            Configuration.AutoUnlockExpertDeliveryAutoSwitchWhenOpen = expertDeliverySettings.AutoSwitchWhenOpen;
            Configuration.AutoUnlockExpertDeliveryDefaultPage = Math.Clamp(expertDeliverySettings.DefaultPage, 0, 2);
            Configuration.AutoUnlockExpertDeliverySkipHq = expertDeliverySettings.SkipHq;
            Configuration.AutoUnlockExpertDeliverySkipMateria = expertDeliverySettings.SkipMateria;
            Configuration.AutoUnlockExpertDeliveryIgnoreSealCap = expertDeliverySettings.IgnoreSealCap;
            if (Configuration.AutoUnlockExpertDeliveryEnabled)
            {
                AutoUnlockExpertDelivery.ApplyConfiguration(
                    Configuration.AutoUnlockExpertDeliveryAutoSwitchWhenOpen,
                    Configuration.AutoUnlockExpertDeliveryDefaultPage,
                    Configuration.AutoUnlockExpertDeliverySkipHq,
                    Configuration.AutoUnlockExpertDeliverySkipMateria,
                    Configuration.AutoUnlockExpertDeliveryIgnoreSealCap);
            }
        }

        if (TryDeserializeXAModSettings(modSettings, "auto-unlock-expert-delivery", out XAModUnlockExpertDeliverySettings? unlockExpertDeliverySettings)
            && unlockExpertDeliverySettings != null)
        {
            Configuration.UnlockExpertDeliveryForcedRankFloor = ExpertDeliveryUnlockService.NormalizeForcedRankFloor(unlockExpertDeliverySettings.ForcedRankFloor);
            ExpertDeliveryUnlock.ApplyConfiguration(Configuration.UnlockExpertDeliveryForcedRankFloor);
        }

        if (TryDeserializeXAModSettings(modSettings, "bailout-esc-menu", out XAModBailoutEscMenuSettings? bailoutEscMenuSettings)
            && bailoutEscMenuSettings != null)
        {
            Configuration.BailoutEscMenuSeconds = EscMenuBailoutService.NormalizeTimeoutSeconds(bailoutEscMenuSettings.TimeoutSeconds);
            if (Configuration.BailoutEscMenuEnabled)
                EscMenuBailout.ApplyConfiguration(Configuration.BailoutEscMenuSeconds);
        }

        if (TryDeserializeXAModSettings(modSettings, "auto-leave-duty", out XAModAutoLeaveDutySettings? autoLeaveDutySettings)
            && autoLeaveDutySettings != null)
        {
            Configuration.AutoLeaveDutyDelaySeconds = AutoLeaveDutyService.ClampDelaySeconds(autoLeaveDutySettings.DelaySeconds);
            if (Configuration.AutoLeaveDutyEnabled)
                AutoLeaveDuty.ApplyConfiguration(Configuration.AutoLeaveDutyDelaySeconds);
        }

        if (TryDeserializeXAModSettings(modSettings, "eureka-instance-id", out XAModEurekaInstanceIdSettings? eurekaInstanceIdSettings)
            && eurekaInstanceIdSettings != null)
        {
            if (eurekaInstanceIdSettings.Zone.HasValue)
                Configuration.EurekaInstanceIdZone = (int)EurekaInstanceIdService.NormalizeZone(eurekaInstanceIdSettings.Zone.Value);
            if (eurekaInstanceIdSettings.BaselineInstanceId.HasValue)
                Configuration.EurekaInstanceIdBaselineInstanceId = EurekaInstanceIdService.NormalizeInstanceId(eurekaInstanceIdSettings.BaselineInstanceId.Value);
            if (eurekaInstanceIdSettings.ShowInDtr.HasValue)
                Configuration.EurekaInstanceIdShowInDtr = eurekaInstanceIdSettings.ShowInDtr.Value;
            if (eurekaInstanceIdSettings.LeaveDutyDelaySeconds.HasValue)
                Configuration.EurekaInstanceIdLeaveDutyDelaySeconds = EurekaInstanceIdService.ClampLeaveDutyDelaySeconds(eurekaInstanceIdSettings.LeaveDutyDelaySeconds.Value);

            var hasPerZoneData = false;
            hasPerZoneData |= ApplyEurekaInstanceIdPresetZoneData(
                Configuration,
                EurekaInstanceIdService.EurekaZone.Anemos,
                eurekaInstanceIdSettings.AnemosEnabled,
                eurekaInstanceIdSettings.AnemosBaselineInstanceId);
            hasPerZoneData |= ApplyEurekaInstanceIdPresetZoneData(
                Configuration,
                EurekaInstanceIdService.EurekaZone.Pagos,
                eurekaInstanceIdSettings.PagosEnabled,
                eurekaInstanceIdSettings.PagosBaselineInstanceId);
            hasPerZoneData |= ApplyEurekaInstanceIdPresetZoneData(
                Configuration,
                EurekaInstanceIdService.EurekaZone.Pyros,
                eurekaInstanceIdSettings.PyrosEnabled,
                eurekaInstanceIdSettings.PyrosBaselineInstanceId);
            hasPerZoneData |= ApplyEurekaInstanceIdPresetZoneData(
                Configuration,
                EurekaInstanceIdService.EurekaZone.Hydatos,
                eurekaInstanceIdSettings.HydatosEnabled,
                eurekaInstanceIdSettings.HydatosBaselineInstanceId);

            if (!hasPerZoneData && eurekaInstanceIdSettings.Zone.HasValue)
            {
                ApplyLegacyEurekaInstanceIdSelection(
                    Configuration,
                    EurekaInstanceIdService.NormalizeZone(eurekaInstanceIdSettings.Zone.Value),
                    eurekaInstanceIdSettings.BaselineInstanceId ?? Configuration.EurekaInstanceIdBaselineInstanceId);
            }

            Configuration.EurekaInstanceIdPlaySound = eurekaInstanceIdSettings.PlaySound;
            Configuration.EurekaInstanceIdSoundEffectId = EurekaInstanceIdService.ClampSoundEffectId(eurekaInstanceIdSettings.SoundEffectId);
            Configuration.EurekaInstanceIdSoundVolume = EurekaInstanceIdService.ClampSoundVolume(eurekaInstanceIdSettings.SoundVolume);
            NormalizeEurekaInstanceIdConfiguration(Configuration);
            EurekaInstanceId.ApplyConfiguration();
        }

        if (TryDeserializeXAModSettings(modSettings, "auto-refuse-trade-request", out XAModTradeRefusalSettings? tradeRefusalSettings)
            && tradeRefusalSettings != null)
        {
            Configuration.AutoRefuseTradeShowNotification = tradeRefusalSettings.ShowNotification;
            Configuration.AutoRefuseTradeSendEcho = tradeRefusalSettings.SendEcho;
            Configuration.AutoRefuseTradeExtraCommands = tradeRefusalSettings.ExtraCommands ?? string.Empty;
            if (Configuration.AutoRefuseTradeRequestEnabled)
            {
                AutoRefuseTrade.ApplyConfiguration(
                    Configuration.AutoRefuseTradeShowNotification,
                    Configuration.AutoRefuseTradeSendEcho,
                    Configuration.AutoRefuseTradeExtraCommands);
            }
        }

        if (TryDeserializeXAModSettings(modSettings, "custom-sight-distance", out XAModCustomSightDistanceSettings? sightDistanceSettings)
            && sightDistanceSettings != null)
        {
            var maxDistance = Math.Clamp(sightDistanceSettings.MaxDistance, 1f, 80f);
            var minDistance = Math.Clamp(sightDistanceSettings.MinDistance, 0f, maxDistance);
            var minRotation = Math.Clamp(sightDistanceSettings.MinRotation, -1.569f, 1.569f);
            var maxRotation = Math.Clamp(sightDistanceSettings.MaxRotation, minRotation, 1.569f);
            var minFoV = Math.Clamp(sightDistanceSettings.MinFoV, 0.01f, 3f);
            var maxFoV = Math.Clamp(sightDistanceSettings.MaxFoV, minFoV, 3f);
            var currentFoV = Math.Clamp(sightDistanceSettings.CurrentFoV, minFoV, maxFoV);

            Configuration.CustomSightDistanceMaxDistance = maxDistance;
            Configuration.CustomSightDistanceMinDistance = minDistance;
            Configuration.CustomSightDistanceMaxRotation = maxRotation;
            Configuration.CustomSightDistanceMinRotation = minRotation;
            Configuration.CustomSightDistanceMaxFoV = maxFoV;
            Configuration.CustomSightDistanceMinFoV = minFoV;
            Configuration.CustomSightDistanceFoV = currentFoV;
            Configuration.CustomSightDistanceIgnoreCollision = sightDistanceSettings.IgnoreCollision;
            if (Configuration.CustomSightDistanceEnabled)
            {
                SightDistance.ApplyConfiguration(
                    Configuration.CustomSightDistanceMaxDistance,
                    Configuration.CustomSightDistanceMinDistance,
                    Configuration.CustomSightDistanceMaxRotation,
                    Configuration.CustomSightDistanceMinRotation,
                    Configuration.CustomSightDistanceMaxFoV,
                    Configuration.CustomSightDistanceMinFoV,
                    Configuration.CustomSightDistanceFoV,
                    Configuration.CustomSightDistanceIgnoreCollision);
            }
        }

        if (TryDeserializeXAModSettings(modSettings, "infinite-sprint", out XAModInfiniteSprintSettings? infiniteSprintSettings)
            && infiniteSprintSettings != null)
        {
            Configuration.InfiniteSprintDelaySeconds = PlayerModsService.ClampInfiniteSprintDelaySeconds(infiniteSprintSettings.DelaySeconds);
            if (Configuration.InfiniteSprintEnabled)
                PlayerMods.ApplyInfiniteSprintConfiguration(Configuration.InfiniteSprintDelaySeconds);
        }

        if (TryDeserializeXAModSettings(modSettings, "xa-peep", out XAModXAPeepSettings? xaPeepSettings)
            && xaPeepSettings != null)
        {
            var soundEffectId = XAPeepData.ClampSoundEffectId(xaPeepSettings.SoundEffectId);
            if (xaPeepSettings.PlaySound && soundEffectId == 0)
                soundEffectId = 2;
            var targeterLineColor = xaPeepSettings.TargeterLineColor ?? new XAModColorSettings();
            var targeterDotColor = xaPeepSettings.TargeterDotColor ?? new XAModColorSettings();

            Configuration.XAPeepAutoOpenWindowOnPluginLoad = xaPeepSettings.AutoOpenWindowOnPluginLoad;
            Configuration.XAPeepWindowLocked = xaPeepSettings.WindowLocked;
            Configuration.XAPeepLogParty = xaPeepSettings.LogParty;
            Configuration.XAPeepLogAlliance = xaPeepSettings.LogAlliance;
            Configuration.XAPeepLogInCombat = xaPeepSettings.LogInCombat;
            Configuration.XAPeepDisplayLineWhenTargetingMe = xaPeepSettings.ShowCardWhenTargeted;
            Configuration.XAPeepShowTargeterLine = xaPeepSettings.ShowTargeterLine;
            Configuration.XAPeepTargeterLineColor = new Vector4(
                ClampUnitFloat(targeterLineColor.R),
                ClampUnitFloat(targeterLineColor.G),
                ClampUnitFloat(targeterLineColor.B),
                ClampUnitFloat(targeterLineColor.A));
            Configuration.XAPeepShowTargeterDot = xaPeepSettings.ShowTargeterDot;
            Configuration.XAPeepTargeterDotColor = new Vector4(
                ClampUnitFloat(targeterDotColor.R),
                ClampUnitFloat(targeterDotColor.G),
                ClampUnitFloat(targeterDotColor.B),
                ClampUnitFloat(targeterDotColor.A));
            Configuration.XAPeepTargeterDotSize = Math.Clamp(xaPeepSettings.TargeterDotSize, 1f, 15f);
            Configuration.XAPeepShowTargetersCard = xaPeepSettings.ShowTargetersCard;
            Configuration.XAPeepShowCenterNotification = xaPeepSettings.ShowCenterNotification;
            Configuration.XAPeepShowChatNotification = xaPeepSettings.ShowChatNotification;
            Configuration.XAPeepPlaySound = xaPeepSettings.PlaySound && soundEffectId > 0;
            Configuration.XAPeepSoundEffectId = soundEffectId;
            Configuration.XAPeepSoundVolume = Math.Clamp(xaPeepSettings.SoundVolume, 0f, 1f);
        }
    }

    private static bool TryDeserializeXAModSettings<T>(IReadOnlyDictionary<string, JsonElement>? modSettings, string key, out T? settings)
    {
        settings = default;
        if (!TryGetXAModSettingsElement(modSettings, key, out var element))
            return false;

        try
        {
            settings = element.Deserialize<T>(ToonModsPresetSerialization.JsonOptions);
            return settings != null;
        }
        catch
        {
            settings = default;
            return false;
        }
    }

    private static bool TryGetXAModSettingsElement(IReadOnlyDictionary<string, JsonElement>? modSettings, string key, out JsonElement element)
    {
        if (modSettings != null)
        {
            foreach (var entry in modSettings)
            {
                if (entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    element = entry.Value;
                    return true;
                }
            }
        }

        element = default;
        return false;
    }

    private static List<XAModResolutionPreset> NormalizeCustomResolutionPresets(IEnumerable<XAModResolutionPreset>? presets)
    {
        var normalized = new List<XAModResolutionPreset>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var preset in presets ?? Enumerable.Empty<XAModResolutionPreset>())
        {
            if (!SystemWindowModsService.TryNormalizeCustomResolution(preset.Width, preset.Height, out var width, out var height, out _))
                continue;

            var key = $"{width}x{height}";
            if (!seen.Add(key))
                continue;

            normalized.Add(new XAModResolutionPreset
            {
                Width = width,
                Height = height,
            });
        }

        return normalized;
    }

    private static XAModColorSettings CreateColorSettings(float r, float g, float b, float a)
    {
        return new XAModColorSettings
        {
            R = ClampUnitFloat(r),
            G = ClampUnitFloat(g),
            B = ClampUnitFloat(b),
            A = ClampUnitFloat(a),
        };
    }

    private static float ClampUnitFloat(float value)
        => Math.Clamp(value, 0f, 1f);

    private bool TryHandleResolutionCommand(string value, out string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            message = "Usage: /xa res <width>x<height>, /xa res add <width>x<height>, or /xa res remove <width>x<height>.";
            return false;
        }

        if (value.StartsWith("add ", StringComparison.OrdinalIgnoreCase))
            return TryAddCustomResolutionPreset(value[4..].Trim(), out message);

        if (value.StartsWith("remove ", StringComparison.OrdinalIgnoreCase))
            return TryRemoveCustomResolutionPreset(value[7..].Trim(), out message);

        if (!TryParseResolutionCommand(value, out var width, out var height))
        {
            message = "Usage: /xa res <width>x<height>, /xa res add <width>x<height>, or /xa res remove <width>x<height>.";
            return false;
        }

        return SystemWindowMods.TryApplyCustomResolution(width, height, out message);
    }

    private bool TryAddCustomResolutionPreset(string value, out string message)
    {
        if (!TryParseResolutionCommand(value, out var width, out var height))
        {
            message = "Usage: /xa res add <width>x<height>.";
            return false;
        }

        if (!SystemWindowModsService.TryNormalizeCustomResolution(width, height, out var normalizedWidth, out var normalizedHeight, out message))
            return false;

        if (Configuration.CustomResolutionPresets.Any(entry => entry.Width == normalizedWidth && entry.Height == normalizedHeight))
        {
            message = $"Custom resolution button {normalizedWidth}x{normalizedHeight} already exists.";
            return false;
        }

        Configuration.CustomResolutionPresets.Add(new XAModResolutionPreset
        {
            Width = normalizedWidth,
            Height = normalizedHeight,
        });
        Configuration.Save();
        message = $"Added custom resolution button {normalizedWidth}x{normalizedHeight}.";
        return true;
    }

    private bool TryRemoveCustomResolutionPreset(string value, out string message)
    {
        if (!TryParseResolutionCommand(value, out var width, out var height))
        {
            message = "Usage: /xa res remove <width>x<height>.";
            return false;
        }

        if (!SystemWindowModsService.TryNormalizeCustomResolution(width, height, out var normalizedWidth, out var normalizedHeight, out message))
            return false;

        var removedCount = Configuration.CustomResolutionPresets.RemoveAll(entry => entry.Width == normalizedWidth && entry.Height == normalizedHeight);
        if (removedCount == 0)
        {
            message = $"No custom resolution button {normalizedWidth}x{normalizedHeight} was found.";
            return false;
        }

        Configuration.Save();
        message = $"Removed custom resolution button {normalizedWidth}x{normalizedHeight}.";
        return true;
    }

    private bool TryApplySprintDelayCommand(string value, out string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            message = "Usage: /xa sprintdelay <seconds>.";
            return false;
        }

        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var delaySeconds))
        {
            message = "Usage: /xa sprintdelay <seconds>.";
            return false;
        }

        if (delaySeconds < PlayerModsService.InfiniteSprintDelaySecondsMinimum
            || delaySeconds > PlayerModsService.InfiniteSprintDelaySecondsMaximum)
        {
            message = $"Infinite Sprint delay must be between {PlayerModsService.InfiniteSprintDelaySecondsMinimum:0.0} and {PlayerModsService.InfiniteSprintDelaySecondsMaximum:0.0} seconds.";
            return false;
        }

        delaySeconds = PlayerModsService.ClampInfiniteSprintDelaySeconds(delaySeconds);
        Configuration.InfiniteSprintDelaySeconds = delaySeconds;
        PlayerMods.ApplyInfiniteSprintConfiguration(delaySeconds);
        Configuration.Save();
        message = Configuration.InfiniteSprintEnabled
            ? $"Infinite Sprint delay set to {delaySeconds:0.0#} seconds."
            : $"Infinite Sprint delay saved as {delaySeconds:0.0#} seconds.";
        return true;
    }

    private void PrintCommandResult(bool success, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        if (success)
            ChatGui.Print($"[XASlave] {message}");
        else
            ChatGui.PrintError($"[XASlave] {message}");
    }

    private static bool MigrateLegacyCharacterListAnonymizeState(Configuration configuration)
    {
        var hasLegacyFlagsEnabled = configuration.ReloggerAnonymizeCharacters
            || configuration.RefreshSubsAnonymizeCharacters
            || configuration.PrepLogisticsAnonymizeCharacters
            || configuration.XagmanTonyAnonymizeCharacters
            || configuration.XagmanFranchiseAnonymizeCharacters
            || configuration.FcPermsAnonymizeCharacters
            || configuration.DupPlotsAnonymizeCharacters
            || configuration.ReturnAltsAnonymizeCharacters;

        if (!hasLegacyFlagsEnabled)
            return false;

        var changed = false;
        if (!configuration.GlobalCharacterListAnonymizeEnabled)
        {
            configuration.GlobalCharacterListAnonymizeEnabled = true;
            changed = true;
        }

        if (configuration.ReloggerAnonymizeCharacters)
        {
            configuration.ReloggerAnonymizeCharacters = false;
            changed = true;
        }

        if (configuration.RefreshSubsAnonymizeCharacters)
        {
            configuration.RefreshSubsAnonymizeCharacters = false;
            changed = true;
        }

        if (configuration.PrepLogisticsAnonymizeCharacters)
        {
            configuration.PrepLogisticsAnonymizeCharacters = false;
            changed = true;
        }

        if (configuration.XagmanTonyAnonymizeCharacters)
        {
            configuration.XagmanTonyAnonymizeCharacters = false;
            changed = true;
        }

        if (configuration.XagmanFranchiseAnonymizeCharacters)
        {
            configuration.XagmanFranchiseAnonymizeCharacters = false;
            changed = true;
        }

        if (configuration.FcPermsAnonymizeCharacters)
        {
            configuration.FcPermsAnonymizeCharacters = false;
            changed = true;
        }

        if (configuration.DupPlotsAnonymizeCharacters)
        {
            configuration.DupPlotsAnonymizeCharacters = false;
            changed = true;
        }

        if (configuration.ReturnAltsAnonymizeCharacters)
        {
            configuration.ReturnAltsAnonymizeCharacters = false;
            changed = true;
        }

        return changed;
    }

    private static bool MigrateLegacyEurekaInstanceIdState(Configuration configuration)
    {
        var changed = NormalizeEurekaInstanceIdConfiguration(configuration);
        if (HasAnyEurekaInstanceIdZoneEnabled(configuration))
            return changed;

        if (!configuration.EurekaInstanceIdEnabled && configuration.EurekaInstanceIdBaselineInstanceId <= 0)
            return changed;

        ApplyLegacyEurekaInstanceIdSelection(
            configuration,
            EurekaInstanceIdService.NormalizeZone(configuration.EurekaInstanceIdZone),
            configuration.EurekaInstanceIdBaselineInstanceId);
        return true;
    }

    private static bool ApplyEurekaLogogramCreatorDefaultSettings(Configuration configuration)
    {
        if (configuration.EurekaLogogramCreatorDefaultSettingsMigrationApplied)
            return false;

        var changed = false;
        var looksUntouched =
            !configuration.AutoRefreshAllPagesOnOpen
            && !configuration.AutoDestroyWhenMagiaBoardFull
            && !configuration.AutoRetryFailedExtraction
            && configuration.ShowFavoritesOverlay
            && configuration.QueueStepFrameDelay == 10;

        if (looksUntouched)
        {
            configuration.AutoRefreshAllPagesOnOpen = true;
            configuration.AutoDestroyWhenMagiaBoardFull = false;
            configuration.AutoRetryFailedExtraction = false;
            configuration.ShowFavoritesOverlay = true;
            configuration.QueueStepFrameDelay = 20;
            changed = true;
        }

        configuration.EurekaLogogramCreatorDefaultSettingsMigrationApplied = true;
        changed = true;
        return changed;
    }

    private static bool NormalizeEurekaInstanceIdConfiguration(Configuration configuration)
    {
        var changed = false;

        var normalizedLegacyZone = (int)EurekaInstanceIdService.NormalizeZone(configuration.EurekaInstanceIdZone);
        if (configuration.EurekaInstanceIdZone != normalizedLegacyZone)
        {
            configuration.EurekaInstanceIdZone = normalizedLegacyZone;
            changed = true;
        }

        var normalizedLegacyBaseline = EurekaInstanceIdService.NormalizeInstanceId(configuration.EurekaInstanceIdBaselineInstanceId);
        if (configuration.EurekaInstanceIdBaselineInstanceId != normalizedLegacyBaseline)
        {
            configuration.EurekaInstanceIdBaselineInstanceId = normalizedLegacyBaseline;
            changed = true;
        }

        var normalizedLeaveDutyDelay = EurekaInstanceIdService.ClampLeaveDutyDelaySeconds(configuration.EurekaInstanceIdLeaveDutyDelaySeconds);
        if (configuration.EurekaInstanceIdLeaveDutyDelaySeconds != normalizedLeaveDutyDelay)
        {
            configuration.EurekaInstanceIdLeaveDutyDelaySeconds = normalizedLeaveDutyDelay;
            changed = true;
        }

        var normalizedSoundEffectId = EurekaInstanceIdService.ClampSoundEffectId(configuration.EurekaInstanceIdSoundEffectId);
        if (configuration.EurekaInstanceIdSoundEffectId != normalizedSoundEffectId)
        {
            configuration.EurekaInstanceIdSoundEffectId = normalizedSoundEffectId;
            changed = true;
        }

        var normalizedSoundVolume = EurekaInstanceIdService.ClampSoundVolume(configuration.EurekaInstanceIdSoundVolume);
        if (Math.Abs(configuration.EurekaInstanceIdSoundVolume - normalizedSoundVolume) > 0.001f)
        {
            configuration.EurekaInstanceIdSoundVolume = normalizedSoundVolume;
            changed = true;
        }

        changed |= NormalizeEurekaInstanceIdZoneBaseline(configuration, EurekaInstanceIdService.EurekaZone.Anemos);
        changed |= NormalizeEurekaInstanceIdZoneBaseline(configuration, EurekaInstanceIdService.EurekaZone.Pagos);
        changed |= NormalizeEurekaInstanceIdZoneBaseline(configuration, EurekaInstanceIdService.EurekaZone.Pyros);
        changed |= NormalizeEurekaInstanceIdZoneBaseline(configuration, EurekaInstanceIdService.EurekaZone.Hydatos);
        return changed;
    }

    private static bool NormalizeEurekaInstanceIdZoneBaseline(Configuration configuration, EurekaInstanceIdService.EurekaZone zone)
    {
        var current = GetEurekaInstanceIdZoneBaseline(configuration, zone);
        var normalized = EurekaInstanceIdService.NormalizeInstanceId(current);
        if (current == normalized)
            return false;

        SetEurekaInstanceIdZoneBaseline(configuration, zone, normalized);
        return true;
    }

    private static bool ApplyEurekaInstanceIdPresetZoneData(
        Configuration configuration,
        EurekaInstanceIdService.EurekaZone zone,
        bool? enabled,
        int? baselineInstanceId)
    {
        var hasData = false;
        if (enabled.HasValue)
        {
            SetEurekaInstanceIdZoneEnabled(configuration, zone, enabled.Value);
            hasData = true;
        }

        if (baselineInstanceId.HasValue)
        {
            SetEurekaInstanceIdZoneBaseline(configuration, zone, EurekaInstanceIdService.NormalizeInstanceId(baselineInstanceId.Value));
            hasData = true;
        }

        return hasData;
    }

    private static void ApplyLegacyEurekaInstanceIdSelection(
        Configuration configuration,
        EurekaInstanceIdService.EurekaZone zone,
        int baselineInstanceId)
    {
        var normalizedBaseline = EurekaInstanceIdService.NormalizeInstanceId(baselineInstanceId);
        SetEurekaInstanceIdZoneEnabled(configuration, zone, true);
        SetEurekaInstanceIdZoneBaseline(configuration, zone, normalizedBaseline);
        configuration.EurekaInstanceIdZone = (int)zone;
        configuration.EurekaInstanceIdBaselineInstanceId = normalizedBaseline;
    }

    private static bool HasAnyEurekaInstanceIdZoneEnabled(Configuration configuration)
    {
        return configuration.EurekaInstanceIdAnemosEnabled
            || configuration.EurekaInstanceIdPagosEnabled
            || configuration.EurekaInstanceIdPyrosEnabled
            || configuration.EurekaInstanceIdHydatosEnabled;
    }

    private static void SetEurekaInstanceIdZoneEnabled(Configuration configuration, EurekaInstanceIdService.EurekaZone zone, bool enabled)
    {
        switch (zone)
        {
            case EurekaInstanceIdService.EurekaZone.Anemos:
                configuration.EurekaInstanceIdAnemosEnabled = enabled;
                break;
            case EurekaInstanceIdService.EurekaZone.Pagos:
                configuration.EurekaInstanceIdPagosEnabled = enabled;
                break;
            case EurekaInstanceIdService.EurekaZone.Pyros:
                configuration.EurekaInstanceIdPyrosEnabled = enabled;
                break;
            case EurekaInstanceIdService.EurekaZone.Hydatos:
                configuration.EurekaInstanceIdHydatosEnabled = enabled;
                break;
        }
    }

    private static int GetEurekaInstanceIdZoneBaseline(Configuration configuration, EurekaInstanceIdService.EurekaZone zone)
    {
        return zone switch
        {
            EurekaInstanceIdService.EurekaZone.Anemos => configuration.EurekaInstanceIdAnemosBaselineInstanceId,
            EurekaInstanceIdService.EurekaZone.Pagos => configuration.EurekaInstanceIdPagosBaselineInstanceId,
            EurekaInstanceIdService.EurekaZone.Pyros => configuration.EurekaInstanceIdPyrosBaselineInstanceId,
            EurekaInstanceIdService.EurekaZone.Hydatos => configuration.EurekaInstanceIdHydatosBaselineInstanceId,
            _ => 0,
        };
    }

    private static void SetEurekaInstanceIdZoneBaseline(Configuration configuration, EurekaInstanceIdService.EurekaZone zone, int baselineInstanceId)
    {
        switch (zone)
        {
            case EurekaInstanceIdService.EurekaZone.Anemos:
                configuration.EurekaInstanceIdAnemosBaselineInstanceId = baselineInstanceId;
                break;
            case EurekaInstanceIdService.EurekaZone.Pagos:
                configuration.EurekaInstanceIdPagosBaselineInstanceId = baselineInstanceId;
                break;
            case EurekaInstanceIdService.EurekaZone.Pyros:
                configuration.EurekaInstanceIdPyrosBaselineInstanceId = baselineInstanceId;
                break;
            case EurekaInstanceIdService.EurekaZone.Hydatos:
                configuration.EurekaInstanceIdHydatosBaselineInstanceId = baselineInstanceId;
                break;
        }
    }

    internal bool IsAutoRetainerMultiModeEnabled()
        => IpcClient.AutoRetainerGetMultiModeEnabled();

    internal bool CanEnableSpecialRenderHideChat(out string message)
    {
        if (IsAutoRetainerMultiModeEnabled())
        {
            message = "Hide Chat is blocked while AutoRetainer Multi Mode is enabled.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    internal bool IsSpecialRenderChatCurrentlyHidden()
    {
        if (!Configuration.SpecialRenderModesEnabled)
            return false;

        return Configuration.SpecialRenderHideChatEnabled
            || Configuration.SpecialRenderHideAddonsKeepNameplatesEnabled;
    }

    internal bool CanTriggerLogoutActions(out string message)
    {
        if (IsSpecialRenderChatCurrentlyHidden())
        {
            message = "Logout and kill-game actions are blocked while Special Rendering Modes is hiding chat. Restore chat first.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    internal bool TryRequestLogoutAction(out string message)
    {
        if (!CanTriggerLogoutActions(out message))
            return false;

        var success = InstantLogout.RequestLogout();
        message = success ? string.Empty : InstantLogout.StatusText;
        return success;
    }

    internal bool TryRequestKillGameAction(out string message)
    {
        if (!CanTriggerLogoutActions(out message))
            return false;

        var success = InstantLogout.RequestKillGame();
        message = success ? string.Empty : InstantLogout.StatusText;
        return success;
    }

    public void DisableAllXAMods()
    {
        DisableXAModDefinitions(GetAllXAModDefinitions());
        Configuration.Save();
    }

    public bool LoadModListPreset(string name, out string message)
        => LoadSavedXAModsPreset(name, out message);

    public IReadOnlyList<string> GetSavedModListNames()
        => Configuration.ToonModsSavedLists.Select(entry => entry.Name).ToList();

    public IReadOnlyList<TitleBarFavXAModInfo> GetTitleBarFavXAModInfos()
    {
        return GetAllXAModDefinitions()
            .Select(definition => new TitleBarFavXAModInfo(
                definition.Key,
                definition.DisplayName,
                GetXAModsRestoreScopeLabel(definition.Scope)))
            .ToList();
    }

    public bool TryGetTitleBarFavXAModInfo(string key, out TitleBarFavXAModInfo info)
    {
        var definition = GetAllXAModDefinitions()
            .FirstOrDefault(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

        if (definition.Key == null)
        {
            info = default;
            return false;
        }

        info = new TitleBarFavXAModInfo(
            definition.Key,
            definition.DisplayName,
            GetXAModsRestoreScopeLabel(definition.Scope));
        return true;
    }

    public bool IsXAModEnabled(string key)
        => GetXAModDefinition(key).GetCurrent();

    public bool HasAnyEnabledXAMods()
        => GetAllXAModDefinitions().Any(definition => definition.GetCurrent());

    public bool SetXAModEnabledByKey(string key, bool enabled, out string message)
        => SetXAModEnabled(GetXAModDefinition(key), enabled, out message);

    public bool ToggleXAModByKey(string key, out bool enabled, out string message)
    {
        var definition = GetXAModDefinition(key);
        var targetState = !definition.GetCurrent();
        var success = SetXAModEnabled(definition, targetState, out message);
        enabled = definition.GetCurrent();
        return success;
    }

    private void ApplyStoredXAModConfiguration(string key)
    {
        switch (key.ToLowerInvariant())
        {
            case "disable-background-game-rendering":
                SystemWindowMods.SetDisableBackgroundRenderingOnlyWhenMinimized(Configuration.DisableBackgroundGameRenderingOnlyWhenMinimized);
                SystemWindowMods.SetDisableBackgroundRenderingDisableWhenArMultiIsOn(Configuration.DisableBackgroundGameRenderingDisableWhenArMultiIsOn);
                break;
            case "auto-hide-game-objects":
                AutoHideGameObjects.ApplyConfiguration(
                    Configuration.AutoHideGameObjectsHidePlayer,
                    Configuration.AutoHideGameObjectsHideUnimportantEnpc,
                    Configuration.AutoHideGameObjectsHidePet,
                    Configuration.AutoHideGameObjectsHideChocobo,
                    Configuration.AutoHideGameObjectsDisableInDuties,
                    Configuration.AutoHideGameObjectsDisableInIslandSanctuary,
                    Configuration.AutoHideGameObjectsUseOccultCrescentRules);
                break;
            case "auto-hide-unnecessary-popups":
                PopupCleaner.ApplyConfiguration(Configuration.AutoHideUnnecessaryPopupsHideHowToNoticeEnabled);
                break;
            case "low-resolution":
                SystemWindowMods.ApplyLowResolutionConfiguration(Configuration.LowResolutionScale);
                break;
            case "expanded-player-right-click-menu-search":
                PlayerSearchContextMenu.ApplyConfiguration(
                    Configuration.ExpandedPlayerRightClickMenuSearchFflogsEnabled,
                    Configuration.ExpandedPlayerRightClickMenuSearchLodestoneEnabled,
                    Configuration.ExpandedPlayerRightClickMenuSearchLalachievementsEnabled,
                    Configuration.ExpandedPlayerRightClickMenuSearchOpenAllEnabled);
                break;
            case "auto-expert-delivery":
                AutoUnlockExpertDelivery.ApplyConfiguration(
                    Configuration.AutoUnlockExpertDeliveryAutoSwitchWhenOpen,
                    Configuration.AutoUnlockExpertDeliveryDefaultPage,
                    Configuration.AutoUnlockExpertDeliverySkipHq,
                    Configuration.AutoUnlockExpertDeliverySkipMateria,
                    Configuration.AutoUnlockExpertDeliveryIgnoreSealCap);
                break;
            case "auto-unlock-expert-delivery":
                ExpertDeliveryUnlock.ApplyConfiguration(Configuration.UnlockExpertDeliveryForcedRankFloor);
                break;
            case "bailout-esc-menu":
                EscMenuBailout.ApplyConfiguration(Configuration.BailoutEscMenuSeconds);
                break;
            case "auto-leave-duty":
                AutoLeaveDuty.ApplyConfiguration(Configuration.AutoLeaveDutyDelaySeconds);
                break;
            case "eureka-instance-id":
                NormalizeEurekaInstanceIdConfiguration(Configuration);
                EurekaInstanceId.ApplyConfiguration();
                break;
            case "auto-refuse-trade-request":
                AutoRefuseTrade.ApplyConfiguration(
                    Configuration.AutoRefuseTradeShowNotification,
                    Configuration.AutoRefuseTradeSendEcho,
                    Configuration.AutoRefuseTradeExtraCommands);
                break;
            case "custom-sight-distance":
                SightDistance.ApplyConfiguration(
                    Configuration.CustomSightDistanceMaxDistance,
                    Configuration.CustomSightDistanceMinDistance,
                    Configuration.CustomSightDistanceMaxRotation,
                    Configuration.CustomSightDistanceMinRotation,
                    Configuration.CustomSightDistanceMaxFoV,
                    Configuration.CustomSightDistanceMinFoV,
                    Configuration.CustomSightDistanceFoV,
                    Configuration.CustomSightDistanceIgnoreCollision);
                break;
            case "infinite-sprint":
                PlayerMods.ApplyInfiniteSprintConfiguration(Configuration.InfiniteSprintDelaySeconds);
                break;
        }
    }

    private bool RestoreAllXAMods(out string message)
    {
        var disabledCount = DisableXAModDefinitions(GetAllXAModDefinitions());
        Configuration.Save();
        message = disabledCount > 0
            ? $"Disabled {disabledCount} XA Mods toggle(s)."
            : "XA Mods were already off.";
        return true;
    }

    private bool RestoreXAModsSection(XAModsRestoreScope scope, out string message)
    {
        var disabledCount = DisableXAModDefinitions(GetAllXAModDefinitions().Where(definition => definition.Scope == scope));
        Configuration.Save();
        message = disabledCount > 0
            ? $"Disabled {disabledCount} {GetXAModsRestoreScopeLabel(scope)} toggle(s)."
            : $"{GetXAModsRestoreScopeLabel(scope)} were already off.";
        return true;
    }

    private static int DisableXAModDefinitions(IEnumerable<XAModCommandDefinition> definitions)
    {
        var disabledCount = 0;
        foreach (var definition in definitions)
        {
            if (definition.GetCurrent())
                disabledCount++;

            definition.Apply(false);
            definition.Store(false);
        }

        return disabledCount;
    }

    private bool TryHandleXAPeepCommand(string args, out string message)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)
            || trimmed.Equals("open", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("toggle", StringComparison.OrdinalIgnoreCase))
        {
            ToggleXAPeepUi();
            message = XAPeepWindow.IsOpen ? "Opened XA Peep." : "Closed XA Peep.";
            return true;
        }

        if (trimmed.Equals("close", StringComparison.OrdinalIgnoreCase))
        {
            XAPeepWindow.IsOpen = false;
            message = "Closed XA Peep.";
            return true;
        }

        if (trimmed.Equals("clear", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("clearhistory", StringComparison.OrdinalIgnoreCase))
        {
            XAPeep.ClearHistory();
            message = "Cleared XA Peep history.";
            return true;
        }

        if (!TryParseToggleCommandState(trimmed, out var enabled, out var parseMessage))
        {
            message = $"{parseMessage} Usage: /xa peep [on|off|open|close|clear]";
            return false;
        }

        return SetXAModEnabled(GetXAModDefinition("xa-peep"), enabled, out message);
    }

    private XAModCommandDefinition GetXAModDefinition(string key)
    {
        return GetAllXAModDefinitions()
            .First(definition => definition.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<XAModCommandDefinition> GetAllXAModDefinitions()
    {
        yield return new("auto-allow-multiple-game-instances", "Allow Multiple Game Instances", XAModsRestoreScope.Game, () => Configuration.AutoAllowMultipleGameInstancesEnabled, SystemWindowMods.SetAllowMultipleGameInstancesEnabled, applied => Configuration.AutoAllowMultipleGameInstancesEnabled = applied, () => SystemWindowMods.AllowMultipleGameInstancesStatusText);
        yield return new("auto-cancel-login-cooldown", "Cancel Login Cooldown", XAModsRestoreScope.Game, () => Configuration.AutoCancelLoginCooldownEnabled, SystemWindowMods.SetCancelLoginCooldownEnabled, applied => Configuration.AutoCancelLoginCooldownEnabled = applied, () => SystemWindowMods.CancelLoginCooldownStatusText);
        yield return new("auto-display-msq-progress", "Display MSQ Progress", XAModsRestoreScope.Game, () => Configuration.AutoDisplayMsqProgressEnabled, MsqProgressDisplay.SetEnabled, applied => Configuration.AutoDisplayMsqProgressEnabled = applied, () => MsqProgressDisplay.StatusText);
        yield return new("auto-skip-cutscenes", "Skip Cutscenes", XAModsRestoreScope.Game, () => Configuration.AutoSkipCutscenesEnabled, AutoSkipCutscenes.SetEnabled, applied => Configuration.AutoSkipCutscenesEnabled = applied, () => AutoSkipCutscenes.StatusText);
        yield return new("auto-skip-cutscenes-feeding-chocobo", "Skip Cutscenes Feeding Chocobo", XAModsRestoreScope.Game, () => Configuration.AutoSkipCutscenesFeedingChocoboEnabled, BuddyFeedCutsceneSkip.SetEnabled, applied => Configuration.AutoSkipCutscenesFeedingChocoboEnabled = applied, () => BuddyFeedCutsceneSkip.StatusText);
        yield return new("auto-hide-unnecessary-popups", "Hide Unnecessary Popups", XAModsRestoreScope.Game, () => Configuration.AutoHideUnnecessaryPopupsEnabled, PopupCleaner.SetEnabled, applied => Configuration.AutoHideUnnecessaryPopupsEnabled = applied, () => PopupCleaner.StatusText);
        yield return new("auto-prevent-game-exiting-from-lobby-errors", "Prevent Game Exiting From Lobby Errors", XAModsRestoreScope.Game, () => Configuration.AutoPreventGameExitingFromLobbyErrorsEnabled, SystemWindowMods.SetPreventLobbyExitEnabled, applied => Configuration.AutoPreventGameExitingFromLobbyErrorsEnabled = applied, () => SystemWindowMods.PreventLobbyExitStatusText);
        yield return new("auto-close-lobby-errors", "Close Lobby Errors", XAModsRestoreScope.Game, () => Configuration.AutoCloseLobbyErrorsEnabled, LobbyErrorAutoClose.SetEnabled, applied => Configuration.AutoCloseLobbyErrorsEnabled = applied, () => LobbyErrorAutoClose.StatusText);
        yield return new("bailout-esc-menu", "Bailout ESC Menu", XAModsRestoreScope.Game, () => Configuration.BailoutEscMenuEnabled, EscMenuBailout.SetEnabled, applied => Configuration.BailoutEscMenuEnabled = applied, () => EscMenuBailout.StatusText);
        yield return new("auto-skip-dialogue", "Skip Dialogue", XAModsRestoreScope.Game, () => Configuration.AutoSkipDialogueEnabled, DialogueSkip.SetEnabled, applied => Configuration.AutoSkipDialogueEnabled = applied, () => DialogueSkip.StatusText);
        yield return new("display-actual-queue-position", "Display Actual Queue Position", XAModsRestoreScope.Game, () => Configuration.DisplayActualQueuePositionEnabled, QueuePositionDisplay.SetEnabled, applied => Configuration.DisplayActualQueuePositionEnabled = applied, () => QueuePositionDisplay.StatusText);
        yield return new("target-command-fix", "Fix /target Command", XAModsRestoreScope.Game, () => Configuration.TargetCommandFixEnabled, TargetCommandFix.SetEnabled, applied => Configuration.TargetCommandFixEnabled = applied, () => TargetCommandFix.StatusText);
        yield return new("copy-item-name-for-all", "Copy Item Name For All", XAModsRestoreScope.Game, () => Configuration.CopyItemNameForAllEnabled, CopyItemNameContextMenu.SetEnabled, applied => Configuration.CopyItemNameForAllEnabled = applied, () => CopyItemNameContextMenu.StatusText);
        yield return new("expanded-player-right-click-menu-search", "Expanded Player Right-Click Menu Search", XAModsRestoreScope.Game, () => Configuration.ExpandedPlayerRightClickMenuSearchEnabled, PlayerSearchContextMenu.SetEnabled, applied => Configuration.ExpandedPlayerRightClickMenuSearchEnabled = applied, () => PlayerSearchContextMenu.StatusText);
        yield return new("live-anonymous-mode", "Live Anonymous Mode", XAModsRestoreScope.Game, () => Configuration.LiveAnonymousModeEnabled, NameplatePrivacy.SetAnonymousModeEnabled, applied => Configuration.LiveAnonymousModeEnabled = applied, () => NameplatePrivacy.AnonymousModeStatusText);

        yield return new("auto-ignore-minimum-window-size", "Ignore Minimum Window Size", XAModsRestoreScope.Graphic, () => Configuration.AutoIgnoreMinimumWindowSizeEnabled, SystemWindowMods.SetIgnoreMinimumWindowSizeEnabled, applied => Configuration.AutoIgnoreMinimumWindowSizeEnabled = applied, () => SystemWindowMods.IgnoreMinimumWindowSizeStatusText);
        yield return new("auto-hide-game-objects", "Hide Game Objects", XAModsRestoreScope.Graphic, () => Configuration.AutoHideGameObjectsEnabled, AutoHideGameObjects.SetEnabled, applied => Configuration.AutoHideGameObjectsEnabled = applied, () => AutoHideGameObjects.StatusText);
        yield return new("custom-resolutions", "Custom Resolutions", XAModsRestoreScope.Graphic, () => Configuration.CustomResolutionsEnabled, SystemWindowMods.SetCustomResolutionsEnabled, applied => Configuration.CustomResolutionsEnabled = applied, () => SystemWindowMods.CustomResolutionsStatusText);
        yield return new("disable-background-game-rendering", "Disable Background Rendering", XAModsRestoreScope.Graphic, () => Configuration.DisableBackgroundGameRenderingEnabled, SystemWindowMods.SetDisableBackgroundRenderingEnabled, applied => Configuration.DisableBackgroundGameRenderingEnabled = applied, () => SystemWindowMods.DisableBackgroundRenderingStatusText);
        yield return new("low-resolution", "Low Resolution", XAModsRestoreScope.Graphic, () => Configuration.LowResolutionEnabled, SystemWindowMods.SetLowResolutionEnabled, applied => Configuration.LowResolutionEnabled = applied, () => SystemWindowMods.LowResolutionStatusText);
        yield return new("special-rendering-modes", "Special Rendering Modes", XAModsRestoreScope.Graphic, () => Configuration.SpecialRenderModesEnabled, SetSpecialRenderModesEnabled, applied => Configuration.SpecialRenderModesEnabled = applied, () => Configuration.SpecialRenderModesEnabled ? SystemWindowMods.SpecialRenderModesStatusText : "Disabled");

        yield return new("anti-afk", "Anti-AFK", XAModsRestoreScope.Player, () => Configuration.AntiAfkEnabled, AntiAfk.SetEnabled, applied => Configuration.AntiAfkEnabled = applied, () => AntiAfk.StatusText);
        yield return new("auto-expert-delivery", "Automate Expert Delivery", XAModsRestoreScope.Player, () => Configuration.AutoUnlockExpertDeliveryEnabled, AutoUnlockExpertDelivery.SetEnabled, applied => Configuration.AutoUnlockExpertDeliveryEnabled = applied, () => AutoUnlockExpertDelivery.StatusText);
        yield return new("auto-leave-duty", "Auto Leave Duty", XAModsRestoreScope.Player, () => Configuration.AutoLeaveDutyEnabled, AutoLeaveDuty.SetEnabled, applied => Configuration.AutoLeaveDutyEnabled = applied, () => AutoLeaveDuty.StatusText);
        yield return new("auto-merge", "Auto Merge", XAModsRestoreScope.Player, () => Configuration.AutoMergeEnabled, AutoMerge.SetEnabled, applied => Configuration.AutoMergeEnabled = applied, () => AutoMerge.StatusText);
        yield return new("quick-return", "Instant Return", XAModsRestoreScope.Illegal, () => Configuration.QuickReturnEnabled, QuickReturn.SetEnabled, applied => Configuration.QuickReturnEnabled = applied, () => QuickReturn.StatusText);
        yield return new("auto-refuse-trade-request", "Refuse Trade Request", XAModsRestoreScope.Player, () => Configuration.AutoRefuseTradeRequestEnabled, AutoRefuseTrade.SetEnabled, applied => Configuration.AutoRefuseTradeRequestEnabled = applied, () => AutoRefuseTrade.StatusText);
        yield return new("auto-reveal-undiscovered-areas", "Reveal Undiscovered Areas", XAModsRestoreScope.Player, () => Configuration.AutoRevealUndiscoveredAreasEnabled, SystemWindowMods.SetRevealUndiscoveredAreasEnabled, applied => Configuration.AutoRevealUndiscoveredAreasEnabled = applied, () => SystemWindowMods.RevealUndiscoveredAreasStatusText);
        yield return new("auto-clear-teleportation-lock", "Clear Teleportation Lock", XAModsRestoreScope.Player, () => Configuration.AutoClearTeleportationLockEnabled, TeleportLockClear.SetEnabled, applied => Configuration.AutoClearTeleportationLockEnabled = applied, () => TeleportLockClear.StatusText);
        yield return new("custom-sight-distance", "Custom Sight Distance", XAModsRestoreScope.Player, () => Configuration.CustomSightDistanceEnabled, SightDistance.SetEnabled, applied => Configuration.CustomSightDistanceEnabled = applied, () => SightDistance.StatusText);
        yield return new("doze-sit-anywhere", "Doze & Sit Anywhere", XAModsRestoreScope.Player, () => Configuration.DozeSitAnywhereEnabled, DozeSitAnywhere.SetEnabled, applied => Configuration.DozeSitAnywhereEnabled = applied, () => DozeSitAnywhere.StatusText);
        yield return new("infinite-sprint", "Infinite Sprint", XAModsRestoreScope.Player, () => Configuration.InfiniteSprintEnabled, PlayerMods.SetInfiniteSprintEnabled, applied => Configuration.InfiniteSprintEnabled = applied, () => PlayerMods.InfiniteSprintStatusText);
        yield return new("instant-logout", "Instant Logout", XAModsRestoreScope.Illegal, () => Configuration.InstantLogoutEnabled, InstantLogout.SetEnabled, applied => Configuration.InstantLogoutEnabled = applied, () => InstantLogout.StatusText);
        yield return new("item-commands", "Item Commands", XAModsRestoreScope.Player, () => Configuration.ItemCommandsEnabled, ItemCommands.SetEnabled, applied => Configuration.ItemCommandsEnabled = applied, () => ItemCommands.StatusText);
        yield return new("xa-peep", "XA Peep", XAModsRestoreScope.Player, () => Configuration.XAPeepEnabled, XAPeep.SetEnabled, applied => Configuration.XAPeepEnabled = applied, () => XAPeep.StatusText);

        yield return new(
            "anonymize-character-lists",
            "Anonymize Character Lists",
            XAModsRestoreScope.Plugin,
            () => Configuration.GlobalCharacterListAnonymizeEnabled,
            enabled =>
            {
                Configuration.GlobalCharacterListAnonymizeEnabled = enabled;
                return enabled;
            },
            applied => Configuration.GlobalCharacterListAnonymizeEnabled = applied,
            () => Configuration.GlobalCharacterListAnonymizeEnabled
                ? "Enabled - character-list tables and duplicate summaries use deterministic aliases for screenshot-safe local views."
                : "Disabled");
        yield return new("force-peepingtom", "Force PeepingTom", XAModsRestoreScope.Plugin, () => Configuration.ForcePeepingTomEnabled, PeepingTomIntegration.SetForceEnabled, applied => Configuration.ForcePeepingTomEnabled = applied, () => PeepingTomIntegration.StatusText);

        yield return new("eureka-instance-id", "Instance ID", XAModsRestoreScope.Eureka, () => Configuration.EurekaInstanceIdEnabled, EurekaInstanceId.SetEnabled, applied => Configuration.EurekaInstanceIdEnabled = applied, () => EurekaInstanceId.StatusText);

        yield return new("auto-unlock-expert-delivery", "Unlock Expert Delivery", XAModsRestoreScope.Illegal, () => Configuration.UnlockExpertDeliveryEnabled, ExpertDeliveryUnlock.SetEnabled, applied => Configuration.UnlockExpertDeliveryEnabled = applied, () => ExpertDeliveryUnlock.StatusText);
        yield return new("moveable-after-death", "Moveable After Death", XAModsRestoreScope.Illegal, () => Configuration.MoveableAfterDeathEnabled, PlayerMods.SetMoveableAfterDeathEnabled, applied => Configuration.MoveableAfterDeathEnabled = applied, () => PlayerMods.MoveableAfterDeathStatusText);
    }

    private bool TryHandleXAModToggleCommand(string subcommand, string value)
    {
        if (!TryGetXAModToggleCommandDefinition(subcommand, out var commandDefinition))
            return false;

        if (string.IsNullOrWhiteSpace(value))
        {
            var currentState = commandDefinition.Definition.GetCurrent() ? "On" : "Off";
            ChatGui.Print($"[XASlave] {commandDefinition.Definition.DisplayName}: {currentState}. {commandDefinition.Definition.GetStatusText()} Usage: {commandDefinition.Usage}");
            return true;
        }

        if (!TryParseToggleCommandState(value, out var enabled, out var message))
        {
            ChatGui.PrintError($"[XASlave] {message} Usage: {commandDefinition.Usage}");
            return true;
        }

        PrintCommandResult(SetXAModEnabled(commandDefinition.Definition, enabled, out message), message);
        return true;
    }

    private bool SetXAModEnabled(XAModCommandDefinition definition, bool enabled, out string message)
    {
        if (!enabled)
        {
            definition.Apply(false);
            definition.Store(false);
            Configuration.Save();
            message = $"{definition.DisplayName} disabled.";
            return true;
        }

        ApplyStoredXAModConfiguration(definition.Key);
        var applied = definition.Apply(true);
        definition.Store(applied);
        Configuration.Save();
        message = applied
            ? $"{definition.DisplayName} enabled. {definition.GetStatusText()}"
            : definition.GetStatusText();
        return applied;
    }

    private static bool TryParseToggleCommandState(string value, out bool enabled, out string message)
    {
        enabled = false;
        if (value.Equals("on", StringComparison.OrdinalIgnoreCase)
            || value.Equals("enable", StringComparison.OrdinalIgnoreCase)
            || value.Equals("enabled", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.OrdinalIgnoreCase))
        {
            message = string.Empty;
            enabled = true;
            return true;
        }

        if (value.Equals("off", StringComparison.OrdinalIgnoreCase)
            || value.Equals("disable", StringComparison.OrdinalIgnoreCase)
            || value.Equals("disabled", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("0", StringComparison.OrdinalIgnoreCase))
        {
            message = string.Empty;
            enabled = false;
            return true;
        }

        message = "Use on/off, enable/disable, true/false, or 1/0.";
        return false;
    }

    private bool TryGetXAModToggleCommandDefinition(string subcommand, out XAModToggleCommandDefinition definition)
    {
        switch (subcommand.ToLowerInvariant())
        {
            case "multiinstance":
            case "multibox":
                definition = new("multiinstance", "/xa multiinstance on|off", GetXAModDefinition("auto-allow-multiple-game-instances"));
                return true;
            case "logincooldown":
                definition = new("logincooldown", "/xa logincooldown on|off", GetXAModDefinition("auto-cancel-login-cooldown"));
                return true;
            case "msqprogress":
                definition = new("msqprogress", "/xa msqprogress on|off", GetXAModDefinition("auto-display-msq-progress"));
                return true;
            case "skipcutscenes":
                definition = new("skipcutscenes", "/xa skipcutscenes on|off", GetXAModDefinition("auto-skip-cutscenes"));
                return true;
            case "chocobocutscene":
                definition = new("chocobocutscene", "/xa chocobocutscene on|off", GetXAModDefinition("auto-skip-cutscenes-feeding-chocobo"));
                return true;
            case "hidepopups":
                definition = new("hidepopups", "/xa hidepopups on|off", GetXAModDefinition("auto-hide-unnecessary-popups"));
                return true;
            case "preventlobbyexit":
                definition = new("preventlobbyexit", "/xa preventlobbyexit on|off", GetXAModDefinition("auto-prevent-game-exiting-from-lobby-errors"));
                return true;
            case "closeerrors":
                definition = new("closeerrors", "/xa closeerrors on|off", GetXAModDefinition("auto-close-lobby-errors"));
                return true;
            case "skipdialogue":
                definition = new("skipdialogue", "/xa skipdialogue on|off", GetXAModDefinition("auto-skip-dialogue"));
                return true;
            case "queueposition":
                definition = new("queueposition", "/xa queueposition on|off", GetXAModDefinition("display-actual-queue-position"));
                return true;
            case "targetfix":
            case "targetcommand":
                definition = new("targetfix", "/xa targetfix on|off", GetXAModDefinition("target-command-fix"));
                return true;
            case "copyitemname":
                definition = new("copyitemname", "/xa copyitemname on|off", GetXAModDefinition("copy-item-name-for-all"));
                return true;
            case "playersearch":
                definition = new("playersearch", "/xa playersearch on|off", GetXAModDefinition("expanded-player-right-click-menu-search"));
                return true;
            case "anonymous":
                definition = new("anonymous", "/xa anonymous on|off", GetXAModDefinition("live-anonymous-mode"));
                return true;
            case "minwindow":
                definition = new("minwindow", "/xa minwindow on|off", GetXAModDefinition("auto-ignore-minimum-window-size"));
                return true;
            case "hideobjects":
                definition = new("hideobjects", "/xa hideobjects on|off", GetXAModDefinition("auto-hide-game-objects"));
                return true;
            case "customres":
                definition = new("customres", "/xa customres on|off", GetXAModDefinition("custom-resolutions"));
                return true;
            case "bgpause":
                definition = new("bgpause", "/xa bgpause on|off", GetXAModDefinition("disable-background-game-rendering"));
                return true;
            case "specialrender":
                definition = new("specialrender", "/xa specialrender on|off", GetXAModDefinition("special-rendering-modes"));
                return true;
            case "antiafk":
                definition = new("antiafk", "/xa antiafk on|off", GetXAModDefinition("anti-afk"));
                return true;
            case "expertdelivery":
                definition = new("expertdelivery", "/xa expertdelivery on|off", GetXAModDefinition("auto-expert-delivery"));
                return true;
            case "leaveduty":
            case "autoleaveduty":
                definition = new("leaveduty", "/xa leaveduty on|off", GetXAModDefinition("auto-leave-duty"));
                return true;
            case "automerge":
            case "merge":
                definition = new("automerge", "/xa automerge on|off", GetXAModDefinition("auto-merge"));
                return true;
            case "instancereturn":
            case "quickreturn":
            case "instantreturn":
                definition = new("instantreturn", "/xa instantreturn on|off", GetXAModDefinition("quick-return"));
                return true;
            case "refusetrade":
                definition = new("refusetrade", "/xa refusetrade on|off", GetXAModDefinition("auto-refuse-trade-request"));
                return true;
            case "revealmap":
                definition = new("revealmap", "/xa revealmap on|off", GetXAModDefinition("auto-reveal-undiscovered-areas"));
                return true;
            case "teleportlock":
                definition = new("teleportlock", "/xa teleportlock on|off", GetXAModDefinition("auto-clear-teleportation-lock"));
                return true;
            case "sightdistance":
                definition = new("sightdistance", "/xa sightdistance on|off", GetXAModDefinition("custom-sight-distance"));
                return true;
            case "sitdoze":
                definition = new("sitdoze", "/xa sitdoze on|off", GetXAModDefinition("doze-sit-anywhere"));
                return true;
            case "sprint":
                definition = new("sprint", "/xa sprint on|off", GetXAModDefinition("infinite-sprint"));
                return true;
            case "instantlogout":
                definition = new("instantlogout", "/xa instantlogout on|off", GetXAModDefinition("instant-logout"));
                return true;
            case "itemcommands":
            case "itemcmds":
                definition = new("itemcommands", "/xa itemcommands on|off", GetXAModDefinition("item-commands"));
                return true;
            case "peepingtom":
                definition = new("peepingtom", "/xa peepingtom on|off", GetXAModDefinition("force-peepingtom"));
                return true;
            case "anonchars":
                definition = new("anonchars", "/xa anonchars on|off", GetXAModDefinition("anonymize-character-lists"));
                return true;
            case "eurekaid":
            case "eurekainstance":
                definition = new("eurekaid", "/xa eurekaid on|off", GetXAModDefinition("eureka-instance-id"));
                return true;
            case "unlockexpert":
                definition = new("unlockexpert", "/xa unlockexpert on|off", GetXAModDefinition("auto-unlock-expert-delivery"));
                return true;
            case "moveafterdeath":
                definition = new("moveafterdeath", "/xa moveafterdeath on|off", GetXAModDefinition("moveable-after-death"));
                return true;
            default:
                definition = default;
                return false;
        }
    }

    private static bool IsXaModsSubcommand(string subcommand)
    {
        return subcommand.Equals("xamods", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("mods", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetXAModsRestoreScopeLabel(XAModsRestoreScope scope)
    {
        return scope switch
        {
            XAModsRestoreScope.Game => "Game Mods",
            XAModsRestoreScope.Graphic => "Graphic Mods",
            XAModsRestoreScope.Player => "Player Mods",
            XAModsRestoreScope.Plugin => "Plugin Mods",
            XAModsRestoreScope.Eureka => "Eureka Mods",
            XAModsRestoreScope.Illegal => "Illegal Shit You Shouldn't Use",
            _ => "XA Mods",
        };
    }

    private Vector4 GetSpecialRenderBackgroundColor()
    {
        return new Vector4(
            Configuration.SpecialRenderModeBackgroundColorR,
            Configuration.SpecialRenderModeBackgroundColorG,
            Configuration.SpecialRenderModeBackgroundColorB,
            Configuration.SpecialRenderModeBackgroundColorA);
    }

    private static UiFlags GetAllSpecialRenderUiFlags()
    {
        return UiFlags.ActionBars
            | UiFlags.Chat
            | UiFlags.Hud
            | UiFlags.Nameplates
            | UiFlags.TargetInfo
            | UiFlags.Shortcuts;
    }

    private bool ClearUnsafeSpecialRenderHideChatSetting(bool notifyUser)
    {
        if (!IsAutoRetainerMultiModeEnabled() || !Configuration.SpecialRenderHideChatEnabled)
            return false;

        Configuration.SpecialRenderHideChatEnabled = false;
        if (notifyUser)
            ChatGui.PrintError("[XASlave] Hide Chat was turned off because AutoRetainer Multi Mode is enabled.");

        return true;
    }

    private UiFlags GetSpecialRenderHiddenUiFlags()
    {
        var hiddenUiFlags = UiFlags.None;

        if (Configuration.SpecialRenderHideAddonsKeepNameplatesEnabled)
        {
            hiddenUiFlags |= UiFlags.ActionBars
                | UiFlags.Chat
                | UiFlags.Hud
                | UiFlags.TargetInfo
                | UiFlags.Shortcuts;
        }

        if (Configuration.SpecialRenderHideAddonsKeepChatEnabled)
        {
            hiddenUiFlags |= UiFlags.ActionBars
                | UiFlags.Hud
                | UiFlags.Nameplates
                | UiFlags.TargetInfo
                | UiFlags.Shortcuts;
        }

        if (Configuration.SpecialRenderHideChatEnabled)
            hiddenUiFlags |= UiFlags.Chat;

        if (Configuration.SpecialRenderHideActionBarsEnabled)
            hiddenUiFlags |= UiFlags.ActionBars;

        if (Configuration.SpecialRenderHideTargetInfoEnabled)
            hiddenUiFlags |= UiFlags.TargetInfo;

        if (Configuration.SpecialRenderHideNameplatesEnabled)
            hiddenUiFlags |= UiFlags.Nameplates;

        return hiddenUiFlags;
    }

    internal void ApplySpecialRenderModesConfiguration()
    {
        var clearedHideChatForSafety = ClearUnsafeSpecialRenderHideChatSetting(notifyUser: true);
        if (hasAppliedSpecialRenderUiFlags && appliedSpecialRenderUiFlags != 0)
            SystemWindowMods.SetSpecialRenderUiVisibility(appliedSpecialRenderUiFlags, true);

        var hiddenUiFlags = GetSpecialRenderHiddenUiFlags();
        if (hiddenUiFlags != 0)
            SystemWindowMods.SetSpecialRenderUiVisibility(hiddenUiFlags, false);

        appliedSpecialRenderUiFlags = hiddenUiFlags;
        hasAppliedSpecialRenderUiFlags = true;

        if (clearedHideChatForSafety)
            Configuration.Save();
    }

    internal void EnforceSpecialRenderSafetyOnFrameworkTick()
    {
        if (!ClearUnsafeSpecialRenderHideChatSetting(notifyUser: true))
            return;

        if (Configuration.SpecialRenderModesEnabled)
            ApplySpecialRenderModesConfiguration();

        Configuration.Save();
    }

    internal void RestoreSpecialRenderModes(bool clearStoredUiToggles = false)
    {
        if (clearStoredUiToggles)
        {
            Configuration.SpecialRenderHideAddonsKeepNameplatesEnabled = false;
            Configuration.SpecialRenderHideAddonsKeepChatEnabled = false;
            Configuration.SpecialRenderHideChatEnabled = false;
            Configuration.SpecialRenderHideActionBarsEnabled = false;
            Configuration.SpecialRenderHideTargetInfoEnabled = false;
            Configuration.SpecialRenderHideNameplatesEnabled = false;
        }

        appliedSpecialRenderUiFlags = 0;
        hasAppliedSpecialRenderUiFlags = false;
        SystemWindowMods.SetSpecialRenderWorldHidden(false, GetSpecialRenderBackgroundColor());
        SystemWindowMods.SetSpecialRenderUiVisibility(GetAllSpecialRenderUiFlags(), true);
    }

    internal bool SetSpecialRenderModesEnabled(bool value)
    {
        if (!value)
        {
            RestoreSpecialRenderModes();
        }
        else
        {
            ApplySpecialRenderModesConfiguration();
        }

        return value;
    }

    public void ToggleMainUi() => SlaveWindow.Toggle();

    public void OpenXAModsUi()
    {
        SlaveWindow.OpenXAModsTask();
        SlaveWindow.BringToFront();
    }

    public void ToggleXAPeepUi()
    {
        XAPeepWindow.IsOpen = !XAPeepWindow.IsOpen;
        if (XAPeepWindow.IsOpen)
            XAPeepWindow.BringToFront();
    }

    public void ToggleXAPeepHistoryUi()
    {
        XAPeepHistoryWindow.IsOpen = !XAPeepHistoryWindow.IsOpen;
        if (XAPeepHistoryWindow.IsOpen)
            XAPeepHistoryWindow.BringToFront();
    }

    public void OpenXAPeepHistoryUi()
    {
        XAPeepHistoryWindow.IsOpen = true;
        XAPeepHistoryWindow.BringToFront();
    }

    public (string Address, int Port) ApplyXagmanHubEndpoint(string? address, int port)
    {
        var normalizedAddress = XagmanPeerService.NormalizeHubAddress(address);
        var normalizedPort = XagmanPeerService.NormalizePort(port);
        if (Configuration.XagmanHubPort == normalizedPort
            && string.Equals(Configuration.XagmanHubAddress, normalizedAddress, StringComparison.OrdinalIgnoreCase)
            && XagmanPeers.HubPort == normalizedPort
            && string.Equals(XagmanPeers.HubAddress, normalizedAddress, StringComparison.OrdinalIgnoreCase))
            return (normalizedAddress, normalizedPort);

        Configuration.XagmanHubAddress = normalizedAddress;
        Configuration.XagmanHubPort = normalizedPort;
        Configuration.Save();
        RestartXagmanPeerService();
        return (normalizedAddress, normalizedPort);
    }

    public bool SetXagmanPeerConnectionsEnabled(bool enabled)
    {
        if (Configuration.XagmanPeerConnectionsEnabled == enabled && XagmanPeers.IsStarted == enabled)
            return enabled;

        Configuration.XagmanPeerConnectionsEnabled = enabled;
        Configuration.Save();
        RestartXagmanPeerService();
        return enabled;
    }

    private void RestartXagmanPeerService()
    {
        XagmanPeers.Dispose();
        XagmanPeers = new XagmanPeerService(Log, InstanceId, Configuration.XagmanHubAddress, Configuration.XagmanHubPort, _ => { });
        if (Configuration.XagmanPeerConnectionsEnabled)
            XagmanPeers.Start();

        SlaveWindow?.RebindXagmanPeerEventHandlers();
    }

    private static List<XagmanItemEntry> MergeXagmanItems(params IEnumerable<XagmanItemEntry>[] sources)
    {
        return sources
            .SelectMany(source => source)
            .Where(item => item.ItemId > 0 && !string.IsNullOrWhiteSpace(item.ItemName))
            .GroupBy(item => new { item.ItemId, item.IsHq })
            .Select(group => new XagmanItemEntry
            {
                ItemId = group.Key.ItemId,
                ItemName = group.First().ItemName,
                IsHq = group.Key.IsHq,
                Mode = group.First().Mode,
                Quantity = Math.Max(0, group.First().Quantity),
            })
            .OrderBy(item => item.ItemId)
            .ToList();
    }
}

internal static class BuildInfo
{
    public const string Version = "0.0.0.27";
}
