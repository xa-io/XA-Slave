using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
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
    [PluginService] public static IPartyList PartyList { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] public static IToastGui ToastGui { get; private set; } = null!;

    private const string CommandName = "/xa";

    public static Plugin Instance { get; private set; } = null!;
    public string InstanceId { get; }
    public int ProcessId { get; }

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("XASlave");
    private SlaveWindow SlaveWindow { get; init; }

    // Services
    public IpcClient IpcClient { get; init; }
    public IpcProvider IpcProvider { get; init; }
    public AutoCollectionService AutoCollector { get; init; }
    public TaskRunner TaskRunner { get; init; }
    public AutoRetainerConfigReader ArConfigReader { get; init; }
    public ExternalTaskLoader ExternalTaskLoader { get; init; }
    public WindowRenamerService WindowRenamer { get; init; }
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
        var normalizedXagmanHubPort = Configuration.XagmanHubPort == 47786
            ? XagmanPeerService.DefaultHubPort
            : XagmanPeerService.NormalizePort(Configuration.XagmanHubPort);
        var xagmanHubPortChanged = Configuration.XagmanHubPort != normalizedXagmanHubPort;
        Configuration.XagmanHubPort = normalizedXagmanHubPort;
        if (livePullsWereEnabled || autoGlamDefaultsInitialized || xagmanPreflightChanged || xagmanItemsChanged || xagmanItemsMigrationChanged || xagmanHubPortChanged)
            Configuration.Save();

        IpcClient = new IpcClient(PluginInterface, Log);
        SlaveDatabase = new SlaveDatabaseService(PluginInterface, Log);
        AutoCollector = new AutoCollectionService(this, Condition, Framework, ObjectTable, Log);
        TaskRunner = new TaskRunner(Condition, Framework, Log, DtrBar, ToastGui);
        ArConfigReader = new AutoRetainerConfigReader(PluginInterface, Log);
        IpcProvider = new IpcProvider(PluginInterface, this, Log);
        ExternalTaskLoader = new ExternalTaskLoader(this, PluginInterface, Log);
        WindowRenamer = new WindowRenamerService(Log);
        ArPostProcessor = new ArPostProcessService(this, ClientState, Condition, Framework, ObjectTable, Log, DtrBar);
        XagmanPeers = new XagmanPeerService(Log, InstanceId, Configuration.XagmanHubPort, _ => { });
        XagmanPeers.Start();

        SlaveWindow = new SlaveWindow(this);
        WindowSystem.AddWindow(SlaveWindow);

        if (Configuration.OpenPluginOnLoad)
            SlaveWindow.IsOpen = true;

        // Apply window rename on plugin load if enabled
        if (Configuration.WindowRenamerEnabled)
            WindowRenamer.ApplyFromConfig(Configuration);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the XA Slave window"
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

        // Cancel any running task on logout — BUT skip if relogger suppresses it
        // (logout is expected during /ays relog character switches)
        if (TaskRunner.IsRunning && !TaskRunner.SuppressLogoutCancel)
        {
            Log.Information("[XASlave] Character logged out — cancelling running task.");
            TaskRunner.AddLog("EVENT: Character logged out — cancelling running task.");
            TaskRunner.Cancel();
        }
        else if (TaskRunner.IsRunning)
        {
            Log.Information("[XASlave] Character logged out — relogger active, not cancelling.");
            TaskRunner.AddLog("EVENT: Character logged out — relogger active, not cancelling.");
        }

        Log.Information("[XASlave] Character logged out — sending final save to XA Database.");
        if (TaskRunner.IsRunning)
            TaskRunner.AddLog("EVENT: Character logged out — sending final save to XA Database.");
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

#if XA_SLAVE_TESTING_BUILD
        // /xa run <task> — test IPC RunTask locally
        if (trimmed.StartsWith("run ", StringComparison.OrdinalIgnoreCase))
        {
            var taskName = trimmed.Substring(4).Trim();
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

        // Unknown subcommand — toggle window
        SlaveWindow.Toggle();
    }

    public void ToggleMainUi() => SlaveWindow.Toggle();

    public int ApplyXagmanHubPort(int value)
    {
        var normalized = XagmanPeerService.NormalizePort(value);
        if (Configuration.XagmanHubPort == normalized && XagmanPeers.HubPort == normalized)
            return normalized;

        Configuration.XagmanHubPort = normalized;
        Configuration.Save();
        RestartXagmanPeerService();
        return normalized;
    }

    private void RestartXagmanPeerService()
    {
        XagmanPeers.Dispose();
        XagmanPeers = new XagmanPeerService(Log, InstanceId, Configuration.XagmanHubPort, _ => { });
        XagmanPeers.Start();
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
    public const string Version = "0.0.0.15";
}
