using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
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

    public static Plugin Instance { get; private set; } = null!;
    public string InstanceId { get; }
    public int ProcessId { get; }

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("XASlave");
    private SlaveWindow SlaveWindow { get; init; }

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
    public PlayerModsService PlayerMods { get; init; }
    public DozeSitAnywhereService DozeSitAnywhere { get; init; }
    public InstantLogoutService InstantLogout { get; init; }
    public TeleportLockClearService TeleportLockClear { get; init; }
    public PeepingTomIntegrationService PeepingTomIntegration { get; init; }
    public ArPostProcessService ArPostProcessor { get; init; }
    public SlaveDatabaseService SlaveDatabase { get; init; }
    public XagmanPeerService XagmanPeers { get; private set; }

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
        var xagmanPreflightChanged = !Configuration.XagmanUsePreflightOnFirstCharacter;
        Configuration.XagmanUsePreflightOnFirstCharacter = true;
        var xagmanItemsChanged = false;
        var xagmanItemsMigrationChanged = false;
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
        if (livePullsWereEnabled || protectedRiskyToonModsReset || autoGlamDefaultsInitialized || xagmanPreflightChanged || xagmanItemsChanged || xagmanItemsMigrationChanged || xagmanHubAddressChanged || xagmanHubPortChanged)
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
        BuddyFeedCutsceneSkip = new BuddyFeedCutsceneSkipService(SigScanner, GameInterop, Log);
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
        AutoRefuseTrade = new AutoRefuseTradeService(SigScanner, GameInterop, Log);
        PlayerMods = new PlayerModsService(Framework, Condition, SigScanner, GameInterop, Log);
        DozeSitAnywhere = new DozeSitAnywhereService(ClientState, ObjectTable, SigScanner, GameInterop, Log);
        InstantLogout = new InstantLogoutService(ClientState, Framework, SigScanner, Log, LobbyErrorAutoClose);
        TeleportLockClear = new TeleportLockClearService(ChatGui, Log);
        PeepingTomIntegration = new PeepingTomIntegrationService(PluginInterface, Framework, ClientState, Log);
        ArPostProcessor = new ArPostProcessService(this, ClientState, Condition, Framework, ObjectTable, Log, DtrBar);
        XagmanPeers = new XagmanPeerService(Log, InstanceId, Configuration.XagmanHubAddress, Configuration.XagmanHubPort, _ => { });
        if (Configuration.XagmanPeerConnectionsEnabled)
            XagmanPeers.Start();

        SystemWindowMods.SetDisableBackgroundRenderingOnlyWhenMinimized(Configuration.DisableBackgroundGameRenderingOnlyWhenMinimized);
        SystemWindowMods.SetDisableBackgroundRenderingDisableWhenArMultiIsOn(Configuration.DisableBackgroundGameRenderingDisableWhenArMultiIsOn);
        AutoHideGameObjects.ApplyConfiguration(
            Configuration.AutoHideGameObjectsHidePlayer,
            Configuration.AutoHideGameObjectsHideUnimportantEnpc,
            Configuration.AutoHideGameObjectsHidePet,
            Configuration.AutoHideGameObjectsHideChocobo,
            Configuration.AutoHideGameObjectsDisableInDuties,
            Configuration.AutoHideGameObjectsDisableInIslandSanctuary,
            Configuration.AutoHideGameObjectsUseOccultCrescentRules);
        SightDistance.ApplyConfiguration(
            Configuration.CustomSightDistanceMaxDistance,
            Configuration.CustomSightDistanceMinDistance,
            Configuration.CustomSightDistanceMaxRotation,
            Configuration.CustomSightDistanceMinRotation,
            Configuration.CustomSightDistanceMaxFoV,
            Configuration.CustomSightDistanceMinFoV,
            Configuration.CustomSightDistanceFoV,
            Configuration.CustomSightDistanceIgnoreCollision);
        PlayerSearchContextMenu.ApplyConfiguration(
            Configuration.ExpandedPlayerRightClickMenuSearchFflogsEnabled,
            Configuration.ExpandedPlayerRightClickMenuSearchLodestoneEnabled,
            Configuration.ExpandedPlayerRightClickMenuSearchLalachievementsEnabled,
            Configuration.ExpandedPlayerRightClickMenuSearchOpenAllEnabled);
        AutoUnlockExpertDelivery.ApplyConfiguration(
            Configuration.AutoUnlockExpertDeliveryAutoSwitchWhenOpen,
            Configuration.AutoUnlockExpertDeliveryDefaultPage,
            Configuration.AutoUnlockExpertDeliverySkipHq,
            Configuration.AutoUnlockExpertDeliverySkipMateria,
            Configuration.AutoUnlockExpertDeliveryIgnoreSealCap);
        AutoRefuseTrade.ApplyConfiguration(
            Configuration.AutoRefuseTradeShowNotification,
            Configuration.AutoRefuseTradeSendEcho,
            Configuration.AutoRefuseTradeExtraCommands);
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
        PlayerMods.ApplyInfiniteSprintConfiguration(Configuration.InfiniteSprintDelaySeconds);
        SystemWindowMods.ApplyLowResolutionConfiguration(Configuration.LowResolutionScale);
        PeepingTomIntegration.SetPreserveHistoryOnLogoutEnabled(Configuration.ForcePeepingTomPreserveHistoryOnLogoutEnabled);

        if (Configuration.AutoAllowMultipleGameInstancesEnabled && !SystemWindowMods.SetAllowMultipleGameInstancesEnabled(true))
        {
            Configuration.AutoAllowMultipleGameInstancesEnabled = false;
            Configuration.Save();
        }

        if (Configuration.AutoCancelLoginCooldownEnabled && !SystemWindowMods.SetCancelLoginCooldownEnabled(true))
        {
            Configuration.AutoCancelLoginCooldownEnabled = false;
            Configuration.Save();
        }

        if (Configuration.AutoDisplayMsqProgressEnabled && !MsqProgressDisplay.SetEnabled(true))
        {
            Configuration.AutoDisplayMsqProgressEnabled = false;
            Configuration.Save();
        }

        if (Configuration.CopyItemNameForAllEnabled && !CopyItemNameContextMenu.SetEnabled(true))
        {
            Configuration.CopyItemNameForAllEnabled = false;
            Configuration.Save();
        }

        if (Configuration.AutoSkipCutscenesEnabled && !AutoSkipCutscenes.SetEnabled(true))
        {
            Configuration.AutoSkipCutscenesEnabled = false;
            Configuration.Save();
        }

        if (Configuration.AutoSkipCutscenesFeedingChocoboEnabled && !BuddyFeedCutsceneSkip.SetEnabled(true))
        {
            Configuration.AutoSkipCutscenesFeedingChocoboEnabled = false;
            Configuration.Save();
        }

        if (Configuration.AutoIgnoreMinimumWindowSizeEnabled && !SystemWindowMods.SetIgnoreMinimumWindowSizeEnabled(true))
        {
            Configuration.AutoIgnoreMinimumWindowSizeEnabled = false;
            Configuration.Save();
        }

        if (Configuration.AutoHideUnnecessaryPopupsEnabled && !PopupCleaner.SetEnabled(true))
        {
            Configuration.AutoHideUnnecessaryPopupsEnabled = false;
            Configuration.Save();
        }

        if (Configuration.AutoPreventGameExitingFromLobbyErrorsEnabled && !SystemWindowMods.SetPreventLobbyExitEnabled(true))
        {
            Configuration.AutoPreventGameExitingFromLobbyErrorsEnabled = false;
            Configuration.Save();
        }

        if (Configuration.AutoCloseLobbyErrorsEnabled && !LobbyErrorAutoClose.SetEnabled(true))
        {
            Configuration.AutoCloseLobbyErrorsEnabled = false;
            Configuration.Save();
        }

        if (Configuration.DisplayActualQueuePositionEnabled && !QueuePositionDisplay.SetEnabled(true))
        {
            Configuration.DisplayActualQueuePositionEnabled = false;
            Configuration.Save();
        }

        if (Configuration.AutoHideGameObjectsEnabled && !AutoHideGameObjects.SetEnabled(true))
        {
            Configuration.AutoHideGameObjectsEnabled = false;
            Configuration.Save();
        }

        if (Configuration.AutoSkipDialogueEnabled && !DialogueSkip.SetEnabled(true))
        {
            Configuration.AutoSkipDialogueEnabled = false;
            Configuration.Save();
        }

        if (Configuration.CustomResolutionsEnabled && !SystemWindowMods.SetCustomResolutionsEnabled(true))
        {
            Configuration.CustomResolutionsEnabled = false;
            Configuration.Save();
        }

        if (Configuration.DisableBackgroundGameRenderingEnabled && !SystemWindowMods.SetDisableBackgroundRenderingEnabled(true))
        {
            Configuration.DisableBackgroundGameRenderingEnabled = false;
            Configuration.Save();
        }

        if (Configuration.LowResolutionEnabled && !SystemWindowMods.SetLowResolutionEnabled(true))
        {
            Configuration.LowResolutionEnabled = false;
            Configuration.Save();
        }

        if (Configuration.CustomSightDistanceEnabled && !SightDistance.SetEnabled(true))
        {
            Configuration.CustomSightDistanceEnabled = false;
            Configuration.Save();
        }

        if (Configuration.ExpandedPlayerRightClickMenuSearchEnabled && !PlayerSearchContextMenu.SetEnabled(true))
        {
            Configuration.ExpandedPlayerRightClickMenuSearchEnabled = false;
            Configuration.Save();
        }

        if (Configuration.LiveAnonymousModeEnabled && !NameplatePrivacy.SetAnonymousModeEnabled(true))
        {
            Configuration.LiveAnonymousModeEnabled = false;
            Configuration.Save();
        }

        if (Configuration.AutoUnlockExpertDeliveryEnabled && !AutoUnlockExpertDelivery.SetEnabled(true))
        {
            Configuration.AutoUnlockExpertDeliveryEnabled = false;
            Configuration.Save();
        }

        if (Configuration.UnlockExpertDeliveryEnabled && !ExpertDeliveryUnlock.SetEnabled(true))
        {
            Configuration.UnlockExpertDeliveryEnabled = false;
            Configuration.Save();
        }

        if (Configuration.AutoRefuseTradeRequestEnabled && !AutoRefuseTrade.SetEnabled(true))
        {
            Configuration.AutoRefuseTradeRequestEnabled = false;
            Configuration.Save();
        }

        if (Configuration.AutoRevealUndiscoveredAreasEnabled && !SystemWindowMods.SetRevealUndiscoveredAreasEnabled(true))
        {
            Configuration.AutoRevealUndiscoveredAreasEnabled = false;
            Configuration.Save();
        }

        if (Configuration.AutoClearTeleportationLockEnabled && !TeleportLockClear.SetEnabled(true))
        {
            Configuration.AutoClearTeleportationLockEnabled = false;
            Configuration.Save();
        }

        if (Configuration.DozeSitAnywhereEnabled && !DozeSitAnywhere.SetEnabled(true))
        {
            Configuration.DozeSitAnywhereEnabled = false;
            Configuration.Save();
        }

        if (Configuration.InfiniteSprintEnabled && !PlayerMods.SetInfiniteSprintEnabled(true))
        {
            Configuration.InfiniteSprintEnabled = false;
            Configuration.Save();
        }

        if (Configuration.InstantLogoutEnabled && !InstantLogout.SetEnabled(true))
        {
            Configuration.InstantLogoutEnabled = false;
            Configuration.Save();
        }

        if (Configuration.MoveableAfterDeathEnabled && !PlayerMods.SetMoveableAfterDeathEnabled(true))
        {
            Configuration.MoveableAfterDeathEnabled = false;
            Configuration.Save();
        }

        if (Configuration.ForcePeepingTomEnabled && !PeepingTomIntegration.SetForceEnabled(true))
        {
            Configuration.ForcePeepingTomEnabled = false;
            Configuration.Save();
        }

        SlaveWindow = new SlaveWindow(this);
        WindowSystem.AddWindow(SlaveWindow);

        if (Configuration.OpenPluginOnLoad)
            SlaveWindow.IsOpen = true;

        // Apply window rename on plugin load if enabled
        if (Configuration.WindowRenamerEnabled)
            WindowRenamer.ApplyFromConfig(Configuration);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open XA Slave. Subcommands include xamods, db, preset save/load/list, XA Mods toggle on/off commands, res, lowres, sprintdelay, and the section restore commands."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;

        Log.Information("[XASlave] Plugin loaded successfully.");
    }

    public void Dispose()
    {
        AutoSkipCutscenes.Dispose();
        BuddyFeedCutsceneSkip.Dispose();
        PopupCleaner.Dispose();
        SystemWindowMods.Dispose();
        LobbyErrorAutoClose.Dispose();
        QueuePositionDisplay.Dispose();
        MsqProgressDisplay.Dispose();
        AutoHideGameObjects.Dispose();
        DialogueSkip.Dispose();
        CopyItemNameContextMenu.Dispose();
        SightDistance.Dispose();
        PlayerSearchContextMenu.Dispose();
        NameplatePrivacy.Dispose();
        AutoUnlockExpertDelivery.Dispose();
        ExpertDeliveryUnlock.Dispose();
        AutoRefuseTrade.Dispose();
        PlayerMods.Dispose();
        DozeSitAnywhere.Dispose();
        InstantLogout.Dispose();
        TeleportLockClear.Dispose();
        PeepingTomIntegration.Dispose();
        ArPostProcessor.Dispose();
        WindowRenamer.Dispose();
        IpcProvider.Dispose();
        ExternalTaskLoader.Dispose();

        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        SlaveWindow.Dispose();
        TaskRunner.Dispose();
        AutoCollector.Dispose();
        XagmanPeers.Dispose();

        CommandManager.RemoveHandler(CommandName);
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

        // Cancel any running task on logout â€” BUT skip if relogger suppresses it
        // (logout is expected during /ays relog character switches)
        if (TaskRunner.IsRunning && !TaskRunner.SuppressLogoutCancel)
        {
            Log.Information("[XASlave] Character logged out â€” cancelling running task.");
            TaskRunner.AddLog("EVENT: Character logged out â€” cancelling running task.");
            TaskRunner.Cancel();
        }
        else if (TaskRunner.IsRunning)
        {
            Log.Information("[XASlave] Character logged out â€” relogger active, not cancelling.");
            TaskRunner.AddLog("EVENT: Character logged out â€” relogger active, not cancelling.");
        }

        Log.Information("[XASlave] Character logged out â€” sending final save to XA Database.");
        if (TaskRunner.IsRunning)
            TaskRunner.AddLog("EVENT: Character logged out â€” sending final save to XA Database.");
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
        // /xa run <task> â€” test IPC RunTask locally
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

        if (subcommand.Equals("xamods", StringComparison.OrdinalIgnoreCase))
        {
            SlaveWindow.OpenXAModsTask();
            return;
        }

        if (subcommand.Equals("commands", StringComparison.OrdinalIgnoreCase))
        {
            SlaveWindow.OpenCommandsReferenceTask();
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
            InstantLogout.RequestLogout();
            return;
        }

        if (subcommand.Equals("killgame", StringComparison.OrdinalIgnoreCase))
        {
            InstantLogout.RequestKillGame();
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

        // Unknown subcommand â€” toggle window
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
            message = "Usage: pass the same text you would enter after /xa, for example `commands`, `xamods`, `db clear`, `logout`, `killgame`, `sprint on`, or `res 500x345`.";
            return false;
        }

        var firstSpaceIndex = trimmed.IndexOf(' ');
        var subcommand = firstSpaceIndex >= 0 ? trimmed[..firstSpaceIndex].Trim() : trimmed;
        var subcommandArgs = firstSpaceIndex >= 0 ? trimmed[(firstSpaceIndex + 1)..].Trim() : string.Empty;

        if (subcommand.Equals("xamods", StringComparison.OrdinalIgnoreCase))
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
            var success = InstantLogout.RequestLogout();
            message = success ? "Triggered XA hard logout." : InstantLogout.StatusText;
            return success;
        }

        if (subcommand.Equals("killgame", StringComparison.OrdinalIgnoreCase))
        {
            var success = InstantLogout.RequestKillGame();
            message = success ? "Triggered XA kill-game flow." : InstantLogout.StatusText;
            return success;
        }

        if (subcommand.Equals("preset", StringComparison.OrdinalIgnoreCase))
            return TryHandlePresetCommandFromIpc(subcommandArgs, out message);

        if (subcommand.Equals("lowres", StringComparison.OrdinalIgnoreCase))
            return TryApplyLowResolutionCommand(subcommandArgs, out message);

        if (subcommand.Equals("res", StringComparison.OrdinalIgnoreCase))
            return TryHandleResolutionCommand(subcommandArgs, out message);

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

        Configuration.LowResolutionScale = scale;
        SystemWindowMods.ApplyLowResolutionConfiguration(scale);
        var applied = SystemWindowMods.SetLowResolutionEnabled(true);
        Configuration.LowResolutionEnabled = applied;
        Configuration.Save();

        message = applied
            ? $"Low Resolution enabled at {scale:0.00}."
            : SystemWindowMods.LowResolutionStatusText;
        return applied;
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
        var savedList = Configuration.ToonModsSavedLists.FirstOrDefault(entry => entry.Name.Equals(trimmedName, StringComparison.OrdinalIgnoreCase));
        if (savedList == null)
        {
            Configuration.ToonModsSavedLists.Add(new ToonModSavedList
            {
                Name = trimmedName,
                ModKeys = modKeys,
            });
        }
        else
        {
            savedList.Name = trimmedName;
            savedList.ModKeys = modKeys;
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

        return ApplyXAModsPreset(savedList.Name, savedList.ModKeys, out message);
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

    private bool ApplyXAModsPreset(string title, IEnumerable<string> modKeys, out string message)
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

        Configuration.Save();
        message = unknownCount > 0 || unavailableCount > 0
            ? $"Loaded XA Mods preset '{title}' ({appliedCount} applied, {unavailableCount} unavailable, {unknownCount} unknown)."
            : $"Loaded XA Mods preset '{title}' ({appliedCount} mod(s)).";
        return unknownCount == 0 && unavailableCount == 0;
    }

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

    public void DisableAllXAMods()
    {
        DisableXAModDefinitions(GetAllXAModDefinitions());
        Configuration.Save();
    }

    public bool LoadModListPreset(string name, out string message)
        => LoadSavedXAModsPreset(name, out message);

    public IReadOnlyList<string> GetSavedModListNames()
        => Configuration.ToonModsSavedLists.Select(entry => entry.Name).ToList();

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
        yield return new("auto-skip-dialogue", "Skip Dialogue", XAModsRestoreScope.Game, () => Configuration.AutoSkipDialogueEnabled, DialogueSkip.SetEnabled, applied => Configuration.AutoSkipDialogueEnabled = applied, () => DialogueSkip.StatusText);
        yield return new("display-actual-queue-position", "Display Actual Queue Position", XAModsRestoreScope.Game, () => Configuration.DisplayActualQueuePositionEnabled, QueuePositionDisplay.SetEnabled, applied => Configuration.DisplayActualQueuePositionEnabled = applied, () => QueuePositionDisplay.StatusText);
        yield return new("copy-item-name-for-all", "Copy Item Name For All", XAModsRestoreScope.Game, () => Configuration.CopyItemNameForAllEnabled, CopyItemNameContextMenu.SetEnabled, applied => Configuration.CopyItemNameForAllEnabled = applied, () => CopyItemNameContextMenu.StatusText);
        yield return new("expanded-player-right-click-menu-search", "Expanded Player Right-Click Menu Search", XAModsRestoreScope.Game, () => Configuration.ExpandedPlayerRightClickMenuSearchEnabled, PlayerSearchContextMenu.SetEnabled, applied => Configuration.ExpandedPlayerRightClickMenuSearchEnabled = applied, () => PlayerSearchContextMenu.StatusText);
        yield return new("live-anonymous-mode", "Live Anonymous Mode", XAModsRestoreScope.Game, () => Configuration.LiveAnonymousModeEnabled, NameplatePrivacy.SetAnonymousModeEnabled, applied => Configuration.LiveAnonymousModeEnabled = applied, () => NameplatePrivacy.AnonymousModeStatusText);

        yield return new("auto-ignore-minimum-window-size", "Ignore Minimum Window Size", XAModsRestoreScope.Graphic, () => Configuration.AutoIgnoreMinimumWindowSizeEnabled, SystemWindowMods.SetIgnoreMinimumWindowSizeEnabled, applied => Configuration.AutoIgnoreMinimumWindowSizeEnabled = applied, () => SystemWindowMods.IgnoreMinimumWindowSizeStatusText);
        yield return new("auto-hide-game-objects", "Hide Game Objects", XAModsRestoreScope.Graphic, () => Configuration.AutoHideGameObjectsEnabled, AutoHideGameObjects.SetEnabled, applied => Configuration.AutoHideGameObjectsEnabled = applied, () => AutoHideGameObjects.StatusText);
        yield return new("custom-resolutions", "Custom Resolutions", XAModsRestoreScope.Graphic, () => Configuration.CustomResolutionsEnabled, SystemWindowMods.SetCustomResolutionsEnabled, applied => Configuration.CustomResolutionsEnabled = applied, () => SystemWindowMods.CustomResolutionsStatusText);
        yield return new("disable-background-game-rendering", "Disable Background Rendering", XAModsRestoreScope.Graphic, () => Configuration.DisableBackgroundGameRenderingEnabled, SystemWindowMods.SetDisableBackgroundRenderingEnabled, applied => Configuration.DisableBackgroundGameRenderingEnabled = applied, () => SystemWindowMods.DisableBackgroundRenderingStatusText);
        yield return new("low-resolution", "Low Resolution", XAModsRestoreScope.Graphic, () => Configuration.LowResolutionEnabled, SystemWindowMods.SetLowResolutionEnabled, applied => Configuration.LowResolutionEnabled = applied, () => SystemWindowMods.LowResolutionStatusText);
        yield return new("special-rendering-modes", "Special Rendering Modes", XAModsRestoreScope.Graphic, () => Configuration.SpecialRenderModesEnabled, SetSpecialRenderModesEnabled, applied => Configuration.SpecialRenderModesEnabled = applied, () => Configuration.SpecialRenderModesEnabled ? SystemWindowMods.SpecialRenderModesStatusText : "Disabled");

        yield return new("auto-expert-delivery", "Automate Expert Delivery", XAModsRestoreScope.Player, () => Configuration.AutoUnlockExpertDeliveryEnabled, AutoUnlockExpertDelivery.SetEnabled, applied => Configuration.AutoUnlockExpertDeliveryEnabled = applied, () => AutoUnlockExpertDelivery.StatusText);
        yield return new("auto-refuse-trade-request", "Refuse Trade Request", XAModsRestoreScope.Player, () => Configuration.AutoRefuseTradeRequestEnabled, AutoRefuseTrade.SetEnabled, applied => Configuration.AutoRefuseTradeRequestEnabled = applied, () => AutoRefuseTrade.StatusText);
        yield return new("auto-reveal-undiscovered-areas", "Reveal Undiscovered Areas", XAModsRestoreScope.Player, () => Configuration.AutoRevealUndiscoveredAreasEnabled, SystemWindowMods.SetRevealUndiscoveredAreasEnabled, applied => Configuration.AutoRevealUndiscoveredAreasEnabled = applied, () => SystemWindowMods.RevealUndiscoveredAreasStatusText);
        yield return new("auto-clear-teleportation-lock", "Clear Teleportation Lock", XAModsRestoreScope.Player, () => Configuration.AutoClearTeleportationLockEnabled, TeleportLockClear.SetEnabled, applied => Configuration.AutoClearTeleportationLockEnabled = applied, () => TeleportLockClear.StatusText);
        yield return new("custom-sight-distance", "Custom Sight Distance", XAModsRestoreScope.Player, () => Configuration.CustomSightDistanceEnabled, SightDistance.SetEnabled, applied => Configuration.CustomSightDistanceEnabled = applied, () => SightDistance.StatusText);
        yield return new("doze-sit-anywhere", "Doze & Sit Anywhere", XAModsRestoreScope.Player, () => Configuration.DozeSitAnywhereEnabled, DozeSitAnywhere.SetEnabled, applied => Configuration.DozeSitAnywhereEnabled = applied, () => DozeSitAnywhere.StatusText);
        yield return new("infinite-sprint", "Infinite Sprint", XAModsRestoreScope.Player, () => Configuration.InfiniteSprintEnabled, PlayerMods.SetInfiniteSprintEnabled, applied => Configuration.InfiniteSprintEnabled = applied, () => PlayerMods.InfiniteSprintStatusText);
        yield return new("instant-logout", "Instant Logout", XAModsRestoreScope.Player, () => Configuration.InstantLogoutEnabled, InstantLogout.SetEnabled, applied => Configuration.InstantLogoutEnabled = applied, () => InstantLogout.StatusText);

        yield return new("force-peepingtom", "Force PeepingTom", XAModsRestoreScope.Plugin, () => Configuration.ForcePeepingTomEnabled, PeepingTomIntegration.SetForceEnabled, applied => Configuration.ForcePeepingTomEnabled = applied, () => PeepingTomIntegration.StatusText);

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
            case "expertdelivery":
                definition = new("expertdelivery", "/xa expertdelivery on|off", GetXAModDefinition("auto-expert-delivery"));
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
            case "peepingtom":
                definition = new("peepingtom", "/xa peepingtom on|off", GetXAModDefinition("force-peepingtom"));
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

    private static string GetXAModsRestoreScopeLabel(XAModsRestoreScope scope)
    {
        return scope switch
        {
            XAModsRestoreScope.Game => "Game Mods",
            XAModsRestoreScope.Graphic => "Graphic Mods",
            XAModsRestoreScope.Player => "Player Mods",
            XAModsRestoreScope.Plugin => "Plugin Mods",
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

    private void RestoreSpecialRenderModes()
    {
        var allUiFlags = UIModule.UiFlags.ActionBars
            | UIModule.UiFlags.Chat
            | UIModule.UiFlags.Hud
            | UIModule.UiFlags.Nameplates
            | UIModule.UiFlags.TargetInfo
            | UIModule.UiFlags.Shortcuts;

        SystemWindowMods.SetSpecialRenderWorldHidden(false, GetSpecialRenderBackgroundColor());
        SystemWindowMods.SetSpecialRenderUiVisibility(allUiFlags, true);
    }

    private bool SetSpecialRenderModesEnabled(bool value)
    {
        if (!value)
            RestoreSpecialRenderModes();

        return value;
    }

    public void ToggleMainUi() => SlaveWindow.Toggle();

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
    public const string Version = "0.0.0.20";
}
