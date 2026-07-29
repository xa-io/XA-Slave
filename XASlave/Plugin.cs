using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Agent;
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
    [PluginService] public static IAgentLifecycle AgentLifecycle { get; private set; } = null!;
    [PluginService] public static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static INotificationManager NotificationManager { get; private set; } = null!;

    private const string CommandName = "/xa";
    private enum XAModsRestoreScope
    {
        Game,
        Ui,
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
        bool IsReady,
        bool IsPending);

    private readonly record struct DeferredStartupAction(
        Action Action,
        string? Name,
        int LineNumber);

    private readonly record struct PostLoadXAModActivation(
        Action Activate,
        string Key,
        string DisplayName,
        TimeSpan Delay);

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
    public DalamudNotificationSuppressorService DalamudNotificationsSuck { get; init; }
    public DalamudLogDisablerService DalamudLogDisabler { get; init; }
    public BetterHighlightPotentialTargetsService BetterHighlightPotentialTargets { get; init; }
    public SystemWindowModsService SystemWindowMods { get; init; }
    public ReplaceUnownedMountHotbarsService ReplaceUnownedMountHotbars { get; init; }
    public LobbyErrorAutoCloseService LobbyErrorAutoClose { get; init; }
    public QueuePositionDisplayService QueuePositionDisplay { get; init; }
    public MsqProgressDisplayService MsqProgressDisplay { get; init; }
    public TooltipItemIdService TooltipItemId { get; init; }
    public AutoDisplayIdsService AutoDisplayIds { get; init; }
    public AutoDisplayNetworkLatencyService AutoDisplayNetworkLatency { get; init; }
    public ChatTimestampFormatService ChatTimestampFormat { get; init; }
    public NoUiFadeService NoUiFade { get; init; }
    public AutoHideGameObjectsService AutoHideGameObjects { get; init; }
    public DialogueSkipService DialogueSkip { get; init; }
    public AutoLockGameWindowService AutoLockGameWindow { get; init; }
    public NotifyWhenFriendIsNearService NotifyWhenFriendIsNear { get; init; }
    public AlertWhenTypingInCombatService AlertWhenTypingInCombat { get; init; }
    public BetterCastBarService BetterCastBar { get; init; }
    public BetterDutyFinderSettingsService BetterDutyFinder { get; init; }
    public CopyItemNameContextMenuService CopyItemNameContextMenu { get; init; }
    public SightDistanceService SightDistance { get; init; }
    public PlayerSearchContextMenuService PlayerSearchContextMenu { get; init; }
    public NameplatePrivacyService NameplatePrivacy { get; init; }
    public BlacklistedPartyNameService BlacklistedPartyName { get; init; }
    public AutoUnlockExpertDeliveryService AutoUnlockExpertDelivery { get; init; }
    public ExpertDeliveryUnlockService ExpertDeliveryUnlock { get; init; }
    public AutoRefuseTradeService AutoRefuseTrade { get; init; }
    public TargetCommandFixService TargetCommandFix { get; init; }
    public AntiAfkService AntiAfk { get; init; }
    public AutoDutyCommenceService AutoDutyCommence { get; init; }
    public AutoLeaveDutyService AutoLeaveDuty { get; init; }
    public BetterInventoryMoverService BetterInventoryMover { get; init; }
    public BetterCompanyChestService BetterCompanyChest { get; init; }
    public AutoOpenMoogleMailService AutoOpenMoogleMail { get; init; }
    public EnableItemIconInShopsService EnableItemIconInShops { get; init; }
    public FieldEntryCommandService FieldEntryCommand { get; init; }
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
    public ARealmRecordedIntegrationService ARealmRecordedIntegration { get; init; }
    public TeleportHelperService TeleportHelper { get; init; }
    public ArPostProcessService ArPostProcessor { get; init; }
    public SlaveDatabaseService SlaveDatabase { get; init; }
    public XagmanPeerService XagmanPeers { get; private set; }

    private UiFlags appliedSpecialRenderUiFlags;
    private bool hasAppliedSpecialRenderUiFlags;
    private bool isDisposed;
    private readonly Queue<DeferredStartupAction> deferredStartupActions = new();
    private readonly Queue<PostLoadXAModActivation> postLoadXAModActivations = new();
    private readonly HashSet<string> pendingPostLoadXAModActivations = new(StringComparer.Ordinal);
    private readonly List<string> completedPostLoadXAModActivations = new();
    private readonly List<string> failedPostLoadXAModActivations = new();
    private bool deferredStartupQueueScheduled;
    private bool postLoadXAModActivationsScheduled;
    private bool postLoadXAModActivationCompletionPending;
    private bool postLoadXAModActivationCompletionCheckScheduled;
    private const double DeferredStartupQueueFrameBudgetMilliseconds = 4.0;
    private const int DeferredStartupQueueMaxActionsPerTick = 24;
    private const double PostLoadXAModActivationInitialDelaySeconds = 1.0;
    private const double PostLoadXAModActivationSpacingSeconds = 0.5;
    private const double DeferredStartupSummaryPendingArmingTimeoutSeconds = 10.0;
    private const double DeferredStartupActionDebugThresholdMilliseconds = 5.0;
    private static readonly TimeSpan ExternalTaskLoadDelay = TimeSpan.FromSeconds(1);
    private DateTime deferredStartupSummaryPendingArmingSinceUtc = DateTime.MinValue;
    private DateTime postLoadXAModActivationCompletionPendingSinceUtc = DateTime.MinValue;

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
        AutoSkipCutscenes = new AutoSkipCutsceneService(Condition, Framework, ClientState, DataManager, PartyList, SigScanner, GameInterop, AgentLifecycle, Log);
        BuddyFeedCutsceneSkip = new BuddyFeedCutsceneSkipService(SigScanner, GameInterop, ClientState, Log);
        PopupCleaner = new PopupCleanerService(AddonLifecycle, Log);
        DalamudNotificationsSuck = new DalamudNotificationSuppressorService(PluginInterface, Log);
        DalamudLogDisabler = new DalamudLogDisablerService(PluginInterface, Framework, Log);
        BetterHighlightPotentialTargets = new BetterHighlightPotentialTargetsService(Framework, ObjectTable, TargetManager, ClientState, Log);
        SystemWindowMods = new SystemWindowModsService(SigScanner, GameInterop, Log, Framework, GameConfig, ClientState, () => IpcClient.AutoRetainerGetMultiModeEnabled());
        ReplaceUnownedMountHotbars = new ReplaceUnownedMountHotbarsService(GameInterop, Log);
        LobbyErrorAutoClose = new LobbyErrorAutoCloseService(AddonLifecycle, Framework, ClientState, Log);
        QueuePositionDisplay = new QueuePositionDisplayService(Framework, SigScanner, GameInterop, Log);
        MsqProgressDisplay = new MsqProgressDisplayService(AddonLifecycle, DataManager, Log);
        TooltipItemId = new TooltipItemIdService(AddonLifecycle, GameGui, SigScanner, GameInterop, Log);
        AutoDisplayIds = new AutoDisplayIdsService(AddonLifecycle, Framework, ClientState, DataManager, TargetManager, DtrBar, Log);
        AutoDisplayNetworkLatency = new AutoDisplayNetworkLatencyService(Framework, ClientState, DtrBar, Log);
        ChatTimestampFormat = new ChatTimestampFormatService(Framework, SigScanner, GameInterop, Log);
        NoUiFade = new NoUiFadeService(Framework, SigScanner, GameInterop, Log);
        AutoHideGameObjects = new AutoHideGameObjectsService(Framework, ClientState, Condition, TargetManager, SigScanner, GameInterop, Log);
        DialogueSkip = new DialogueSkipService(AddonLifecycle, SigScanner, GameInterop, Log);
        AutoLockGameWindow = new AutoLockGameWindowService(Condition, Log);
        NotifyWhenFriendIsNear = new NotifyWhenFriendIsNearService(Framework, ClientState, ObjectTable, ToastGui, ChatGui, Log);
        AlertWhenTypingInCombat = new AlertWhenTypingInCombatService(Framework, ClientState, Condition, ToastGui, Log);
        BetterCastBar = new BetterCastBarService(AddonLifecycle, ObjectTable, DataManager, Log);
        BetterDutyFinder = new BetterDutyFinderSettingsService(AddonLifecycle, SigScanner, GameConfig, Log);
        CopyItemNameContextMenu = new CopyItemNameContextMenuService(ContextMenu, DataManager, Log);
        SightDistance = new SightDistanceService(Framework, SigScanner, GameInterop, Log);
        PlayerSearchContextMenu = new PlayerSearchContextMenuService(ContextMenu, DataManager, Log);
        NameplatePrivacy = new NameplatePrivacyService(NamePlateGui, IpcClient, Log);
        BlacklistedPartyName = new BlacklistedPartyNameService(Framework, Log);
        AutoUnlockExpertDelivery = new AutoUnlockExpertDeliveryService(Framework, DataManager, Log);
        ExpertDeliveryUnlock = new ExpertDeliveryUnlockService(GameInterop, Log);
        ExpertDeliveryUnlock.ApplyConfiguration(Configuration.UnlockExpertDeliveryForcedRankFloor);
        AutoRefuseTrade = new AutoRefuseTradeService(SigScanner, GameInterop, Log);
        TargetCommandFix = new TargetCommandFixService(ChatGui, Log);
        AntiAfk = new AntiAfkService(Framework, ClientState, Log);
        AutoDutyCommence = new AutoDutyCommenceService(AddonLifecycle, Log);
        AutoLeaveDuty = new AutoLeaveDutyService(DutyState, ClientState, PlayerState, Condition, Framework, Log);
        BetterInventoryMover = new BetterInventoryMoverService(ContextMenu, DataManager, Log);
        BetterCompanyChest = new BetterCompanyChestService(AddonLifecycle, ContextMenu, DataManager, Log);
        AutoOpenMoogleMail = new AutoOpenMoogleMailService(Framework, Log);
        EnableItemIconInShops = new EnableItemIconInShopsService(AddonLifecycle, DataManager, Log);
        FieldEntryCommand = new FieldEntryCommandService(Framework, DataManager, ClientState, IpcClient, Log);
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
        XAPeep = new XAPeepService(Framework, ClientState, Condition, ObjectTable, GameGui, Log, SlaveDatabase, Configuration);
        PeepingTomIntegration = new PeepingTomIntegrationService(PluginInterface, Framework, Log);
        ARealmRecordedIntegration = new ARealmRecordedIntegrationService(PluginInterface, Framework, ClientState, Condition, DataManager, Log);
        TeleportHelper = new TeleportHelperService(Framework, ClientState, PlayerState, Log);
        ArPostProcessor = new ArPostProcessService(this, ClientState, Condition, Framework, ObjectTable, Log, DtrBar);
        XagmanPeers = new XagmanPeerService(Log, InstanceId, Configuration.XagmanHubAddress, Configuration.XagmanHubPort, _ => { });
        if (Configuration.XagmanPeerConnectionsEnabled)
        {
            QueueDeferredStartupAction("XagmanPeerConnectionsEnabled", () =>
            {
                XagmanPeers.Start();
            });
        }

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
        var normalizedCustomTimestampFormat = ChatTimestampFormatService.NormalizeFormat(Configuration.CustomTimestampFormat);
        if (!string.Equals(Configuration.CustomTimestampFormat, normalizedCustomTimestampFormat, StringComparison.Ordinal))
        {
            Configuration.CustomTimestampFormat = normalizedCustomTimestampFormat;
            Configuration.Save();
        }
        var normalizedBetterHighlightColor = BetterHighlightPotentialTargetsService.NormalizeHighlightColor(Configuration.BetterHighlightPotentialTargetsColor);
        if (Configuration.BetterHighlightPotentialTargetsColor != normalizedBetterHighlightColor)
        {
            Configuration.BetterHighlightPotentialTargetsColor = normalizedBetterHighlightColor;
            Configuration.Save();
        }
        ChatTimestampFormat.ApplyConfiguration(Configuration.CustomTimestampFormat);
        if (Configuration.CustomTimestampFormatEnabled)
            ChatTimestampFormat.PrepareHookDuringPluginLoad();

        ApplyAutoDisplayIdsConfiguration(save: false);
        ApplyAutoDisplayNetworkLatencyConfiguration(save: false);
        AutoSkipCutscenes.ApplyConfiguration(Configuration);
        ApplyDalamudNotificationsSuckConfiguration(save: false);
        ApplyBetterHighlightPotentialTargetsConfiguration(save: false);
        ApplyNotifyWhenFriendIsNearConfiguration(save: false);
        ApplyAlertWhenTypingInCombatConfiguration(save: false);
        ApplyBetterCastBarConfiguration(save: false);
        ApplyBetterCompanyChestConfiguration(save: false);
        DozeSitAnywhere.ApplyConfiguration(
            Configuration.DozeSitAnywhereAllowDoze,
            Configuration.DozeSitAnywhereAllowSit);
        TeleportHelper.ApplyConfiguration(Configuration.TeleportHelperSelectYes);
        if (Configuration.AutoAllowMultipleGameInstancesEnabled && !SystemWindowMods.RestoreAllowMultipleGameInstancesOnStartup())
        {
            Configuration.AutoAllowMultipleGameInstancesEnabled = false;
            Configuration.Save();
        }
        QueueDeferredStartupAction("AutoCancelLoginCooldownEnabled", () =>
        {
            if (Configuration.AutoCancelLoginCooldownEnabled && !SystemWindowMods.SetCancelLoginCooldownEnabled(true))
            {
                Configuration.AutoCancelLoginCooldownEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("AutoDisplayMsqProgressEnabled", () =>
        {
            if (Configuration.AutoDisplayMsqProgressEnabled && !MsqProgressDisplay.SetEnabled(true))
            {
                Configuration.AutoDisplayMsqProgressEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("DisableTitleScreenMovieEnabled", () =>
        {
            if (Configuration.DisableTitleScreenMovieEnabled && !SystemWindowMods.SetDisableTitleScreenMovieEnabled(true))
            {
                Configuration.DisableTitleScreenMovieEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("AutoDisplayIdsEnabled", () =>
        {
            if (!Configuration.AutoDisplayIdsEnabled)
            {
                Configuration.ShowItemIdEnabled = false;
                TooltipItemId.SetEnabled(false);
                return;
            }

            ApplyStoredXAModConfiguration("auto-display-ids");
            if (!AutoDisplayIds.SetEnabled(true))
            {
                Configuration.AutoDisplayIdsEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("AutoDisplayNetworkLatencyEnabled", () =>
        {
            if (!Configuration.AutoDisplayNetworkLatencyEnabled)
                return;

            ApplyStoredXAModConfiguration("display-network-latency");
            if (!AutoDisplayNetworkLatency.SetEnabled(true))
            {
                Configuration.AutoDisplayNetworkLatencyEnabled = false;
                Configuration.Save();
            }
        });
        if (Configuration.CustomTimestampFormatEnabled)
        {
            QueuePostLoadXAModActivation("CustomTimestampFormatEnabled", "Custom Timestamp Format", PostLoadXAModActivationInitialDelaySeconds, () =>
            {
                ChatTimestampFormat.ApplyConfiguration(Configuration.CustomTimestampFormat);
                if (!ChatTimestampFormat.RestoreEnabledOnStartup())
                {
                    Configuration.CustomTimestampFormatEnabled = false;
                    Configuration.Save();
                }
            });
        }
        if (Configuration.NoUiFadeEnabled)
        {
            QueuePostLoadXAModActivation("NoUiFadeEnabled", "No UI Fade", PostLoadXAModActivationInitialDelaySeconds + (PostLoadXAModActivationSpacingSeconds * 2), () =>
            {
                if (!NoUiFade.RestoreEnabledOnStartup())
                {
                    Configuration.NoUiFadeEnabled = false;
                    Configuration.Save();
                }
            });
        }
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.CopyItemNameForAllEnabled && !CopyItemNameContextMenu.SetEnabled(true))
            {
                Configuration.CopyItemNameForAllEnabled = false;
                Configuration.Save();
            }
        });
        if (Configuration.AutoSkipCutscenesEnabled)
        {
            QueuePostLoadXAModActivation("AutoSkipCutscenesEnabled", "Auto Skip Cutscenes", PostLoadXAModActivationInitialDelaySeconds + PostLoadXAModActivationSpacingSeconds, () =>
            {
                ApplyStoredXAModConfiguration("auto-skip-cutscenes");
                if (!AutoSkipCutscenes.RestoreEnabledOnStartup())
                {
                    Configuration.AutoSkipCutscenesEnabled = false;
                    Configuration.Save();
                }
            });
        }
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
        QueueDeferredStartupAction("DalamudNotificationsSuckEnabled", () =>
        {
            if (!Configuration.DalamudNotificationsSuckEnabled)
                return;

            ApplyStoredXAModConfiguration("dalamud-notifications-suck");
            if (!DalamudNotificationsSuck.SetEnabled(true))
            {
                Configuration.DalamudNotificationsSuckEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("DalamudLogDisablerEnabled", () =>
        {
            DalamudLogDisabler.ApplyConfiguration(Configuration.DalamudLogDisablerBlockedPlugins, Configuration.DalamudLogDisablerMinimumKeptLevel);
            if (!Configuration.DalamudLogDisablerEnabled)
                return;

            if (!DalamudLogDisabler.SetEnabled(true))
            {
                Configuration.DalamudLogDisablerEnabled = false;
                Configuration.Save();
            }
        });
        if (Configuration.BetterHighlightPotentialTargetsEnabled)
        {
            QueuePostLoadXAModActivation("BetterHighlightPotentialTargetsEnabled", "Better Highlight Potential Targets", PostLoadXAModActivationInitialDelaySeconds + (PostLoadXAModActivationSpacingSeconds * 8), () =>
            {
                ApplyStoredXAModConfiguration("better-highlight-potential-targets");
                ApplyBetterHighlightPotentialTargetsConfiguration(save: false);
                if (!BetterHighlightPotentialTargets.SetEnabled(true))
                {
                    Configuration.BetterHighlightPotentialTargetsEnabled = false;
                    Configuration.Save();
                }
            });
        }
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
        if (Configuration.DisplayActualQueuePositionEnabled)
        {
            QueuePostLoadXAModActivation("DisplayActualQueuePositionEnabled", "Display Actual Queue Position", PostLoadXAModActivationInitialDelaySeconds + (PostLoadXAModActivationSpacingSeconds * 3), () =>
            {
                if (!QueuePositionDisplay.RestoreEnabledOnStartup())
                {
                    Configuration.DisplayActualQueuePositionEnabled = false;
                    Configuration.Save();
                }
            });
        }
        if (Configuration.AutoHideGameObjectsEnabled)
        {
            QueuePostLoadXAModActivation("AutoHideGameObjectsEnabled", "Auto Hide Game Objects", PostLoadXAModActivationInitialDelaySeconds + (PostLoadXAModActivationSpacingSeconds * 4), () =>
            {
                ApplyStoredXAModConfiguration("auto-hide-game-objects");
                if (!AutoHideGameObjects.SetEnabled(true))
                {
                    Configuration.AutoHideGameObjectsEnabled = false;
                    Configuration.Save();
                }
            });
        }
        QueueDeferredStartupAction("AutoSkipDialogueEnabled", () =>
        {
            if (Configuration.AutoSkipDialogueEnabled && !DialogueSkip.SetEnabled(true))
            {
                Configuration.AutoSkipDialogueEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("LockGameWindowInCombatEnabled", () =>
        {
            if (Configuration.LockGameWindowInCombatEnabled && !AutoLockGameWindow.SetEnabled(true))
            {
                Configuration.LockGameWindowInCombatEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("NotifyWhenFriendIsNearEnabled", () =>
        {
            if (!Configuration.NotifyWhenFriendIsNearEnabled)
                return;

            ApplyStoredXAModConfiguration("notify-when-friend-is-near");
            if (!NotifyWhenFriendIsNear.SetEnabled(true))
            {
                Configuration.NotifyWhenFriendIsNearEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("AlertWhenTypingInCombatEnabled", () =>
        {
            if (!Configuration.AlertWhenTypingInCombatEnabled)
                return;

            ApplyStoredXAModConfiguration("alert-when-typing-in-combat");
            if (!AlertWhenTypingInCombat.SetEnabled(true))
            {
                Configuration.AlertWhenTypingInCombatEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("BetterCastBarEnabled", () =>
        {
            if (!Configuration.BetterCastBarEnabled)
                return;

            ApplyStoredXAModConfiguration("better-cast-bar");
            if (!BetterCastBar.SetEnabled(true))
            {
                Configuration.BetterCastBarEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("BetterDutyFinderEnabled", () =>
        {
            if (Configuration.BetterDutyFinderEnabled && !BetterDutyFinder.SetEnabled(true))
            {
                Configuration.BetterDutyFinderEnabled = false;
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
        QueueDeferredStartupAction(() =>
        {
            if (!Configuration.CustomResolutionOnLoadEnabled)
                return;

            // Custom resolutions must be active in the service before a size can be applied.
            if (!SystemWindowMods.SetCustomResolutionsEnabled(true))
            {
                Log.Warning("[CustomResolutionOnLoad] Could not enable custom resolutions; skipping on-load resize.");
                return;
            }

            // Lower the client minimum window size first so sizes below 1024x720 stick instead of snapping back.
            if (Configuration.CustomResolutionOnLoadIgnoreMinimumWindowSize)
                SystemWindowMods.SetIgnoreMinimumWindowSizeEnabled(true);

            if (SystemWindowMods.TryApplyCustomResolution(
                    Configuration.CustomResolutionOnLoadWidth,
                    Configuration.CustomResolutionOnLoadHeight,
                    out var customResolutionOnLoadMessage))
            {
                Log.Info($"[CustomResolutionOnLoad] {customResolutionOnLoadMessage}");
            }
            else
            {
                Log.Warning($"[CustomResolutionOnLoad] {customResolutionOnLoadMessage}");
            }
        });
        if (Configuration.DisableBackgroundGameRenderingEnabled)
        {
            QueuePostLoadXAModActivation("DisableBackgroundGameRenderingEnabled", "Disable Background Rendering", PostLoadXAModActivationInitialDelaySeconds + (PostLoadXAModActivationSpacingSeconds * 5), () =>
            {
                ApplyStoredXAModConfiguration("disable-background-game-rendering");
                if (!SystemWindowMods.SetDisableBackgroundRenderingEnabled(true))
                {
                    Configuration.DisableBackgroundGameRenderingEnabled = false;
                    Configuration.Save();
                }
            });
        }
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
        if (Configuration.CustomSightDistanceEnabled)
        {
            QueuePostLoadXAModActivation("CustomSightDistanceEnabled", "Custom Sight Distance", PostLoadXAModActivationInitialDelaySeconds + (PostLoadXAModActivationSpacingSeconds * 2), () =>
            {
                ApplyStoredXAModConfiguration("custom-sight-distance");
                if (!SightDistance.RestoreEnabledOnStartup())
                {
                    Configuration.CustomSightDistanceEnabled = false;
                    Configuration.Save();
                }
            });
        }
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
            var changed = false;

            NameplatePrivacy.ApplyShowTravelerWorldNamesConfiguration(
                Configuration.ShowTravelerWorldNamesDisableInDuties,
                Configuration.ShowTravelerWorldNamesAddSpacer);
            NameplatePrivacy.ApplyShowTitlesAsPlayernamesConfiguration(
                Configuration.ShowTitlesAsPlayernamesHonorificSupportEnabled);

            if (Configuration.LiveAnonymousModeEnabled && !NameplatePrivacy.SetAnonymousModeEnabled(true))
            {
                Configuration.LiveAnonymousModeEnabled = false;
                changed = true;
            }

            if (Configuration.ShowTravelerWorldNamesEnabled && !NameplatePrivacy.SetShowTravelerWorldNamesEnabled(true))
            {
                Configuration.ShowTravelerWorldNamesEnabled = false;
                changed = true;
            }

            if (Configuration.ShowTitlesAsPlayernamesEnabled && !NameplatePrivacy.SetShowTitlesAsPlayernamesEnabled(true))
            {
                Configuration.ShowTitlesAsPlayernamesEnabled = false;
                changed = true;
            }

            if (Configuration.ShowBlacklistedPlayernameInPartyEnabled && !BlacklistedPartyName.SetEnabled(true))
            {
                Configuration.ShowBlacklistedPlayernameInPartyEnabled = false;
                changed = true;
            }

            if (changed)
                Configuration.Save();
        });
        QueueDeferredStartupAction(() =>
        {
            ApplyStoredXAModConfiguration("better-inventory-mover");
            if (Configuration.BetterInventoryMoverEnabled && !BetterInventoryMover.SetEnabled(true))
            {
                Configuration.BetterInventoryMoverEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (!Configuration.BetterCompanyChestEnabled)
                return;

            ApplyStoredXAModConfiguration("better-company-chest");
            if (!BetterCompanyChest.SetEnabled(true))
            {
                Configuration.BetterCompanyChestEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (!Configuration.AutoOpenMoogleMailEnabled)
                return;

            ApplyStoredXAModConfiguration("auto-open-moogle-mail");
            if (!AutoOpenMoogleMail.SetEnabled(true))
            {
                Configuration.AutoOpenMoogleMailEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.EnableItemIconInShopsEnabled && !EnableItemIconInShops.SetEnabled(true))
            {
                Configuration.EnableItemIconInShopsEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction(() =>
        {
            if (Configuration.FieldEntryCommandEnabled && !FieldEntryCommand.SetEnabled(true))
            {
                Configuration.FieldEntryCommandEnabled = false;
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
            if (Configuration.AutoDutyCommenceEnabled && !AutoDutyCommence.SetEnabled(true))
            {
                Configuration.AutoDutyCommenceEnabled = false;
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
        if (Configuration.QuickReturnEnabled)
        {
            QueuePostLoadXAModActivation("QuickReturnEnabled", "Instant Return", PostLoadXAModActivationInitialDelaySeconds + (PostLoadXAModActivationSpacingSeconds * 9), () =>
            {
                if (!QuickReturn.SetEnabled(true))
                {
                    Configuration.QuickReturnEnabled = false;
                    Configuration.Save();
                }
            });
        }
        if (Configuration.ReplaceUnownedMountHotbarsEnabled)
        {
            QueuePostLoadXAModActivation("ReplaceUnownedMountHotbarsEnabled", "Replace Unowned Mount Hotbars", PostLoadXAModActivationInitialDelaySeconds + (PostLoadXAModActivationSpacingSeconds * 10), () =>
            {
                if (!ReplaceUnownedMountHotbars.SetEnabled(true))
                {
                    Configuration.ReplaceUnownedMountHotbarsEnabled = false;
                    Configuration.Save();
                }
            });
        }
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
        if (Configuration.AutoRefuseTradeRequestEnabled)
        {
            QueuePostLoadXAModActivation("AutoRefuseTradeRequestEnabled", "Auto Refuse Trade", PostLoadXAModActivationInitialDelaySeconds + (PostLoadXAModActivationSpacingSeconds * 6), () =>
            {
                ApplyStoredXAModConfiguration("auto-refuse-trade-request");
                if (!AutoRefuseTrade.SetEnabled(true))
                {
                    Configuration.AutoRefuseTradeRequestEnabled = false;
                    Configuration.Save();
                }
            });
        }
        QueueDeferredStartupAction("TargetCommandFixEnabled", () =>
        {
            if (Configuration.TargetCommandFixEnabled && !TargetCommandFix.SetEnabled(true))
            {
                Configuration.TargetCommandFixEnabled = false;
                Configuration.Save();
            }
        });
        if (Configuration.AutoRevealUndiscoveredAreasEnabled)
        {
            QueuePostLoadXAModActivation("AutoRevealUndiscoveredAreasEnabled", "Reveal Undiscovered Areas", PostLoadXAModActivationInitialDelaySeconds + (PostLoadXAModActivationSpacingSeconds * 7), () =>
            {
                if (!SystemWindowMods.SetRevealUndiscoveredAreasEnabled(true))
                {
                    Configuration.AutoRevealUndiscoveredAreasEnabled = false;
                    Configuration.Save();
                }
            });
        }
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
        QueueDeferredStartupAction(() =>
        {
            ARealmRecordedIntegration.ApplyConfiguration(
                Configuration.ARealmRecordedAllZonesAllContentTypes,
                Configuration.ARealmRecordedAllZonesSelectedContentTypes);
            if (Configuration.ARealmRecordedAllZonesEnabled && !ARealmRecordedIntegration.SetForceEnabled(true))
            {
                Configuration.ARealmRecordedAllZonesEnabled = false;
                Configuration.Save();
            }
        });
        QueueDeferredStartupAction("TeleportHelperEnabled", () =>
        {
            if (!Configuration.TeleportHelperEnabled)
                return;

            ApplyStoredXAModConfiguration("teleport-helper");
            if (!TeleportHelper.SetEnabled(true))
            {
                Configuration.TeleportHelperEnabled = false;
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
            HelpMessage = "Open XA Slave. Subcommands include xamods/mods, debug, fe, peep, updates, db, preset save/load/list, XA Mods toggle on/off commands, res, lowres, sprintdelay, and the section restore commands.",
            AllowedInMacros = true,
        });

        PluginInterface.UiBuilder.Draw += UpdateEurekaLogogramCreatorOverlayWindows;
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw += BetterCompanyChest.DrawOverlay;
        PluginInterface.UiBuilder.Draw += AutoOpenMoogleMail.DrawOverlay;
        PluginInterface.UiBuilder.Draw += BetterCastBar.DrawOverlay;
        PluginInterface.UiBuilder.Draw += BetterDutyFinder.DrawOverlay;
        PluginInterface.UiBuilder.Draw += XAPeep.DrawOverlay;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;

        ScheduleDeferredStartupQueue();
        ScheduleExternalTaskLoad();
    }

    private void QueueDeferredStartupAction(Action action, [CallerLineNumber] int lineNumber = 0)
    {
        deferredStartupActions.Enqueue(new DeferredStartupAction(action, null, lineNumber));
    }

    private void QueueDeferredStartupAction(string name, Action action, [CallerLineNumber] int lineNumber = 0)
    {
        deferredStartupActions.Enqueue(new DeferredStartupAction(action, name, lineNumber));
    }

    private void QueuePostLoadXAModActivation(string key, string displayName, double delaySeconds, Action action)
    {
        pendingPostLoadXAModActivations.Add(key);
        postLoadXAModActivations.Enqueue(new PostLoadXAModActivation(action, key, displayName, TimeSpan.FromSeconds(delaySeconds)));
    }

    private void ScheduleDeferredStartupQueue()
    {
        if (deferredStartupQueueScheduled || isDisposed)
            return;

        deferredStartupQueueScheduled = true;
        Framework.RunOnTick(ProcessDeferredStartupQueue, delayTicks: 1);
    }

    private void ScheduleExternalTaskLoad()
    {
        Framework.RunOnTick(LoadExternalTasksAfterStartupDelay, delay: ExternalTaskLoadDelay);
    }

    private void LoadExternalTasksAfterStartupDelay()
    {
        if (isDisposed)
            return;

        try
        {
            if (!Configuration.VerboseTaskLogging)
            {
                ExternalTaskLoader.LoadAll();
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            ExternalTaskLoader.LoadAll();
            stopwatch.Stop();
            Log.Debug($"[XASlave] Delayed external task loading took {stopwatch.Elapsed.TotalMilliseconds:F1}ms.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[XASlave] Delayed external task loading failed.");
        }
    }

    private void ProcessDeferredStartupQueue()
    {
        deferredStartupQueueScheduled = false;
        if (isDisposed)
            return;

        if (deferredStartupActions.Count == 0)
        {
            CompleteInitialStartupQueue();
            return;
        }

        var queueStopwatch = Stopwatch.StartNew();
        var processedCount = 0;

        while (!isDisposed && deferredStartupActions.Count > 0)
        {
            try
            {
                var deferredAction = deferredStartupActions.Dequeue();
                var stopwatch = Stopwatch.StartNew();
                deferredAction.Action();
                stopwatch.Stop();
                processedCount++;

                var elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                if (Configuration.VerboseTaskLogging
                    && elapsedMilliseconds >= DeferredStartupActionDebugThresholdMilliseconds)
                {
                    var label = string.IsNullOrWhiteSpace(deferredAction.Name)
                        ? $"Plugin.cs:{deferredAction.LineNumber}"
                        : deferredAction.Name;
                    Log.Debug($"[XASlave] Deferred startup action '{label}' took {elapsedMilliseconds:F1}ms.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[XASlave] Deferred startup action failed.");
            }

            if (processedCount >= DeferredStartupQueueMaxActionsPerTick
                || queueStopwatch.Elapsed.TotalMilliseconds >= DeferredStartupQueueFrameBudgetMilliseconds)
                break;
        }

        if (isDisposed)
            return;

        if (deferredStartupActions.Count == 0)
        {
            CompleteInitialStartupQueue();
            return;
        }

        ScheduleDeferredStartupQueue();
    }

    private void CompleteInitialStartupQueue()
    {
        if (HasPendingStartupArming())
        {
            if (deferredStartupSummaryPendingArmingSinceUtc == DateTime.MinValue)
                deferredStartupSummaryPendingArmingSinceUtc = DateTime.UtcNow;

            var pendingSeconds = (DateTime.UtcNow - deferredStartupSummaryPendingArmingSinceUtc).TotalSeconds;
            if (pendingSeconds < DeferredStartupSummaryPendingArmingTimeoutSeconds)
            {
                ScheduleDeferredStartupQueue();
                return;
            }

            Log.Warning($"[XASlave] Startup summary waited {pendingSeconds:0.0}s for incremental hook arming; reporting current state. Pending: {DescribePendingStartupArming()}");
        }

        deferredStartupSummaryPendingArmingSinceUtc = DateTime.MinValue;
        SchedulePostLoadXAModActivationPhase();
        LogStartupSummary();
    }

    private void SchedulePostLoadXAModActivationPhase()
    {
        if (postLoadXAModActivationsScheduled || postLoadXAModActivations.Count == 0 || isDisposed)
            return;

        postLoadXAModActivationsScheduled = true;
        var scheduledActivations = postLoadXAModActivations.ToArray();
        completedPostLoadXAModActivations.Clear();
        failedPostLoadXAModActivations.Clear();
        postLoadXAModActivationCompletionPending = true;
        postLoadXAModActivationCompletionCheckScheduled = false;
        postLoadXAModActivationCompletionPendingSinceUtc = DateTime.MinValue;

        while (postLoadXAModActivations.Count > 0)
        {
            var activation = postLoadXAModActivations.Dequeue();
            Framework.RunOnTick(() => ProcessPostLoadXAModActivation(activation), delay: activation.Delay);
        }

        Log.Information($"[XASlave] Scheduled post-load XA Mod activation phase for {scheduledActivations.Length} enabled mod(s): {string.Join(", ", scheduledActivations.Select(activation => activation.DisplayName))}.");
    }

    private void ProcessPostLoadXAModActivation(PostLoadXAModActivation activation)
    {
        if (isDisposed)
            return;

        var completed = false;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            activation.Activate();
            stopwatch.Stop();
            completed = true;

            var elapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            if (Configuration.VerboseTaskLogging
                && elapsedMilliseconds >= DeferredStartupActionDebugThresholdMilliseconds)
                Log.Debug($"[XASlave] Post-load XA Mod activation '{activation.DisplayName}' took {elapsedMilliseconds:F1}ms.");
        }
        catch (Exception ex)
        {
            failedPostLoadXAModActivations.Add(activation.DisplayName);
            Log.Warning(ex, $"[XASlave] Post-load XA Mod activation '{activation.DisplayName}' failed.");
        }
        finally
        {
            if (completed)
                completedPostLoadXAModActivations.Add(activation.DisplayName);

            pendingPostLoadXAModActivations.Remove(activation.Key);
            if (pendingPostLoadXAModActivations.Count == 0)
                SchedulePostLoadXAModActivationCompletionCheck();
        }
    }

    private void SchedulePostLoadXAModActivationCompletionCheck()
    {
        if (postLoadXAModActivationCompletionCheckScheduled || isDisposed)
            return;

        postLoadXAModActivationCompletionCheckScheduled = true;
        Framework.RunOnTick(CheckPostLoadXAModActivationCompletion, delayTicks: 1);
    }

    private void CheckPostLoadXAModActivationCompletion()
    {
        postLoadXAModActivationCompletionCheckScheduled = false;
        if (isDisposed || !postLoadXAModActivationCompletionPending)
            return;

        if (pendingPostLoadXAModActivations.Count > 0)
        {
            SchedulePostLoadXAModActivationCompletionCheck();
            return;
        }

        if (HasPendingStartupArming())
        {
            if (postLoadXAModActivationCompletionPendingSinceUtc == DateTime.MinValue)
                postLoadXAModActivationCompletionPendingSinceUtc = DateTime.UtcNow;

            var pendingSeconds = (DateTime.UtcNow - postLoadXAModActivationCompletionPendingSinceUtc).TotalSeconds;
            if (pendingSeconds < DeferredStartupSummaryPendingArmingTimeoutSeconds)
            {
                SchedulePostLoadXAModActivationCompletionCheck();
                return;
            }

            Log.Warning($"[XASlave] Post-load XA Mod activation completion waited {pendingSeconds:0.0}s for incremental arming; reporting current state. Pending: {DescribePendingStartupArming()}");
        }

        postLoadXAModActivationCompletionPending = false;
        postLoadXAModActivationCompletionPendingSinceUtc = DateTime.MinValue;
        LogPostLoadXAModActivationComplete();
    }

    private void LogPostLoadXAModActivationComplete()
    {
        if (isDisposed)
            return;

        var completedText = completedPostLoadXAModActivations.Count == 0
            ? "None"
            : string.Join(", ", completedPostLoadXAModActivations);

        if (failedPostLoadXAModActivations.Count == 0)
        {
            Log.Information($"[XASlave] Post-load XA Mod activation complete. {completedPostLoadXAModActivations.Count} enabled mod(s) processed: {completedText}.");
            return;
        }

        Log.Warning($"[XASlave] Post-load XA Mod activation complete with {failedPostLoadXAModActivations.Count} failure(s). Processed: {completedText}. Failed: {string.Join(", ", failedPostLoadXAModActivations)}.");
    }

    private bool HasPendingStartupArming()
    {
        return IsStartupArmingPending(AutoSkipCutscenes.IsStartupArmingPending, AutoSkipCutscenes.StatusText)
            || IsStartupArmingPending(SystemWindowMods.IsAllowMultipleGameInstancesStartupPending, SystemWindowMods.AllowMultipleGameInstancesStatusText)
            || IsStartupArmingPending(SystemWindowMods.IsCancelLoginCooldownStartupArmingPending, SystemWindowMods.CancelLoginCooldownStatusText)
            || IsStartupArmingPending(SystemWindowMods.IsPreventLobbyExitStartupArmingPending, SystemWindowMods.PreventLobbyExitStatusText)
            || IsStartupArmingPending(ChatTimestampFormat.IsStartupArmingPending, ChatTimestampFormat.StatusText)
            || IsStartupArmingPending(NoUiFade.IsStartupArmingPending, NoUiFade.StatusText)
            || IsStartupArmingPending(QueuePositionDisplay.IsStartupArmingPending, QueuePositionDisplay.StatusText)
            || IsStartupArmingPending(SightDistance.IsStartupArmingPending, SightDistance.StatusText)
            || IsStartupArmingPending(BetterHighlightPotentialTargets.IsStartupArmingPending, BetterHighlightPotentialTargets.StatusText);
    }

    private string DescribePendingStartupArming()
    {
        var pending = new List<string>();
        if (IsStartupArmingPending(AutoSkipCutscenes.IsStartupArmingPending, AutoSkipCutscenes.StatusText))
            pending.Add($"Auto Skip Cutscenes: {AutoSkipCutscenes.StatusText}");

        if (IsStartupArmingPending(SystemWindowMods.IsAllowMultipleGameInstancesStartupPending, SystemWindowMods.AllowMultipleGameInstancesStatusText))
            pending.Add($"Allow Multiple Game Instances: {SystemWindowMods.AllowMultipleGameInstancesStatusText}");

        if (IsStartupArmingPending(SystemWindowMods.IsCancelLoginCooldownStartupArmingPending, SystemWindowMods.CancelLoginCooldownStatusText))
            pending.Add($"Cancel Login Cooldown: {SystemWindowMods.CancelLoginCooldownStatusText}");

        if (IsStartupArmingPending(SystemWindowMods.IsPreventLobbyExitStartupArmingPending, SystemWindowMods.PreventLobbyExitStatusText))
            pending.Add($"Prevent Lobby Exit: {SystemWindowMods.PreventLobbyExitStatusText}");

        if (IsStartupArmingPending(ChatTimestampFormat.IsStartupArmingPending, ChatTimestampFormat.StatusText))
            pending.Add($"Custom Timestamp Format: {ChatTimestampFormat.StatusText}");

        if (IsStartupArmingPending(NoUiFade.IsStartupArmingPending, NoUiFade.StatusText))
            pending.Add($"No UI Fade: {NoUiFade.StatusText}");

        if (IsStartupArmingPending(QueuePositionDisplay.IsStartupArmingPending, QueuePositionDisplay.StatusText))
            pending.Add($"Queue Position Display: {QueuePositionDisplay.StatusText}");

        if (IsStartupArmingPending(SightDistance.IsStartupArmingPending, SightDistance.StatusText))
            pending.Add($"Custom Sight Distance: {SightDistance.StatusText}");

        if (IsStartupArmingPending(BetterHighlightPotentialTargets.IsStartupArmingPending, BetterHighlightPotentialTargets.StatusText))
            pending.Add($"Better Highlight Potential Targets: {BetterHighlightPotentialTargets.StatusText}");

        return pending.Count == 0 ? "None" : string.Join("; ", pending);
    }

    private static bool IsStartupArmingPending(bool pendingFlag, string statusText)
    {
        return pendingFlag
            || statusText.StartsWith("Arming", StringComparison.OrdinalIgnoreCase);
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
        var pending = requestedSurfaces
            .Where(surface => !surface.IsReady && surface.IsPending)
            .ToList();
        var unavailable = requestedSurfaces
            .Where(surface => !surface.IsReady && !surface.IsPending)
            .ToList();

        var summary = (pending.Count, unavailable.Count) switch
        {
            (0, 0) => $"[XASlave] Plugin loaded successfully. {armedCount} hook-backed startup XA Mods armed.",
            (_, 0) => $"[XASlave] Plugin loaded successfully. {armedCount} hook-backed startup XA Mods armed, {pending.Count} still arming.",
            (0, _) => $"[XASlave] Plugin loaded successfully. {armedCount} hook-backed startup XA Mods armed, {unavailable.Count} unavailable. Open XA Mods for current status.",
            _ => $"[XASlave] Plugin loaded successfully. {armedCount} hook-backed startup XA Mods armed, {pending.Count} still arming, {unavailable.Count} unavailable. Open XA Mods for current status.",
        };
        Log.Information(summary);

        if (pending.Count > 0)
            Log.Information($"[XASlave] Startup still arming: {string.Join("; ", pending.Select(surface => $"{surface.Name}: {surface.StatusText}"))}");

        if (unavailable.Count > 0)
            Log.Warning($"[XASlave] Startup unavailable: {string.Join("; ", unavailable.Select(surface => $"{surface.Name}: {surface.StatusText}"))}");
    }

    private IEnumerable<StartupSurfaceStatus> GetStartupSurfaceStatuses()
    {
        yield return CreateStartupSurfaceStatus("Allow Multiple Game Instances", Configuration.AutoAllowMultipleGameInstancesEnabled, SystemWindowMods.AllowMultipleGameInstancesStatusText);
        yield return CreateStartupSurfaceStatus("Cancel Login Cooldown", Configuration.AutoCancelLoginCooldownEnabled, SystemWindowMods.CancelLoginCooldownStatusText);
        yield return CreateStartupSurfaceStatus("Auto Skip Cutscenes", Configuration.AutoSkipCutscenesEnabled, AutoSkipCutscenes.StatusText, "AutoSkipCutscenesEnabled");
        yield return CreateStartupSurfaceStatus("Prevent Lobby Exit", Configuration.AutoPreventGameExitingFromLobbyErrorsEnabled, SystemWindowMods.PreventLobbyExitStatusText);
        yield return CreateStartupSurfaceStatus("Queue Position Display", Configuration.DisplayActualQueuePositionEnabled, QueuePositionDisplay.StatusText, "DisplayActualQueuePositionEnabled");
        yield return CreateStartupSurfaceStatus("Replace Unowned Mount Hotbars", Configuration.ReplaceUnownedMountHotbarsEnabled, ReplaceUnownedMountHotbars.StatusText, "ReplaceUnownedMountHotbarsEnabled");
        yield return CreateStartupSurfaceStatus("Disable Title Screen Movie", Configuration.DisableTitleScreenMovieEnabled, SystemWindowMods.DisableTitleScreenMovieStatusText);
        yield return CreateStartupSurfaceStatus("Auto Display IDs", Configuration.AutoDisplayIdsEnabled, AutoDisplayIds.StatusText);
        yield return CreateStartupSurfaceStatus("Display Network Latency", Configuration.AutoDisplayNetworkLatencyEnabled, AutoDisplayNetworkLatency.StatusText);
        yield return CreateStartupSurfaceStatus("Custom Timestamp Format", Configuration.CustomTimestampFormatEnabled, ChatTimestampFormat.StatusText, "CustomTimestampFormatEnabled");
        yield return CreateStartupSurfaceStatus("No UI Fade", Configuration.NoUiFadeEnabled, NoUiFade.StatusText, "NoUiFadeEnabled");
        yield return CreateStartupSurfaceStatus("Better Highlight Potential Targets", Configuration.BetterHighlightPotentialTargetsEnabled, BetterHighlightPotentialTargets.StatusText, "BetterHighlightPotentialTargetsEnabled");
        yield return CreateStartupSurfaceStatus("Auto Hide Game Objects", Configuration.AutoHideGameObjectsEnabled, AutoHideGameObjects.StatusText, "AutoHideGameObjectsEnabled");
        yield return CreateStartupSurfaceStatus("Skip Dialogue", Configuration.AutoSkipDialogueEnabled, DialogueSkip.StatusText);
        yield return CreateStartupSurfaceStatus("Lock Game Window In Combat", Configuration.LockGameWindowInCombatEnabled, AutoLockGameWindow.StatusText);
        yield return CreateStartupSurfaceStatus("Notify When Friend Is Near", Configuration.NotifyWhenFriendIsNearEnabled, NotifyWhenFriendIsNear.StatusText);
        yield return CreateStartupSurfaceStatus("Alert When Typing In Combat", Configuration.AlertWhenTypingInCombatEnabled, AlertWhenTypingInCombat.StatusText);
        yield return CreateStartupSurfaceStatus("Better Cast Bar", Configuration.BetterCastBarEnabled, BetterCastBar.StatusText);
        yield return CreateStartupSurfaceStatus("Better Duty Finder", Configuration.BetterDutyFinderEnabled, BetterDutyFinder.StatusText);
        yield return CreateStartupSurfaceStatus("Background Rendering Pause", Configuration.DisableBackgroundGameRenderingEnabled, SystemWindowMods.DisableBackgroundRenderingStatusText, "DisableBackgroundGameRenderingEnabled");
        yield return CreateStartupSurfaceStatus("Custom Sight Distance", Configuration.CustomSightDistanceEnabled, SightDistance.StatusText, "CustomSightDistanceEnabled");
        yield return CreateStartupSurfaceStatus("Instant Return", Configuration.QuickReturnEnabled, QuickReturn.StatusText, "QuickReturnEnabled");
        yield return CreateStartupSurfaceStatus("Auto Refuse Trade", Configuration.AutoRefuseTradeRequestEnabled, AutoRefuseTrade.StatusText, "AutoRefuseTradeRequestEnabled");
        yield return CreateStartupSurfaceStatus("Show Titles As Playernames", Configuration.ShowTitlesAsPlayernamesEnabled, NameplatePrivacy.ShowTitlesAsPlayernamesStatusText);
        yield return CreateStartupSurfaceStatus("Show Blacklisted Playername In Party", Configuration.ShowBlacklistedPlayernameInPartyEnabled, BlacklistedPartyName.StatusText);
        yield return CreateStartupSurfaceStatus("Show Traveler World Names", Configuration.ShowTravelerWorldNamesEnabled, NameplatePrivacy.ShowTravelerWorldNamesStatusText);
        yield return CreateStartupSurfaceStatus("Fix /target Command", Configuration.TargetCommandFixEnabled, TargetCommandFix.StatusText);
        yield return CreateStartupSurfaceStatus("Better Inventory Mover", Configuration.BetterInventoryMoverEnabled, BetterInventoryMover.StatusText);
        yield return CreateStartupSurfaceStatus("Better Company Chest", Configuration.BetterCompanyChestEnabled, BetterCompanyChest.StatusText);
        yield return CreateStartupSurfaceStatus("Auto Open Moogle Mail", Configuration.AutoOpenMoogleMailEnabled, AutoOpenMoogleMail.StatusText);
        yield return CreateStartupSurfaceStatus("Enable Item Icon In Shops", Configuration.EnableItemIconInShopsEnabled, EnableItemIconInShops.StatusText);
        yield return CreateStartupSurfaceStatus("Field Operations Entry Command", Configuration.FieldEntryCommandEnabled, FieldEntryCommand.StatusText);
        yield return CreateStartupSurfaceStatus("Reveal Undiscovered Areas", Configuration.AutoRevealUndiscoveredAreasEnabled, SystemWindowMods.RevealUndiscoveredAreasStatusText, "AutoRevealUndiscoveredAreasEnabled");
        yield return CreateStartupSurfaceStatus("Doze & Sit Anywhere", Configuration.DozeSitAnywhereEnabled, DozeSitAnywhere.StatusText);
        yield return CreateStartupSurfaceStatus("Auto Duty Commence", Configuration.AutoDutyCommenceEnabled, AutoDutyCommence.StatusText);
        yield return CreateStartupSurfaceStatus("Infinite Sprint", Configuration.InfiniteSprintEnabled, PlayerMods.InfiniteSprintStatusText);
        yield return CreateStartupSurfaceStatus("Instant Logout", Configuration.InstantLogoutEnabled, InstantLogout.StatusText);
        yield return CreateStartupSurfaceStatus("Teleport Helper", Configuration.TeleportHelperEnabled, TeleportHelper.StatusText);
        yield return CreateStartupSurfaceStatus("Unlock Expert Delivery", Configuration.UnlockExpertDeliveryEnabled, ExpertDeliveryUnlock.StatusText);
        yield return CreateStartupSurfaceStatus("Moveable After Death", Configuration.MoveableAfterDeathEnabled, PlayerMods.MoveableAfterDeathStatusText);
    }

    private StartupSurfaceStatus CreateStartupSurfaceStatus(string name, bool requested, string statusText, string? postLoadActivationKey = null)
    {
        var postLoadActivationPending = postLoadActivationKey is not null
            && pendingPostLoadXAModActivations.Contains(postLoadActivationKey);
        var effectiveStatusText = postLoadActivationPending
            ? "Scheduled - post-load XA Mod activation will arm after core plugin load."
            : statusText;

        return new StartupSurfaceStatus(
            name,
            requested,
            effectiveStatusText,
            IsStartupSurfaceReady(effectiveStatusText),
            postLoadActivationPending || IsStartupSurfacePending(effectiveStatusText));
    }

    private static bool IsStartupSurfaceReady(string statusText)
    {
        return statusText.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase)
            || statusText.StartsWith("Ready", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStartupSurfacePending(string statusText)
    {
        return statusText.StartsWith("Arming", StringComparison.OrdinalIgnoreCase);
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
        TryCleanup("UiBuilder.Draw -= BetterCompanyChest.DrawOverlay", () => PluginInterface.UiBuilder.Draw -= BetterCompanyChest.DrawOverlay);
        TryCleanup("UiBuilder.Draw -= AutoOpenMoogleMail.DrawOverlay", () => PluginInterface.UiBuilder.Draw -= AutoOpenMoogleMail.DrawOverlay);
        TryCleanup("UiBuilder.Draw -= BetterCastBar.DrawOverlay", () => PluginInterface.UiBuilder.Draw -= BetterCastBar.DrawOverlay);
        TryCleanup("UiBuilder.Draw -= BetterDutyFinder.DrawOverlay", () => PluginInterface.UiBuilder.Draw -= BetterDutyFinder.DrawOverlay);
        TryCleanup("UiBuilder.Draw -= XAPeep.DrawOverlay", () => PluginInterface.UiBuilder.Draw -= XAPeep.DrawOverlay);
        TryCleanup("UiBuilder.OpenConfigUi -= ToggleMainUi", () => PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi);
        TryCleanup("UiBuilder.OpenMainUi -= ToggleMainUi", () => PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi);
        TryCleanup("ClientState.Login -= OnLogin", () => ClientState.Login -= OnLogin);
        TryCleanup("ClientState.Logout -= OnLogout", () => ClientState.Logout -= OnLogout);
        TryCleanup($"CommandManager.RemoveHandler({CommandName})", () => CommandManager.RemoveHandler(CommandName));
        TryCleanup("WindowSystem.RemoveAllWindows", WindowSystem.RemoveAllWindows);
        TryCleanup("RestoreSpecialRenderModes", () =>
        {
            if (hasAppliedSpecialRenderUiFlags)
                RestoreSpecialRenderModes(clearStoredUiToggles: false);
        });

        TryDispose("SlaveWindow", SlaveWindow);
        TryCleanup("XagmanPeers.Stop", XagmanPeers.Stop);
        TryDispose("TaskRunner", TaskRunner);
        TryDispose("AutoCollector", AutoCollector);

        TryDispose("AutoSkipCutscenes", AutoSkipCutscenes);
        TryDispose("BuddyFeedCutsceneSkip", BuddyFeedCutsceneSkip);
        TryDispose("PopupCleaner", PopupCleaner);
        TryDispose("DalamudNotificationsSuck", DalamudNotificationsSuck);
        TryDispose("DalamudLogDisabler", DalamudLogDisabler);
        TryDispose("BetterHighlightPotentialTargets", BetterHighlightPotentialTargets);
        TryDispose("SystemWindowMods", SystemWindowMods);
        TryDispose("ReplaceUnownedMountHotbars", ReplaceUnownedMountHotbars);
        TryDispose("LobbyErrorAutoClose", LobbyErrorAutoClose);
        TryDispose("QueuePositionDisplay", QueuePositionDisplay);
        TryDispose("MsqProgressDisplay", MsqProgressDisplay);
        TryDispose("TooltipItemId", TooltipItemId);
        TryDispose("AutoDisplayIds", AutoDisplayIds);
        TryDispose("AutoDisplayNetworkLatency", AutoDisplayNetworkLatency);
        TryDispose("ChatTimestampFormat", ChatTimestampFormat);
        TryDispose("NoUiFade", NoUiFade);
        TryDispose("AutoHideGameObjects", AutoHideGameObjects);
        TryDispose("DialogueSkip", DialogueSkip);
        TryDispose("AutoLockGameWindow", AutoLockGameWindow);
        TryDispose("NotifyWhenFriendIsNear", NotifyWhenFriendIsNear);
        TryDispose("AlertWhenTypingInCombat", AlertWhenTypingInCombat);
        TryDispose("BetterCastBar", BetterCastBar);
        TryDispose("BetterDutyFinder", BetterDutyFinder);
        TryDispose("CopyItemNameContextMenu", CopyItemNameContextMenu);
        TryDispose("SightDistance", SightDistance);
        TryDispose("PlayerSearchContextMenu", PlayerSearchContextMenu);
        TryDispose("NameplatePrivacy", NameplatePrivacy);
        TryDispose("BlacklistedPartyName", BlacklistedPartyName);
        TryDispose("AutoUnlockExpertDelivery", AutoUnlockExpertDelivery);
        TryDispose("ExpertDeliveryUnlock", ExpertDeliveryUnlock);
        TryDispose("AutoRefuseTrade", AutoRefuseTrade);
        TryDispose("TargetCommandFix", TargetCommandFix);
        TryDispose("AntiAfk", AntiAfk);
        TryDispose("AutoDutyCommence", AutoDutyCommence);
        TryDispose("AutoLeaveDuty", AutoLeaveDuty);
        TryDispose("BetterInventoryMover", BetterInventoryMover);
        TryDispose("BetterCompanyChest", BetterCompanyChest);
        TryDispose("AutoOpenMoogleMail", AutoOpenMoogleMail);
        TryDispose("EnableItemIconInShops", EnableItemIconInShops);
        TryDispose("FieldEntryCommand", FieldEntryCommand);
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
        TryDispose("ARealmRecordedIntegration", ARealmRecordedIntegration);
        TryDispose("TeleportHelper", TeleportHelper);
        TryDispose("ArPostProcessor", ArPostProcessor);
        TryDispose("WindowRenamer", WindowRenamer);
        TryDispose("IpcProvider", IpcProvider);
        TryDispose("ExternalTaskLoader", ExternalTaskLoader);
        TryDispose("XagmanPeers", XagmanPeers);
        TryDispose("SlaveDatabase", SlaveDatabase);
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

        // Xagman owns a narrower expected-logout window than the task runner's broad
        // relog suppression, so always let it distinguish expected from unexpected logout.
        var xagmanHandledLogout = SlaveWindow.HandleUnexpectedXagmanLogout(contentId);

        if (TaskRunner.IsRunning && !TaskRunner.SuppressLogoutCancel && !xagmanHandledLogout)
        {
            Log.Information("[XASlave] Character logged out, cancelling running task.");
            TaskRunner.AddLog("EVENT: Character logged out, cancelling running task.");
            TaskRunner.Cancel();
        }
        else if (TaskRunner.IsRunning && xagmanHandledLogout)
        {
            Log.Information("[XASlave] Character logged out during a handled Xagman operation, not cancelling.");
            TaskRunner.AddLog("EVENT: Character logged out during a handled Xagman operation, not cancelling.");
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

        if (IsFieldEntrySubcommand(subcommand))
        {
            PrintCommandResult(TryStartFieldEntryCommand(subcommandArgs, out var message), message);
            return;
        }

        if (subcommand.Equals("commands", StringComparison.OrdinalIgnoreCase))
        {
            SlaveWindow.OpenCommandsReferenceTask();
            return;
        }

        if (subcommand.Equals("debug", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(SlaveWindow.ToggleDebugMenu(out var message), message);
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

        if (subcommand.Equals("mail", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(TryOpenMoogleMailCommand(subcommandArgs, out var message), message);
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

        if (subcommand.Equals("uirestore", StringComparison.OrdinalIgnoreCase))
        {
            PrintCommandResult(RestoreXAModsSection(XAModsRestoreScope.Ui, out var message), message);
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

        if (SlaveWindow.TryExecuteXaMovementCommand(subcommand, subcommandArgs, out var movementMessage, out var movementHandled))
        {
            PrintCommandResult(true, movementMessage);
            return;
        }

        if (movementHandled)
        {
            PrintCommandResult(false, movementMessage);
            return;
        }

        if (TryHandleXAModToggleCommand(subcommand, subcommandArgs))
            return;

        ChatGui.PrintError("[XASlave] That is not a command, please read `/xa commands`.");
    }

    private bool TryStartFieldEntryCommand(string arguments, out string message)
    {
        var value = arguments.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            message = FieldEntryCommand.BuildUsageText();
            return false;
        }

        var started = FieldEntryCommand.TryStart(value);
        message = !started && FieldEntryCommand.StatusText.StartsWith("Unavailable", StringComparison.OrdinalIgnoreCase)
            ? FieldEntryCommand.StatusText
            : FieldEntryCommand.LastActionText;
        return started;
    }

    private bool TryOpenMoogleMailCommand(string arguments, out string message)
    {
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            message = "Usage: /xa mail.";
            return false;
        }

        var opened = AutoOpenMoogleMail.TryOpenLetterListFromCommand();
        message = opened
            ? "Opened Moogle Mail."
            : AutoOpenMoogleMail.LastActionText.Replace("Last action: ", string.Empty, StringComparison.Ordinal);
        return opened;
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

        if (IsFieldEntrySubcommand(subcommand))
            return TryStartFieldEntryCommand(subcommandArgs, out message);

        if (subcommand.Equals("commands", StringComparison.OrdinalIgnoreCase))
        {
            SlaveWindow.OpenCommandsReferenceTask();
            message = "Opened Commands reference.";
            return true;
        }

        if (subcommand.Equals("debug", StringComparison.OrdinalIgnoreCase))
            return SlaveWindow.ToggleDebugMenu(out message);

        if (subcommand.Equals("peep", StringComparison.OrdinalIgnoreCase))
            return TryHandleXAPeepCommand(subcommandArgs, out message);

        if (subcommand.Equals("mail", StringComparison.OrdinalIgnoreCase))
            return TryOpenMoogleMailCommand(subcommandArgs, out message);

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

        if (subcommand.Equals("uirestore", StringComparison.OrdinalIgnoreCase))
            return RestoreXAModsSection(XAModsRestoreScope.Ui, out message);

        if (subcommand.Equals("playerrestore", StringComparison.OrdinalIgnoreCase))
            return RestoreXAModsSection(XAModsRestoreScope.Player, out message);

        if (subcommand.Equals("pluginrestore", StringComparison.OrdinalIgnoreCase))
            return RestoreXAModsSection(XAModsRestoreScope.Plugin, out message);

        if (subcommand.Equals("eurekarestore", StringComparison.OrdinalIgnoreCase))
            return RestoreXAModsSection(XAModsRestoreScope.Eureka, out message);

        if (subcommand.Equals("imlegit", StringComparison.OrdinalIgnoreCase))
            return RestoreXAModsSection(XAModsRestoreScope.Illegal, out message);

        if (SlaveWindow.TryExecuteXaMovementCommand(subcommand, subcommandArgs, out message, out var movementHandled))
            return true;

        if (movementHandled)
            return false;

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
        if (IsExplicitLowResolutionOff(value))
        {
            SystemWindowMods.SetLowResolutionEnabled(false);
            Configuration.LowResolutionEnabled = false;
            Configuration.Save();
            message = "Low Resolution disabled.";
            return true;
        }

        if (IsExplicitLowResolutionOn(value))
        {
            Configuration.LowResolutionScale = SystemWindowModsService.ClampLowResolutionScale(Configuration.LowResolutionScale);
            return SetXAModEnabled(GetXAModDefinition("low-resolution"), true, out message);
        }

        if (!TryParseLowResolutionCommand(value, out var scale, out message))
            return false;

        Configuration.LowResolutionScale = scale;
        if (!Configuration.LowResolutionEnabled)
        {
            if (!SetXAModEnabled(GetXAModDefinition("low-resolution"), true, out message))
                return false;

            message = $"Low Resolution enabled and scale set to {scale:0.00}.";
            return true;
        }

        SystemWindowMods.ApplyLowResolutionConfiguration(scale);
        Configuration.Save();
        message = $"Low Resolution scale set to {scale:0.00}.";
        return true;
    }

    private static bool IsExplicitLowResolutionOn(string value)
    {
        return value.Equals("on", StringComparison.OrdinalIgnoreCase)
            || value.Equals("enable", StringComparison.OrdinalIgnoreCase)
            || value.Equals("enabled", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExplicitLowResolutionOff(string value)
    {
        return value.Equals("off", StringComparison.OrdinalIgnoreCase)
            || value.Equals("disable", StringComparison.OrdinalIgnoreCase)
            || value.Equals("disabled", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("0", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseLowResolutionCommand(string value, out float scale, out string message)
    {
        scale = 0f;
        if (string.IsNullOrWhiteSpace(value))
        {
            message = "Usage: /xa lowres on, /xa lowres <scale>, or /xa lowres off.";
            return false;
        }

        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out scale))
        {
            message = "Usage: /xa lowres on, /xa lowres <scale>, or /xa lowres off.";
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
            case "arealmrecorded-all-zones":
                snapshot = JsonSerializer.SerializeToElement(new XAModARealmRecordedAllZonesSettings
                {
                    AllContentTypes = Configuration.ARealmRecordedAllZonesAllContentTypes,
                    SelectedContentTypes = Configuration.ARealmRecordedAllZonesSelectedContentTypes.ToList(),
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "dalamud-log-disabler":
                snapshot = JsonSerializer.SerializeToElement(new XAModDalamudLogDisablerSettings
                {
                    BlockedPlugins = Configuration.DalamudLogDisablerBlockedPlugins.ToList(),
                    MinimumKeptLevel = Configuration.DalamudLogDisablerMinimumKeptLevel,
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
            case "auto-skip-cutscenes":
                snapshot = JsonSerializer.SerializeToElement(new XAModAutoSkipCutscenesSettings
                {
                    UseZoneWhitelist = Configuration.AutoSkipCutscenesUseZoneWhitelist,
                    WhitelistTerritories = NormalizeTerritoryList(Configuration.AutoSkipCutscenesWhitelistTerritories),
                    BlacklistTerritories = NormalizeTerritoryList(Configuration.AutoSkipCutscenesBlacklistTerritories),
                    SkipNormalCutscenes = Configuration.AutoSkipCutscenesSkipNormalCutscenes,
                    SkipMsqRoulette = Configuration.AutoSkipCutscenesSkipMsqRoulette,
                    AutoEnableMsqFourPlayer = Configuration.AutoSkipCutscenesAutoEnableMsqFourPlayer,
                    ExemptPraetorium = Configuration.AutoSkipCutscenesExemptPraetorium,
                    ExemptCastrum = Configuration.AutoSkipCutscenesExemptCastrum,
                    ExemptPortaDecumana = Configuration.AutoSkipCutscenesExemptPortaDecumana,
                    SkipMassivePc = Configuration.AutoSkipCutscenesSkipMassivePc,
                    SkipGoldSaucer = Configuration.AutoSkipCutscenesSkipGoldSaucer,
                    GoldSaucerMahjong = Configuration.AutoSkipCutscenesGoldSaucerMahjong,
                    GoldSaucerAirForceOne = Configuration.AutoSkipCutscenesGoldSaucerAirForceOne,
                    GoldSaucerChocoboRacing = Configuration.AutoSkipCutscenesGoldSaucerChocoboRacing,
                    GoldSaucerLordOfVerminion = Configuration.AutoSkipCutscenesGoldSaucerLordOfVerminion,
                    GoldSaucerTripleTriad = Configuration.AutoSkipCutscenesGoldSaucerTripleTriad,
                    GoldSaucerBlunderville = Configuration.AutoSkipCutscenesGoldSaucerBlunderville,
                    GoldSaucerFashionReport = Configuration.AutoSkipCutscenesGoldSaucerFashionReport,
                    SkipCustomTalk = Configuration.AutoSkipCutscenesSkipCustomTalk,
                    SkipFeedBuddy = Configuration.AutoSkipCutscenesFeedingChocoboEnabled,
                    SkipOceanFishing = Configuration.AutoSkipCutscenesSkipOceanFishing,
                    SkipCrystallineConflict = Configuration.AutoSkipCutscenesSkipCrystallineConflict,
                    SkipFrontlineRivalWings = Configuration.AutoSkipCutscenesSkipFrontlineRivalWings,
                    SkipInn = Configuration.AutoSkipCutscenesSkipInn,
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
            case "custom-timestamp-format":
                snapshot = JsonSerializer.SerializeToElement(new XAModCustomTimestampFormatSettings
                {
                    Format = Configuration.CustomTimestampFormat,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "auto-display-ids":
                snapshot = JsonSerializer.SerializeToElement(new XAModAutoDisplayIdsSettings
                {
                    ShowItemId = Configuration.AutoDisplayIdsShowItemId,
                    ShowActionId = Configuration.AutoDisplayIdsShowActionId,
                    ShowTargetDataId = Configuration.AutoDisplayIdsShowTargetDataId,
                    ShowWeatherId = Configuration.AutoDisplayIdsShowWeatherId,
                    ShowZoneInfo = Configuration.AutoDisplayIdsShowZoneInfo,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "display-network-latency":
                snapshot = JsonSerializer.SerializeToElement(new XAModNetworkLatencySettings
                {
                    Format = Configuration.AutoDisplayNetworkLatencyFormat,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "notify-when-friend-is-near":
                snapshot = JsonSerializer.SerializeToElement(new XAModNotifyWhenFriendIsNearSettings
                {
                    Patterns = Configuration.NotifyWhenFriendIsNearPatterns.ToList(),
                    CooldownSeconds = Configuration.NotifyWhenFriendIsNearCooldownSeconds,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "alert-when-typing-in-combat":
                snapshot = JsonSerializer.SerializeToElement(new XAModAlertWhenTypingInCombatSettings
                {
                    CooldownSeconds = Configuration.AlertWhenTypingInCombatCooldownSeconds,
                    ToneId = Configuration.AlertWhenTypingInCombatToneId,
                    BeepCount = Configuration.AlertWhenTypingInCombatBeepCount,
                    SoundVolume = Configuration.AlertWhenTypingInCombatSoundVolume,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "better-cast-bar":
                snapshot = JsonSerializer.SerializeToElement(new XAModBetterCastBarSettings
                {
                    InterruptedTextX = Configuration.BetterCastBarInterruptedTextPosition.X,
                    InterruptedTextY = Configuration.BetterCastBarInterruptedTextPosition.Y,
                    InterruptedTextSize = Configuration.BetterCastBarInterruptedTextSize,
                    ActionNameTextX = Configuration.BetterCastBarActionNamePosition.X,
                    ActionNameTextY = Configuration.BetterCastBarActionNamePosition.Y,
                    ActionNameTextSize = Configuration.BetterCastBarActionNameSize,
                    CastingTextX = Configuration.BetterCastBarCastingTextPosition.X,
                    CastingTextY = Configuration.BetterCastBarCastingTextPosition.Y,
                    CastingTextSize = Configuration.BetterCastBarCastingTextSize,
                    CastTimeTextX = Configuration.BetterCastBarCastTimeTextPosition.X,
                    CastTimeTextY = Configuration.BetterCastBarCastTimeTextPosition.Y,
                    CastTimeTextSize = Configuration.BetterCastBarCastTimeTextSize,
                    IconAlpha = Configuration.BetterCastBarIconAlpha,
                    IconX = Configuration.BetterCastBarIconPosition.X,
                    IconY = Configuration.BetterCastBarIconPosition.Y,
                    IconScaleX = Configuration.BetterCastBarIconScale.X,
                    IconScaleY = Configuration.BetterCastBarIconScale.Y,
                    SlidecastMode = Configuration.BetterCastBarSlidecastMode,
                    SlidecastThresholdMs = Configuration.BetterCastBarSlidecastThresholdMs,
                    SlidecastLineWidth = Configuration.BetterCastBarSlidecastLineWidth,
                    SlidecastLineHeight = Configuration.BetterCastBarSlidecastLineHeight,
                    SlidecastNotReadyColor = CreateColorSettings(
                        Configuration.BetterCastBarSlidecastNotReadyColor.X,
                        Configuration.BetterCastBarSlidecastNotReadyColor.Y,
                        Configuration.BetterCastBarSlidecastNotReadyColor.Z,
                        Configuration.BetterCastBarSlidecastNotReadyColor.W),
                    SlidecastReadyColor = CreateColorSettings(
                        Configuration.BetterCastBarSlidecastReadyColor.X,
                        Configuration.BetterCastBarSlidecastReadyColor.Y,
                        Configuration.BetterCastBarSlidecastReadyColor.Z,
                        Configuration.BetterCastBarSlidecastReadyColor.W),
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "better-inventory-mover":
                snapshot = JsonSerializer.SerializeToElement(new XAModBetterInventoryMoverSettings
                {
                    QuickMoveModifier = BetterInventoryMoverService.NormalizeModifier(Configuration.BetterInventoryMoverQuickMoveModifier).ToString(),
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "better-company-chest":
                snapshot = JsonSerializer.SerializeToElement(new XAModBetterCompanyChestSettings
                {
                    DefaultPage = Configuration.BetterCompanyChestDefaultPage,
                    QuickMoveEnabled = Configuration.BetterCompanyChestQuickMoveEnabled,
                    AutoConfirmNumericInput = Configuration.BetterCompanyChestAutoConfirmNumericInput,
                    ShowExchangeableValue = Configuration.BetterCompanyChestShowExchangeableValue,
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
            case "dalamud-notifications-suck":
                snapshot = JsonSerializer.SerializeToElement(new XAModDalamudNotificationsSuckSettings
                {
                    HideAll = Configuration.DalamudNotificationsSuckHideAll,
                    HideDalamudUpdates = Configuration.DalamudNotificationsSuckHideDalamudUpdates,
                    HidePluginLifecycle = Configuration.DalamudNotificationsSuckHidePluginLifecycle,
                    HidePluginErrors = Configuration.DalamudNotificationsSuckHidePluginErrors,
                    HideModManagerAlerts = Configuration.DalamudNotificationsSuckHideModManagerAlerts,
                    HideSuccessInfo = Configuration.DalamudNotificationsSuckHideSuccessInfo,
                    HideWarningsErrors = Configuration.DalamudNotificationsSuckHideWarningsErrors,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "better-highlight-potential-targets":
                snapshot = JsonSerializer.SerializeToElement(new XAModBetterHighlightPotentialTargetsSettings
                {
                    Color = BetterHighlightPotentialTargetsService.NormalizeHighlightColor(Configuration.BetterHighlightPotentialTargetsColor),
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "show-traveler-world-names":
                snapshot = JsonSerializer.SerializeToElement(new XAModShowTravelerWorldNamesSettings
                {
                    DisableInDuties = Configuration.ShowTravelerWorldNamesDisableInDuties,
                    AddSpacer = Configuration.ShowTravelerWorldNamesAddSpacer,
                }, ToonModsPresetSerialization.JsonOptions);
                return true;
            case "show-titles-as-playernames":
                snapshot = JsonSerializer.SerializeToElement(new XAModShowTitlesAsPlayernamesSettings
                {
                    HonorificSupport = Configuration.ShowTitlesAsPlayernamesHonorificSupportEnabled,
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
            case "teleport-helper":
                snapshot = JsonSerializer.SerializeToElement(new XAModTeleportHelperSettings
                {
                    SelectYes = Configuration.TeleportHelperSelectYes,
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
                    LogInDuty = Configuration.XAPeepLogInDuty,
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

        if (TryDeserializeXAModSettings(modSettings, "arealmrecorded-all-zones", out XAModARealmRecordedAllZonesSettings? aRealmRecordedSettings)
            && aRealmRecordedSettings != null)
        {
            Configuration.ARealmRecordedAllZonesAllContentTypes = aRealmRecordedSettings.AllContentTypes;
            Configuration.ARealmRecordedAllZonesSelectedContentTypes = aRealmRecordedSettings.SelectedContentTypes?.Distinct().ToList() ?? new List<uint>();
            ARealmRecordedIntegration.ApplyConfiguration(
                Configuration.ARealmRecordedAllZonesAllContentTypes,
                Configuration.ARealmRecordedAllZonesSelectedContentTypes);
        }

        if (TryDeserializeXAModSettings(modSettings, "dalamud-log-disabler", out XAModDalamudLogDisablerSettings? dalamudLogDisablerSettings)
            && dalamudLogDisablerSettings != null)
        {
            Configuration.DalamudLogDisablerBlockedPlugins = dalamudLogDisablerSettings.BlockedPlugins?
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            Configuration.DalamudLogDisablerMinimumKeptLevel = dalamudLogDisablerSettings.MinimumKeptLevel;
            DalamudLogDisabler.ApplyConfiguration(Configuration.DalamudLogDisablerBlockedPlugins, Configuration.DalamudLogDisablerMinimumKeptLevel);
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

        if (TryDeserializeXAModSettings(modSettings, "auto-skip-cutscenes", out XAModAutoSkipCutscenesSettings? autoSkipCutscenesSettings)
            && autoSkipCutscenesSettings != null)
        {
            Configuration.AutoSkipCutscenesUseZoneWhitelist = autoSkipCutscenesSettings.UseZoneWhitelist;
            Configuration.AutoSkipCutscenesWhitelistTerritories = NormalizeTerritoryList(autoSkipCutscenesSettings.WhitelistTerritories);
            Configuration.AutoSkipCutscenesBlacklistTerritories = NormalizeTerritoryList(autoSkipCutscenesSettings.BlacklistTerritories);
            Configuration.AutoSkipCutscenesSkipNormalCutscenes = autoSkipCutscenesSettings.SkipNormalCutscenes;
            Configuration.AutoSkipCutscenesSkipMsqRoulette = autoSkipCutscenesSettings.SkipMsqRoulette;
            Configuration.AutoSkipCutscenesAutoEnableMsqFourPlayer = autoSkipCutscenesSettings.AutoEnableMsqFourPlayer;
            Configuration.AutoSkipCutscenesExemptPraetorium = autoSkipCutscenesSettings.ExemptPraetorium;
            Configuration.AutoSkipCutscenesExemptCastrum = autoSkipCutscenesSettings.ExemptCastrum;
            Configuration.AutoSkipCutscenesExemptPortaDecumana = autoSkipCutscenesSettings.ExemptPortaDecumana;
            Configuration.AutoSkipCutscenesSkipMassivePc = autoSkipCutscenesSettings.SkipMassivePc;
            Configuration.AutoSkipCutscenesSkipGoldSaucer = autoSkipCutscenesSettings.SkipGoldSaucer;
            Configuration.AutoSkipCutscenesGoldSaucerMahjong = autoSkipCutscenesSettings.GoldSaucerMahjong;
            Configuration.AutoSkipCutscenesGoldSaucerAirForceOne = autoSkipCutscenesSettings.GoldSaucerAirForceOne;
            Configuration.AutoSkipCutscenesGoldSaucerChocoboRacing = autoSkipCutscenesSettings.GoldSaucerChocoboRacing;
            Configuration.AutoSkipCutscenesGoldSaucerLordOfVerminion = autoSkipCutscenesSettings.GoldSaucerLordOfVerminion;
            Configuration.AutoSkipCutscenesGoldSaucerTripleTriad = autoSkipCutscenesSettings.GoldSaucerTripleTriad;
            Configuration.AutoSkipCutscenesGoldSaucerBlunderville = autoSkipCutscenesSettings.GoldSaucerBlunderville;
            Configuration.AutoSkipCutscenesGoldSaucerFashionReport = autoSkipCutscenesSettings.GoldSaucerFashionReport;
            Configuration.AutoSkipCutscenesSkipCustomTalk = autoSkipCutscenesSettings.SkipCustomTalk;
            Configuration.AutoSkipCutscenesFeedingChocoboEnabled = autoSkipCutscenesSettings.SkipFeedBuddy;
            Configuration.AutoSkipCutscenesSkipOceanFishing = autoSkipCutscenesSettings.SkipOceanFishing;
            Configuration.AutoSkipCutscenesSkipCrystallineConflict = autoSkipCutscenesSettings.SkipCrystallineConflict;
            Configuration.AutoSkipCutscenesSkipFrontlineRivalWings = autoSkipCutscenesSettings.SkipFrontlineRivalWings;
            Configuration.AutoSkipCutscenesSkipInn = autoSkipCutscenesSettings.SkipInn;
            AutoSkipCutscenes.ApplyConfiguration(Configuration);
            BuddyFeedCutsceneSkip.SetEnabled(Configuration.AutoSkipCutscenesFeedingChocoboEnabled);
        }

        if (TryDeserializeXAModSettings(modSettings, "auto-hide-unnecessary-popups", out XAModPopupCleanerSettings? popupCleanerSettings)
            && popupCleanerSettings != null)
        {
            Configuration.AutoHideUnnecessaryPopupsHideHowToNoticeEnabled = popupCleanerSettings.HideHowToNotice;
            PopupCleaner.ApplyConfiguration(Configuration.AutoHideUnnecessaryPopupsHideHowToNoticeEnabled);
        }

        if (TryDeserializeXAModSettings(modSettings, "dalamud-notifications-suck", out XAModDalamudNotificationsSuckSettings? dalamudNotificationSettings)
            && dalamudNotificationSettings != null)
        {
            Configuration.DalamudNotificationsSuckHideAll = dalamudNotificationSettings.HideAll;
            Configuration.DalamudNotificationsSuckHideDalamudUpdates = dalamudNotificationSettings.HideDalamudUpdates;
            Configuration.DalamudNotificationsSuckHidePluginLifecycle = dalamudNotificationSettings.HidePluginLifecycle;
            Configuration.DalamudNotificationsSuckHidePluginErrors = dalamudNotificationSettings.HidePluginErrors;
            Configuration.DalamudNotificationsSuckHideModManagerAlerts = dalamudNotificationSettings.HideModManagerAlerts;
            Configuration.DalamudNotificationsSuckHideSuccessInfo = dalamudNotificationSettings.HideSuccessInfo;
            Configuration.DalamudNotificationsSuckHideWarningsErrors = dalamudNotificationSettings.HideWarningsErrors;
            ApplyDalamudNotificationsSuckConfiguration(save: false);
        }

        if (TryDeserializeXAModSettings(modSettings, "better-highlight-potential-targets", out XAModBetterHighlightPotentialTargetsSettings? betterHighlightSettings)
            && betterHighlightSettings != null)
        {
            Configuration.BetterHighlightPotentialTargetsColor = BetterHighlightPotentialTargetsService.NormalizeHighlightColor(betterHighlightSettings.Color);
            ApplyBetterHighlightPotentialTargetsConfiguration(save: false);
        }

        if (TryDeserializeXAModSettings(modSettings, "show-traveler-world-names", out XAModShowTravelerWorldNamesSettings? travelerWorldNamesSettings)
            && travelerWorldNamesSettings != null)
        {
            Configuration.ShowTravelerWorldNamesDisableInDuties = travelerWorldNamesSettings.DisableInDuties;
            Configuration.ShowTravelerWorldNamesAddSpacer = travelerWorldNamesSettings.AddSpacer;
            NameplatePrivacy.ApplyShowTravelerWorldNamesConfiguration(
                Configuration.ShowTravelerWorldNamesDisableInDuties,
                Configuration.ShowTravelerWorldNamesAddSpacer);
        }

        if (TryDeserializeXAModSettings(modSettings, "show-titles-as-playernames", out XAModShowTitlesAsPlayernamesSettings? titlesAsPlayernamesSettings)
            && titlesAsPlayernamesSettings != null)
        {
            Configuration.ShowTitlesAsPlayernamesHonorificSupportEnabled = titlesAsPlayernamesSettings.HonorificSupport;
            NameplatePrivacy.ApplyShowTitlesAsPlayernamesConfiguration(Configuration.ShowTitlesAsPlayernamesHonorificSupportEnabled);
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

        if (TryDeserializeXAModSettings(modSettings, "custom-timestamp-format", out XAModCustomTimestampFormatSettings? timestampSettings)
            && timestampSettings != null)
        {
            Configuration.CustomTimestampFormat = ChatTimestampFormatService.NormalizeFormat(timestampSettings.Format);
            ChatTimestampFormat.ApplyConfiguration(Configuration.CustomTimestampFormat);
        }

        if (TryDeserializeXAModSettings(modSettings, "auto-display-ids", out XAModAutoDisplayIdsSettings? autoDisplayIdsSettings)
            && autoDisplayIdsSettings != null)
        {
            if (autoDisplayIdsSettings.ShowItemId.HasValue)
                Configuration.AutoDisplayIdsShowItemId = autoDisplayIdsSettings.ShowItemId.Value;
            Configuration.AutoDisplayIdsShowActionId = autoDisplayIdsSettings.ShowActionId;
            Configuration.AutoDisplayIdsShowTargetDataId = autoDisplayIdsSettings.ShowTargetDataId;
            Configuration.AutoDisplayIdsShowWeatherId = autoDisplayIdsSettings.ShowWeatherId;
            Configuration.AutoDisplayIdsShowZoneInfo = autoDisplayIdsSettings.ShowZoneInfo;
            ApplyAutoDisplayIdsConfiguration(save: false);
        }

        if (TryDeserializeXAModSettings(modSettings, "display-network-latency", out XAModNetworkLatencySettings? networkLatencySettings)
            && networkLatencySettings != null)
        {
            Configuration.AutoDisplayNetworkLatencyFormat = AutoDisplayNetworkLatencyService.NormalizeFormat(networkLatencySettings.Format);
            AutoDisplayNetworkLatency.ApplyConfiguration(Configuration.AutoDisplayNetworkLatencyFormat);
        }

        if (TryDeserializeXAModSettings(modSettings, "notify-when-friend-is-near", out XAModNotifyWhenFriendIsNearSettings? friendNearSettings)
            && friendNearSettings != null)
        {
            Configuration.NotifyWhenFriendIsNearPatterns = (friendNearSettings.Patterns ?? [])
                .Select(pattern => pattern.Trim())
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Configuration.NotifyWhenFriendIsNearCooldownSeconds = NotifyWhenFriendIsNearService.NormalizeCooldownSeconds(friendNearSettings.CooldownSeconds);
            NotifyWhenFriendIsNear.ApplyConfiguration(
                Configuration.NotifyWhenFriendIsNearPatterns,
                Configuration.NotifyWhenFriendIsNearCooldownSeconds);
        }

        if (TryDeserializeXAModSettings(modSettings, "alert-when-typing-in-combat", out XAModAlertWhenTypingInCombatSettings? typingCombatSettings)
            && typingCombatSettings != null)
        {
            Configuration.AlertWhenTypingInCombatCooldownSeconds = AlertWhenTypingInCombatService.NormalizeCooldownSeconds(typingCombatSettings.CooldownSeconds);
            Configuration.AlertWhenTypingInCombatToneId = AlertWhenTypingInCombatService.NormalizeToneId(typingCombatSettings.ToneId);
            Configuration.AlertWhenTypingInCombatBeepCount = AlertWhenTypingInCombatService.NormalizeBeepCount(typingCombatSettings.BeepCount);
            Configuration.AlertWhenTypingInCombatSoundVolume = AlertWhenTypingInCombatService.NormalizeVolume(typingCombatSettings.SoundVolume);
            AlertWhenTypingInCombat.ApplyConfiguration(
                Configuration.AlertWhenTypingInCombatCooldownSeconds,
                Configuration.AlertWhenTypingInCombatToneId,
                Configuration.AlertWhenTypingInCombatBeepCount,
                Configuration.AlertWhenTypingInCombatSoundVolume);
        }

        if (TryDeserializeXAModSettings(modSettings, "better-cast-bar", out XAModBetterCastBarSettings? betterCastBarSettings)
            && betterCastBarSettings != null)
        {
            var notReadyColor = betterCastBarSettings.SlidecastNotReadyColor ?? new XAModColorSettings();
            var readyColor = betterCastBarSettings.SlidecastReadyColor ?? new XAModColorSettings();

            Configuration.BetterCastBarInterruptedTextPosition = new Vector2(betterCastBarSettings.InterruptedTextX, betterCastBarSettings.InterruptedTextY);
            Configuration.BetterCastBarInterruptedTextSize = Math.Clamp(betterCastBarSettings.InterruptedTextSize, 1, 255);
            Configuration.BetterCastBarActionNamePosition = new Vector2(betterCastBarSettings.ActionNameTextX, betterCastBarSettings.ActionNameTextY);
            Configuration.BetterCastBarActionNameSize = Math.Clamp(betterCastBarSettings.ActionNameTextSize, 1, 255);
            Configuration.BetterCastBarCastingTextPosition = new Vector2(betterCastBarSettings.CastingTextX, betterCastBarSettings.CastingTextY);
            Configuration.BetterCastBarCastingTextSize = Math.Clamp(betterCastBarSettings.CastingTextSize, 1, 255);
            Configuration.BetterCastBarCastTimeTextPosition = new Vector2(betterCastBarSettings.CastTimeTextX, betterCastBarSettings.CastTimeTextY);
            Configuration.BetterCastBarCastTimeTextSize = Math.Clamp(betterCastBarSettings.CastTimeTextSize, 1, 255);
            Configuration.BetterCastBarIconAlpha = Math.Clamp(betterCastBarSettings.IconAlpha, 0, 255);
            Configuration.BetterCastBarIconPosition = new Vector2(betterCastBarSettings.IconX, betterCastBarSettings.IconY);
            Configuration.BetterCastBarIconScale = new Vector2(
                Math.Clamp(betterCastBarSettings.IconScaleX, 0.1f, 5f),
                Math.Clamp(betterCastBarSettings.IconScaleY, 0.1f, 5f));
            Configuration.BetterCastBarSlidecastMode = BetterCastBarService.NormalizeSlidecastMode(betterCastBarSettings.SlidecastMode);
            Configuration.BetterCastBarSlidecastThresholdMs = Math.Clamp(betterCastBarSettings.SlidecastThresholdMs, 0, 5000);
            Configuration.BetterCastBarSlidecastLineWidth = Math.Clamp(betterCastBarSettings.SlidecastLineWidth, 1, 20);
            Configuration.BetterCastBarSlidecastLineHeight = Math.Clamp(betterCastBarSettings.SlidecastLineHeight, 0, 100);
            Configuration.BetterCastBarSlidecastNotReadyColor = new Vector4(
                ClampUnitFloat(notReadyColor.R),
                ClampUnitFloat(notReadyColor.G),
                ClampUnitFloat(notReadyColor.B),
                ClampUnitFloat(notReadyColor.A));
            Configuration.BetterCastBarSlidecastReadyColor = new Vector4(
                ClampUnitFloat(readyColor.R),
                ClampUnitFloat(readyColor.G),
                ClampUnitFloat(readyColor.B),
                ClampUnitFloat(readyColor.A));
            BetterCastBar.ApplyConfiguration(Configuration);
        }

        if (TryDeserializeXAModSettings(modSettings, "better-inventory-mover", out XAModBetterInventoryMoverSettings? inventoryMoverSettings)
            && inventoryMoverSettings != null)
        {
            Configuration.BetterInventoryMoverQuickMoveModifier = ParseBetterInventoryMoverModifier(inventoryMoverSettings.QuickMoveModifier);
            ApplyBetterInventoryMoverConfiguration(save: false);
        }

        if (TryDeserializeXAModSettings(modSettings, "better-company-chest", out XAModBetterCompanyChestSettings? companyChestSettings)
            && companyChestSettings != null)
        {
            Configuration.BetterCompanyChestDefaultPage = Math.Clamp(companyChestSettings.DefaultPage, 0, 6);
            Configuration.BetterCompanyChestQuickMoveEnabled = companyChestSettings.QuickMoveEnabled;
            Configuration.BetterCompanyChestAutoConfirmNumericInput = companyChestSettings.AutoConfirmNumericInput;
            Configuration.BetterCompanyChestShowExchangeableValue = companyChestSettings.ShowExchangeableValue;
            BetterCompanyChest.ApplyConfiguration(
                Configuration.BetterCompanyChestDefaultPage,
                Configuration.BetterCompanyChestQuickMoveEnabled,
                Configuration.BetterCompanyChestAutoConfirmNumericInput,
                Configuration.BetterCompanyChestShowExchangeableValue);
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

        if (TryDeserializeXAModSettings(modSettings, "teleport-helper", out XAModTeleportHelperSettings? teleportHelperSettings)
            && teleportHelperSettings != null)
        {
            Configuration.TeleportHelperSelectYes = teleportHelperSettings.SelectYes;
            if (Configuration.TeleportHelperEnabled)
                TeleportHelper.ApplyConfiguration(Configuration.TeleportHelperSelectYes);
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
            Configuration.XAPeepLogInDuty = xaPeepSettings.LogInDuty;
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

    private static List<uint> NormalizeTerritoryList(IEnumerable<uint>? territories)
    {
        return territories?
            .Where(territory => territory > 0)
            .Distinct()
            .OrderBy(territory => territory)
            .ToList()
            ?? new List<uint>();
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

    private static Vector4 ClampVector4(Vector4 value)
        => new(
            ClampUnitFloat(value.X),
            ClampUnitFloat(value.Y),
            ClampUnitFloat(value.Z),
            ClampUnitFloat(value.W));

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

    internal void ApplyAutoDisplayIdsConfiguration(bool save = true)
    {
        var itemIdsShouldRun = Configuration.AutoDisplayIdsEnabled && Configuration.AutoDisplayIdsShowItemId;
        if (!itemIdsShouldRun)
        {
            TooltipItemId.SetEnabled(false);
            Configuration.ShowItemIdEnabled = false;
        }
        else if (TooltipItemId.SetEnabled(true))
        {
            Configuration.ShowItemIdEnabled = true;
        }
        else
        {
            Configuration.ShowItemIdEnabled = false;
        }

        AutoDisplayIds.ApplyConfiguration(
            Configuration.AutoDisplayIdsShowItemId,
            Configuration.AutoDisplayIdsShowActionId,
            Configuration.AutoDisplayIdsShowTargetDataId,
            Configuration.AutoDisplayIdsShowWeatherId,
            Configuration.AutoDisplayIdsShowZoneInfo);

        if (save)
            Configuration.Save();
    }

    internal void ApplyAutoDisplayNetworkLatencyConfiguration(bool save = true)
    {
        Configuration.AutoDisplayNetworkLatencyFormat = AutoDisplayNetworkLatencyService.NormalizeFormat(Configuration.AutoDisplayNetworkLatencyFormat);
        AutoDisplayNetworkLatency.ApplyConfiguration(Configuration.AutoDisplayNetworkLatencyFormat);

        if (save)
            Configuration.Save();
    }

    internal void ApplyDalamudNotificationsSuckConfiguration(bool save = true)
    {
        DalamudNotificationsSuck.ApplyConfiguration(new DalamudNotificationSuppressorOptions
        {
            HideAll = Configuration.DalamudNotificationsSuckHideAll,
            HideDalamudUpdates = Configuration.DalamudNotificationsSuckHideDalamudUpdates,
            HidePluginLifecycle = Configuration.DalamudNotificationsSuckHidePluginLifecycle,
            HidePluginErrors = Configuration.DalamudNotificationsSuckHidePluginErrors,
            HideModManagerAlerts = Configuration.DalamudNotificationsSuckHideModManagerAlerts,
            HideSuccessInfo = Configuration.DalamudNotificationsSuckHideSuccessInfo,
            HideWarningsErrors = Configuration.DalamudNotificationsSuckHideWarningsErrors,
        });

        if (save)
            Configuration.Save();
    }

    internal void ApplyBetterHighlightPotentialTargetsConfiguration(bool save = true)
    {
        Configuration.BetterHighlightPotentialTargetsColor = BetterHighlightPotentialTargetsService.NormalizeHighlightColor(Configuration.BetterHighlightPotentialTargetsColor);
        BetterHighlightPotentialTargets.ApplyConfiguration(Configuration.BetterHighlightPotentialTargetsColor);

        if (save)
            Configuration.Save();
    }

    internal void ApplyNotifyWhenFriendIsNearConfiguration(bool save = true)
    {
        Configuration.NotifyWhenFriendIsNearPatterns = Configuration.NotifyWhenFriendIsNearPatterns
            .Select(pattern => pattern.Trim())
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Configuration.NotifyWhenFriendIsNearCooldownSeconds = NotifyWhenFriendIsNearService.NormalizeCooldownSeconds(Configuration.NotifyWhenFriendIsNearCooldownSeconds);
        NotifyWhenFriendIsNear.ApplyConfiguration(
            Configuration.NotifyWhenFriendIsNearPatterns,
            Configuration.NotifyWhenFriendIsNearCooldownSeconds);

        if (save)
            Configuration.Save();
    }

    internal void ApplyAlertWhenTypingInCombatConfiguration(bool save = true)
    {
        Configuration.AlertWhenTypingInCombatCooldownSeconds = AlertWhenTypingInCombatService.NormalizeCooldownSeconds(Configuration.AlertWhenTypingInCombatCooldownSeconds);
        Configuration.AlertWhenTypingInCombatToneId = AlertWhenTypingInCombatService.NormalizeToneId(Configuration.AlertWhenTypingInCombatToneId);
        Configuration.AlertWhenTypingInCombatBeepCount = AlertWhenTypingInCombatService.NormalizeBeepCount(Configuration.AlertWhenTypingInCombatBeepCount);
        Configuration.AlertWhenTypingInCombatSoundVolume = AlertWhenTypingInCombatService.NormalizeVolume(Configuration.AlertWhenTypingInCombatSoundVolume);
        AlertWhenTypingInCombat.ApplyConfiguration(
            Configuration.AlertWhenTypingInCombatCooldownSeconds,
            Configuration.AlertWhenTypingInCombatToneId,
            Configuration.AlertWhenTypingInCombatBeepCount,
            Configuration.AlertWhenTypingInCombatSoundVolume);

        if (save)
            Configuration.Save();
    }

    internal void ApplyBetterCastBarConfiguration(bool save = true)
    {
        Configuration.BetterCastBarInterruptedTextSize = Math.Clamp(Configuration.BetterCastBarInterruptedTextSize, 1, 255);
        Configuration.BetterCastBarActionNameSize = Math.Clamp(Configuration.BetterCastBarActionNameSize, 1, 255);
        Configuration.BetterCastBarCastingTextSize = Math.Clamp(Configuration.BetterCastBarCastingTextSize, 1, 255);
        Configuration.BetterCastBarCastTimeTextSize = Math.Clamp(Configuration.BetterCastBarCastTimeTextSize, 1, 255);
        Configuration.BetterCastBarIconAlpha = Math.Clamp(Configuration.BetterCastBarIconAlpha, 0, 255);
        Configuration.BetterCastBarIconScale = new Vector2(
            Math.Clamp(Configuration.BetterCastBarIconScale.X, 0.1f, 5f),
            Math.Clamp(Configuration.BetterCastBarIconScale.Y, 0.1f, 5f));
        Configuration.BetterCastBarSlidecastMode = BetterCastBarService.NormalizeSlidecastMode(Configuration.BetterCastBarSlidecastMode);
        Configuration.BetterCastBarSlidecastThresholdMs = Math.Clamp(Configuration.BetterCastBarSlidecastThresholdMs, 0, 5000);
        Configuration.BetterCastBarSlidecastLineWidth = Math.Clamp(Configuration.BetterCastBarSlidecastLineWidth, 1, 20);
        Configuration.BetterCastBarSlidecastLineHeight = Math.Clamp(Configuration.BetterCastBarSlidecastLineHeight, 0, 100);
        Configuration.BetterCastBarSlidecastNotReadyColor = ClampVector4(Configuration.BetterCastBarSlidecastNotReadyColor);
        Configuration.BetterCastBarSlidecastReadyColor = ClampVector4(Configuration.BetterCastBarSlidecastReadyColor);
        BetterCastBar.ApplyConfiguration(Configuration);

        if (save)
            Configuration.Save();
    }

    internal void ApplyBetterInventoryMoverConfiguration(bool save = true)
    {
        Configuration.BetterInventoryMoverQuickMoveModifier = BetterInventoryMoverService.NormalizeModifier(Configuration.BetterInventoryMoverQuickMoveModifier);
        BetterInventoryMover.ApplyConfiguration(Configuration.BetterInventoryMoverQuickMoveModifier);

        if (save)
            Configuration.Save();
    }

    private static BetterInventoryMoverModifierKey ParseBetterInventoryMoverModifier(string? value)
    {
        return Enum.TryParse<BetterInventoryMoverModifierKey>(value, true, out var modifier)
            ? BetterInventoryMoverService.NormalizeModifier(modifier)
            : BetterInventoryMoverModifierKey.LeftShift;
    }

    internal void ApplyBetterCompanyChestConfiguration(bool save = true)
    {
        Configuration.BetterCompanyChestDefaultPage = Math.Clamp(Configuration.BetterCompanyChestDefaultPage, 0, 6);
        BetterCompanyChest.ApplyConfiguration(
            Configuration.BetterCompanyChestDefaultPage,
            Configuration.BetterCompanyChestQuickMoveEnabled,
            Configuration.BetterCompanyChestAutoConfirmNumericInput,
            Configuration.BetterCompanyChestShowExchangeableValue);

        if (save)
            Configuration.Save();
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
            case "auto-skip-cutscenes":
                AutoSkipCutscenes.ApplyConfiguration(Configuration);
                break;
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
            case "auto-display-ids":
                ApplyAutoDisplayIdsConfiguration(save: false);
                break;
            case "display-network-latency":
                ApplyAutoDisplayNetworkLatencyConfiguration(save: false);
                break;
            case "dalamud-notifications-suck":
                ApplyDalamudNotificationsSuckConfiguration(save: false);
                break;
            case "better-highlight-potential-targets":
                ApplyBetterHighlightPotentialTargetsConfiguration(save: false);
                break;
            case "show-traveler-world-names":
                NameplatePrivacy.ApplyShowTravelerWorldNamesConfiguration(
                    Configuration.ShowTravelerWorldNamesDisableInDuties,
                    Configuration.ShowTravelerWorldNamesAddSpacer);
                break;
            case "show-titles-as-playernames":
                NameplatePrivacy.ApplyShowTitlesAsPlayernamesConfiguration(
                    Configuration.ShowTitlesAsPlayernamesHonorificSupportEnabled);
                break;
            case "notify-when-friend-is-near":
                ApplyNotifyWhenFriendIsNearConfiguration(save: false);
                break;
            case "alert-when-typing-in-combat":
                ApplyAlertWhenTypingInCombatConfiguration(save: false);
                break;
            case "better-cast-bar":
                ApplyBetterCastBarConfiguration(save: false);
                break;
            case "better-inventory-mover":
                ApplyBetterInventoryMoverConfiguration(save: false);
                break;
            case "better-company-chest":
                ApplyBetterCompanyChestConfiguration(save: false);
                break;
            case "custom-timestamp-format":
                Configuration.CustomTimestampFormat = ChatTimestampFormatService.NormalizeFormat(Configuration.CustomTimestampFormat);
                ChatTimestampFormat.ApplyConfiguration(Configuration.CustomTimestampFormat);
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
            case "teleport-helper":
                TeleportHelper.ApplyConfiguration(Configuration.TeleportHelperSelectYes);
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
        yield return new("auto-display-msq-progress", "Display MSQ Progress", XAModsRestoreScope.Ui, () => Configuration.AutoDisplayMsqProgressEnabled, MsqProgressDisplay.SetEnabled, applied => Configuration.AutoDisplayMsqProgressEnabled = applied, () => MsqProgressDisplay.StatusText);
        yield return new("disable-title-screen-movie", "Disable Title Screen Movie", XAModsRestoreScope.Graphic, () => Configuration.DisableTitleScreenMovieEnabled, SystemWindowMods.SetDisableTitleScreenMovieEnabled, applied => Configuration.DisableTitleScreenMovieEnabled = applied, () => SystemWindowMods.DisableTitleScreenMovieStatusText);
        yield return new("auto-display-ids", "Auto Display IDs", XAModsRestoreScope.Ui, () => Configuration.AutoDisplayIdsEnabled, value =>
        {
            Configuration.AutoDisplayIdsEnabled = value;
            ApplyAutoDisplayIdsConfiguration(save: false);

            if (!value)
                return AutoDisplayIds.SetEnabled(false);

            return AutoDisplayIds.SetEnabled(true);
        }, applied => Configuration.AutoDisplayIdsEnabled = applied, () => AutoDisplayIds.StatusText);
        yield return new("display-network-latency", "Display Network Latency", XAModsRestoreScope.Ui, () => Configuration.AutoDisplayNetworkLatencyEnabled, value =>
        {
            ApplyAutoDisplayNetworkLatencyConfiguration(save: false);
            return AutoDisplayNetworkLatency.SetEnabled(value);
        }, applied => Configuration.AutoDisplayNetworkLatencyEnabled = applied, () => AutoDisplayNetworkLatency.StatusText);
        yield return new("custom-timestamp-format", "Custom Timestamp Format", XAModsRestoreScope.Ui, () => Configuration.CustomTimestampFormatEnabled, ChatTimestampFormat.SetEnabled, applied => Configuration.CustomTimestampFormatEnabled = applied, () => ChatTimestampFormat.StatusText);
        yield return new("no-ui-fade", "No UI Fade", XAModsRestoreScope.Graphic, () => Configuration.NoUiFadeEnabled, NoUiFade.SetEnabled, applied => Configuration.NoUiFadeEnabled = applied, () => NoUiFade.StatusText);
        yield return new("auto-skip-cutscenes", "Skip Cutscenes", XAModsRestoreScope.Game, () => Configuration.AutoSkipCutscenesEnabled, value =>
        {
            AutoSkipCutscenes.ApplyConfiguration(Configuration);
            return AutoSkipCutscenes.SetEnabled(value);
        }, applied => Configuration.AutoSkipCutscenesEnabled = applied, () => AutoSkipCutscenes.StatusText);
        yield return new("auto-skip-cutscenes-feeding-chocobo", "Skip Cutscenes Feeding Chocobo", XAModsRestoreScope.Game, () => Configuration.AutoSkipCutscenesFeedingChocoboEnabled, BuddyFeedCutsceneSkip.SetEnabled, applied => Configuration.AutoSkipCutscenesFeedingChocoboEnabled = applied, () => BuddyFeedCutsceneSkip.StatusText);
        yield return new("auto-hide-unnecessary-popups", "Hide Unnecessary Popups", XAModsRestoreScope.Graphic, () => Configuration.AutoHideUnnecessaryPopupsEnabled, PopupCleaner.SetEnabled, applied => Configuration.AutoHideUnnecessaryPopupsEnabled = applied, () => PopupCleaner.StatusText);
        yield return new("dalamud-notifications-suck", "Dalamud Notifications Suck", XAModsRestoreScope.Ui, () => Configuration.DalamudNotificationsSuckEnabled, value =>
        {
            ApplyDalamudNotificationsSuckConfiguration(save: false);
            return DalamudNotificationsSuck.SetEnabled(value);
        }, applied => Configuration.DalamudNotificationsSuckEnabled = applied, () => DalamudNotificationsSuck.StatusText);
        yield return new("dalamud-log-disabler", "Dalamud Log Disabler", XAModsRestoreScope.Game, () => Configuration.DalamudLogDisablerEnabled, value =>
        {
            DalamudLogDisabler.ApplyConfiguration(Configuration.DalamudLogDisablerBlockedPlugins, Configuration.DalamudLogDisablerMinimumKeptLevel);
            return DalamudLogDisabler.SetEnabled(value);
        }, applied => Configuration.DalamudLogDisablerEnabled = applied, () => DalamudLogDisabler.StatusText);
        yield return new("better-highlight-potential-targets", "Better Highlight Potential Targets", XAModsRestoreScope.Ui, () => Configuration.BetterHighlightPotentialTargetsEnabled, value =>
        {
            ApplyBetterHighlightPotentialTargetsConfiguration(save: false);
            return BetterHighlightPotentialTargets.SetEnabled(value);
        }, applied => Configuration.BetterHighlightPotentialTargetsEnabled = applied, () => BetterHighlightPotentialTargets.StatusText);
        yield return new("auto-prevent-game-exiting-from-lobby-errors", "Prevent Game Exiting From Lobby Errors", XAModsRestoreScope.Game, () => Configuration.AutoPreventGameExitingFromLobbyErrorsEnabled, SystemWindowMods.SetPreventLobbyExitEnabled, applied => Configuration.AutoPreventGameExitingFromLobbyErrorsEnabled = applied, () => SystemWindowMods.PreventLobbyExitStatusText);
        yield return new("auto-close-lobby-errors", "Close Lobby Errors", XAModsRestoreScope.Game, () => Configuration.AutoCloseLobbyErrorsEnabled, LobbyErrorAutoClose.SetEnabled, applied => Configuration.AutoCloseLobbyErrorsEnabled = applied, () => LobbyErrorAutoClose.StatusText);
        yield return new("bailout-esc-menu", "Bailout ESC Menu", XAModsRestoreScope.Ui, () => Configuration.BailoutEscMenuEnabled, EscMenuBailout.SetEnabled, applied => Configuration.BailoutEscMenuEnabled = applied, () => EscMenuBailout.StatusText);
        yield return new("auto-skip-dialogue", "Skip Dialogue", XAModsRestoreScope.Game, () => Configuration.AutoSkipDialogueEnabled, DialogueSkip.SetEnabled, applied => Configuration.AutoSkipDialogueEnabled = applied, () => DialogueSkip.StatusText);
        yield return new("lock-game-window-in-combat", "Lock Game Window In Combat", XAModsRestoreScope.Game, () => Configuration.LockGameWindowInCombatEnabled, AutoLockGameWindow.SetEnabled, applied => Configuration.LockGameWindowInCombatEnabled = applied, () => AutoLockGameWindow.StatusText);
        yield return new("notify-when-friend-is-near", "Notify When Friend Is Near", XAModsRestoreScope.Player, () => Configuration.NotifyWhenFriendIsNearEnabled, value =>
        {
            ApplyNotifyWhenFriendIsNearConfiguration(save: false);
            return NotifyWhenFriendIsNear.SetEnabled(value);
        }, applied => Configuration.NotifyWhenFriendIsNearEnabled = applied, () => NotifyWhenFriendIsNear.StatusText);
        yield return new("alert-when-typing-in-combat", "Alert When Typing In Combat", XAModsRestoreScope.Player, () => Configuration.AlertWhenTypingInCombatEnabled, value =>
        {
            ApplyAlertWhenTypingInCombatConfiguration(save: false);
            return AlertWhenTypingInCombat.SetEnabled(value);
        }, applied => Configuration.AlertWhenTypingInCombatEnabled = applied, () => AlertWhenTypingInCombat.StatusText);
        yield return new("better-cast-bar", "Better Cast Bar", XAModsRestoreScope.Ui, () => Configuration.BetterCastBarEnabled, value =>
        {
            ApplyBetterCastBarConfiguration(save: false);
            return BetterCastBar.SetEnabled(value);
        }, applied => Configuration.BetterCastBarEnabled = applied, () => BetterCastBar.StatusText);
        yield return new("better-duty-finder", "Better Duty Finder", XAModsRestoreScope.Ui, () => Configuration.BetterDutyFinderEnabled, BetterDutyFinder.SetEnabled, applied => Configuration.BetterDutyFinderEnabled = applied, () => BetterDutyFinder.StatusText);
        yield return new("display-actual-queue-position", "Display Actual Queue Position", XAModsRestoreScope.Game, () => Configuration.DisplayActualQueuePositionEnabled, QueuePositionDisplay.SetEnabled, applied => Configuration.DisplayActualQueuePositionEnabled = applied, () => QueuePositionDisplay.StatusText);
        yield return new("replace-unowned-mount-hotbars", "Replace Unowned Mount Hotbars", XAModsRestoreScope.Game, () => Configuration.ReplaceUnownedMountHotbarsEnabled, ReplaceUnownedMountHotbars.SetEnabled, applied => Configuration.ReplaceUnownedMountHotbarsEnabled = applied, () => ReplaceUnownedMountHotbars.StatusText);
        yield return new("target-command-fix", "Fix /target Command", XAModsRestoreScope.Game, () => Configuration.TargetCommandFixEnabled, TargetCommandFix.SetEnabled, applied => Configuration.TargetCommandFixEnabled = applied, () => TargetCommandFix.StatusText);
        yield return new("copy-item-name-for-all", "Copy Item Name For All", XAModsRestoreScope.Ui, () => Configuration.CopyItemNameForAllEnabled, CopyItemNameContextMenu.SetEnabled, applied => Configuration.CopyItemNameForAllEnabled = applied, () => CopyItemNameContextMenu.StatusText);
        yield return new("expanded-player-right-click-menu-search", "Expanded Player Right-Click Menu Search", XAModsRestoreScope.Ui, () => Configuration.ExpandedPlayerRightClickMenuSearchEnabled, PlayerSearchContextMenu.SetEnabled, applied => Configuration.ExpandedPlayerRightClickMenuSearchEnabled = applied, () => PlayerSearchContextMenu.StatusText);
        yield return new("live-anonymous-mode", "Anonymous Mode", XAModsRestoreScope.Ui, () => Configuration.LiveAnonymousModeEnabled, NameplatePrivacy.SetAnonymousModeEnabled, applied => Configuration.LiveAnonymousModeEnabled = applied, () => NameplatePrivacy.AnonymousModeStatusText);
        yield return new("better-inventory-mover", "Better Inventory Mover", XAModsRestoreScope.Player, () => Configuration.BetterInventoryMoverEnabled, BetterInventoryMover.SetEnabled, applied => Configuration.BetterInventoryMoverEnabled = applied, () => BetterInventoryMover.StatusText);
        yield return new("better-company-chest", "Better Company Chest", XAModsRestoreScope.Player, () => Configuration.BetterCompanyChestEnabled, BetterCompanyChest.SetEnabled, applied => Configuration.BetterCompanyChestEnabled = applied, () => BetterCompanyChest.StatusText);
        yield return new("auto-open-moogle-mail", "Auto Open Moogle Mail", XAModsRestoreScope.Player, () => Configuration.AutoOpenMoogleMailEnabled, AutoOpenMoogleMail.SetEnabled, applied => Configuration.AutoOpenMoogleMailEnabled = applied, () => AutoOpenMoogleMail.StatusText);
        yield return new("enable-item-icon-in-shops", "Enable Item Icon In Shops", XAModsRestoreScope.Ui, () => Configuration.EnableItemIconInShopsEnabled, EnableItemIconInShops.SetEnabled, applied => Configuration.EnableItemIconInShopsEnabled = applied, () => EnableItemIconInShops.StatusText);
        yield return new("field-operations-entry-command", "Field Operations Entry Command", XAModsRestoreScope.Eureka, () => Configuration.FieldEntryCommandEnabled, FieldEntryCommand.SetEnabled, applied => Configuration.FieldEntryCommandEnabled = applied, () => FieldEntryCommand.StatusText);

        yield return new("auto-ignore-minimum-window-size", "Ignore Minimum Window Size", XAModsRestoreScope.Graphic, () => Configuration.AutoIgnoreMinimumWindowSizeEnabled, SystemWindowMods.SetIgnoreMinimumWindowSizeEnabled, applied => Configuration.AutoIgnoreMinimumWindowSizeEnabled = applied, () => SystemWindowMods.IgnoreMinimumWindowSizeStatusText);
        yield return new("auto-hide-game-objects", "Hide Game Objects", XAModsRestoreScope.Graphic, () => Configuration.AutoHideGameObjectsEnabled, AutoHideGameObjects.SetEnabled, applied => Configuration.AutoHideGameObjectsEnabled = applied, () => AutoHideGameObjects.StatusText);
        yield return new("custom-resolutions", "Custom Resolutions", XAModsRestoreScope.Graphic, () => Configuration.CustomResolutionsEnabled, SystemWindowMods.SetCustomResolutionsEnabled, applied => Configuration.CustomResolutionsEnabled = applied, () => SystemWindowMods.CustomResolutionsStatusText);
        yield return new("disable-background-game-rendering", "Disable Background Rendering", XAModsRestoreScope.Graphic, () => Configuration.DisableBackgroundGameRenderingEnabled, SystemWindowMods.SetDisableBackgroundRenderingEnabled, applied => Configuration.DisableBackgroundGameRenderingEnabled = applied, () => SystemWindowMods.DisableBackgroundRenderingStatusText);
        yield return new("low-resolution", "Low Resolution", XAModsRestoreScope.Graphic, () => Configuration.LowResolutionEnabled, SystemWindowMods.SetLowResolutionEnabled, applied => Configuration.LowResolutionEnabled = applied, () => SystemWindowMods.LowResolutionStatusText);
        yield return new("special-rendering-modes", "Special Rendering Modes", XAModsRestoreScope.Graphic, () => Configuration.SpecialRenderModesEnabled, SetSpecialRenderModesEnabled, applied => Configuration.SpecialRenderModesEnabled = applied, () => Configuration.SpecialRenderModesEnabled ? SystemWindowMods.SpecialRenderModesStatusText : "Disabled");

        yield return new("anti-afk", "Anti-AFK", XAModsRestoreScope.Player, () => Configuration.AntiAfkEnabled, AntiAfk.SetEnabled, applied => Configuration.AntiAfkEnabled = applied, () => AntiAfk.StatusText);
        yield return new("auto-duty-commence", "Auto Duty Commence", XAModsRestoreScope.Player, () => Configuration.AutoDutyCommenceEnabled, AutoDutyCommence.SetEnabled, applied => Configuration.AutoDutyCommenceEnabled = applied, () => AutoDutyCommence.StatusText);
        yield return new("auto-expert-delivery", "Automate Expert Delivery", XAModsRestoreScope.Player, () => Configuration.AutoUnlockExpertDeliveryEnabled, AutoUnlockExpertDelivery.SetEnabled, applied => Configuration.AutoUnlockExpertDeliveryEnabled = applied, () => AutoUnlockExpertDelivery.StatusText);
        yield return new("auto-leave-duty", "Auto Leave Duty", XAModsRestoreScope.Player, () => Configuration.AutoLeaveDutyEnabled, AutoLeaveDuty.SetEnabled, applied => Configuration.AutoLeaveDutyEnabled = applied, () => AutoLeaveDuty.StatusText);
        yield return new("auto-merge", "Auto Merge", XAModsRestoreScope.Player, () => Configuration.AutoMergeEnabled, AutoMerge.SetEnabled, applied => Configuration.AutoMergeEnabled = applied, () => AutoMerge.StatusText);
        yield return new("quick-return", "Instant Return", XAModsRestoreScope.Illegal, () => Configuration.QuickReturnEnabled, QuickReturn.SetEnabled, applied => Configuration.QuickReturnEnabled = applied, () => QuickReturn.StatusText);
        yield return new("auto-refuse-trade-request", "Refuse Trade Request", XAModsRestoreScope.Player, () => Configuration.AutoRefuseTradeRequestEnabled, AutoRefuseTrade.SetEnabled, applied => Configuration.AutoRefuseTradeRequestEnabled = applied, () => AutoRefuseTrade.StatusText);
        yield return new("show-titles-as-playernames", "Show Titles As Playernames", XAModsRestoreScope.Player, () => Configuration.ShowTitlesAsPlayernamesEnabled, NameplatePrivacy.SetShowTitlesAsPlayernamesEnabled, applied => Configuration.ShowTitlesAsPlayernamesEnabled = applied, () => NameplatePrivacy.ShowTitlesAsPlayernamesStatusText);
        yield return new("show-blacklisted-playername-in-party", "Show Blacklisted Playername In Party", XAModsRestoreScope.Player, () => Configuration.ShowBlacklistedPlayernameInPartyEnabled, BlacklistedPartyName.SetEnabled, applied => Configuration.ShowBlacklistedPlayernameInPartyEnabled = applied, () => BlacklistedPartyName.StatusText);
        yield return new("show-traveler-world-names", "Show Traveler World Names", XAModsRestoreScope.Player, () => Configuration.ShowTravelerWorldNamesEnabled, value =>
        {
            NameplatePrivacy.ApplyShowTravelerWorldNamesConfiguration(
                Configuration.ShowTravelerWorldNamesDisableInDuties,
                Configuration.ShowTravelerWorldNamesAddSpacer);
            return NameplatePrivacy.SetShowTravelerWorldNamesEnabled(value);
        }, applied => Configuration.ShowTravelerWorldNamesEnabled = applied, () => NameplatePrivacy.ShowTravelerWorldNamesStatusText);
        yield return new("auto-reveal-undiscovered-areas", "Reveal Undiscovered Areas", XAModsRestoreScope.Player, () => Configuration.AutoRevealUndiscoveredAreasEnabled, SystemWindowMods.SetRevealUndiscoveredAreasEnabled, applied => Configuration.AutoRevealUndiscoveredAreasEnabled = applied, () => SystemWindowMods.RevealUndiscoveredAreasStatusText);
        yield return new("auto-clear-teleportation-lock", "Clear Teleportation Lock", XAModsRestoreScope.Player, () => Configuration.AutoClearTeleportationLockEnabled, TeleportLockClear.SetEnabled, applied => Configuration.AutoClearTeleportationLockEnabled = applied, () => TeleportLockClear.StatusText);
        yield return new("custom-sight-distance", "Custom Sight Distance", XAModsRestoreScope.Player, () => Configuration.CustomSightDistanceEnabled, SightDistance.SetEnabled, applied => Configuration.CustomSightDistanceEnabled = applied, () => SightDistance.StatusText);
        yield return new("doze-sit-anywhere", "Doze & Sit Anywhere", XAModsRestoreScope.Player, () => Configuration.DozeSitAnywhereEnabled, DozeSitAnywhere.SetEnabled, applied => Configuration.DozeSitAnywhereEnabled = applied, () => DozeSitAnywhere.StatusText);
        yield return new("infinite-sprint", "Infinite Sprint", XAModsRestoreScope.Player, () => Configuration.InfiniteSprintEnabled, PlayerMods.SetInfiniteSprintEnabled, applied => Configuration.InfiniteSprintEnabled = applied, () => PlayerMods.InfiniteSprintStatusText);
        yield return new("instant-logout", "Instant Logout", XAModsRestoreScope.Illegal, () => Configuration.InstantLogoutEnabled, InstantLogout.SetEnabled, applied => Configuration.InstantLogoutEnabled = applied, () => InstantLogout.StatusText);
        yield return new("item-commands", "Item Commands", XAModsRestoreScope.Player, () => Configuration.ItemCommandsEnabled, ItemCommands.SetEnabled, applied => Configuration.ItemCommandsEnabled = applied, () => ItemCommands.StatusText);
        yield return new("xa-peep", "XA Peep", XAModsRestoreScope.Player, () => Configuration.XAPeepEnabled, SetXAPeepEnabled, applied => Configuration.XAPeepEnabled = applied, () => XAPeep.StatusText);

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
        yield return new("arealmrecorded-all-zones", "ARealmRecorded All Zones", XAModsRestoreScope.Plugin, () => Configuration.ARealmRecordedAllZonesEnabled, value =>
        {
            ARealmRecordedIntegration.ApplyConfiguration(
                Configuration.ARealmRecordedAllZonesAllContentTypes,
                Configuration.ARealmRecordedAllZonesSelectedContentTypes);
            return ARealmRecordedIntegration.SetForceEnabled(value);
        }, applied => Configuration.ARealmRecordedAllZonesEnabled = applied, () => ARealmRecordedIntegration.StatusText);
        yield return new("teleport-helper", "Teleport Helper", XAModsRestoreScope.Plugin, () => Configuration.TeleportHelperEnabled, value =>
        {
            TeleportHelper.ApplyConfiguration(Configuration.TeleportHelperSelectYes);
            return TeleportHelper.SetEnabled(value);
        }, applied => Configuration.TeleportHelperEnabled = applied, () => TeleportHelper.StatusText);

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

    internal bool SetXAPeepEnabled(bool value)
    {
        var applied = XAPeep.SetEnabled(value);
        if (!applied)
            HideXAPeepWindowIfOpen();

        return applied;
    }

    private void HideXAPeepWindowIfOpen()
    {
        if (!XAPeepWindow.IsOpen && !Configuration.XAPeepWindowOpen)
            return;

        XAPeepWindow.IsOpen = false;
        if (!Configuration.XAPeepWindowOpen)
            return;

        Configuration.XAPeepWindowOpen = false;
        Configuration.Save();
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
            case "titlemovie":
            case "titlescreenmovie":
            case "disabletitlemovie":
                definition = new("titlemovie", "/xa titlemovie on|off", GetXAModDefinition("disable-title-screen-movie"));
                return true;
            case "displayids":
            case "autoids":
            case "ids":
                definition = new("displayids", "/xa displayids on|off", GetXAModDefinition("auto-display-ids"));
                return true;
            case "latency":
            case "networklatency":
                definition = new("latency", "/xa latency on|off", GetXAModDefinition("display-network-latency"));
                return true;
            case "timestampseconds":
            case "chattimestamps":
            case "timestampformat":
            case "customtimestamp":
                definition = new("timestampseconds", "/xa timestampseconds on|off", GetXAModDefinition("custom-timestamp-format"));
                return true;
            case "nouifade":
            case "nofade":
            case "uifade":
                definition = new("nouifade", "/xa nouifade on|off", GetXAModDefinition("no-ui-fade"));
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
            case "dalamudnotifs":
            case "dalamudnotifications":
            case "notificationssuck":
                definition = new("dalamudnotifs", "/xa dalamudnotifs on|off", GetXAModDefinition("dalamud-notifications-suck"));
                return true;
            case "highlighttargets":
            case "betterhighlight":
            case "targethighlight":
                definition = new("highlighttargets", "/xa highlighttargets on|off", GetXAModDefinition("better-highlight-potential-targets"));
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
            case "lockcombat":
            case "lockwindowcombat":
                definition = new("lockcombat", "/xa lockcombat on|off", GetXAModDefinition("lock-game-window-in-combat"));
                return true;
            case "friendnear":
            case "friendnotify":
                definition = new("friendnear", "/xa friendnear on|off", GetXAModDefinition("notify-when-friend-is-near"));
                return true;
            case "typingcombat":
            case "combattype":
            case "typingalert":
                definition = new("typingcombat", "/xa typingcombat on|off", GetXAModDefinition("alert-when-typing-in-combat"));
                return true;
            case "castbar":
                definition = new("castbar", "/xa castbar on|off", GetXAModDefinition("better-cast-bar"));
                return true;
            case "dutyfinder":
                definition = new("dutyfinder", "/xa dutyfinder on|off", GetXAModDefinition("better-duty-finder"));
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
            case "inventorymover":
            case "betterinventory":
                definition = new("inventorymover", "/xa inventorymover on|off", GetXAModDefinition("better-inventory-mover"));
                return true;
            case "companychest":
            case "bettercompanychest":
                definition = new("companychest", "/xa companychest on|off", GetXAModDefinition("better-company-chest"));
                return true;
            case "mooglemail":
                definition = new("mooglemail", "/xa mooglemail on|off", GetXAModDefinition("auto-open-moogle-mail"));
                return true;
            case "shopicons":
            case "itemicons":
                definition = new("shopicons", "/xa shopicons on|off", GetXAModDefinition("enable-item-icon-in-shops"));
                return true;
            case "fieldentrytoggle":
            case "fieldentrycommand":
                definition = new("fieldentrycommand", "/xa fieldentrycommand on|off", GetXAModDefinition("field-operations-entry-command"));
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
            case "dutycommence":
            case "autodutycommence":
                definition = new("dutycommence", "/xa dutycommence on|off", GetXAModDefinition("auto-duty-commence"));
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
            case "blacklistedparty":
            case "blacklistparty":
            case "blacklistedplayername":
                definition = new("blacklistedparty", "/xa blacklistedparty on|off", GetXAModDefinition("show-blacklisted-playername-in-party"));
                return true;
            case "titlesasplayernames":
            case "titleplayernames":
            case "playertitles":
            case "showtitles":
                definition = new("titlesasplayernames", "/xa titlesasplayernames on|off", GetXAModDefinition("show-titles-as-playernames"));
                return true;
            case "travelerworlds":
            case "travellerworlds":
            case "travelerworldnames":
            case "worldnames":
                definition = new("travelerworlds", "/xa travelerworlds on|off", GetXAModDefinition("show-traveler-world-names"));
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
            case "recordallzones":
            case "arrallzones":
            case "arealmrecorded":
                definition = new("recordallzones", "/xa recordallzones on|off", GetXAModDefinition("arealmrecorded-all-zones"));
                return true;
            case "disablelogs":
            case "logdisabler":
            case "muteplugin":
                definition = new("disablelogs", "/xa disablelogs on|off", GetXAModDefinition("dalamud-log-disabler"));
                return true;
            case "teleporthelper":
            case "tickethelper":
            case "ticket":
                definition = new("teleporthelper", "/xa teleporthelper on|off", GetXAModDefinition("teleport-helper"));
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

    private static bool IsFieldEntrySubcommand(string subcommand)
    {
        return subcommand.Equals("fe", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("fieldentry", StringComparison.OrdinalIgnoreCase)
            || subcommand.Equals("fieldoperations", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetXAModsRestoreScopeLabel(XAModsRestoreScope scope)
    {
        return scope switch
        {
            XAModsRestoreScope.Game => "Game Mods",
            XAModsRestoreScope.Ui => "UI Mods",
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
        // Check the cheap local config flag first: this runs on every Framework tick, and
        // IsAutoRetainerMultiModeEnabled() is a cross-plugin IPC call that throws/catches when
        // AutoRetainer is absent. Short-circuiting on the config bool avoids a per-tick exception.
        if (!Configuration.SpecialRenderHideChatEnabled || !IsAutoRetainerMultiModeEnabled())
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
            .Where(item => item.SelectorKind == XagmanItemSelectorKind.ExactItem
                ? item.ItemId > 0 && !string.IsNullOrWhiteSpace(item.ItemName)
                : item.SelectorKind is XagmanItemSelectorKind.GreenItemGcSeals
                    or XagmanItemSelectorKind.GreenItemFcCreditsRankProgress)
            .GroupBy(item => new
            {
                item.SelectorKind,
                ItemId = item.SelectorKind == XagmanItemSelectorKind.ExactItem ? item.ItemId : 0u,
                IsHq = item.SelectorKind == XagmanItemSelectorKind.ExactItem && item.IsHq,
                item.Applicability,
            })
            .Select(group => new XagmanItemEntry
            {
                SelectorKind = group.Key.SelectorKind,
                ItemId = group.Key.ItemId,
                ItemName = group.Key.SelectorKind switch
                {
                    XagmanItemSelectorKind.GreenItemGcSeals => "Green Item GC Seals",
                    XagmanItemSelectorKind.GreenItemFcCreditsRankProgress => "Green Item FC Credits / Rank Progress",
                    _ => group.First().ItemName,
                },
                IsHq = group.Key.IsHq,
                Mode = group.Key.SelectorKind == XagmanItemSelectorKind.ExactItem
                    ? group.First().Mode
                    : XagmanItemMode.TopUp,
                Applicability = group.Key.Applicability,
                Quantity = Math.Max(0, group.First().Quantity),
            })
            .OrderBy(item => item.SelectorKind)
            .ThenBy(item => item.ItemId)
            .ThenBy(item => item.Applicability)
            .ToList();
    }
}

internal static class BuildInfo
{
    public const string Version = "0.0.0.42";
}
