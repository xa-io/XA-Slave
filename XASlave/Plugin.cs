using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
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

    public Plugin()
    {
        Instance = this;

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.InitializeFloorderDefaults();

        IpcClient = new IpcClient(PluginInterface, Log);
        AutoCollector = new AutoCollectionService(Condition, Framework, ObjectTable, Log);
        TaskRunner = new TaskRunner(Condition, Framework, Log, DtrBar, ToastGui);
        ArConfigReader = new AutoRetainerConfigReader(PluginInterface, Log);
        IpcProvider = new IpcProvider(PluginInterface, this, Log);
        ExternalTaskLoader = new ExternalTaskLoader(this, PluginInterface, Log);

        SlaveWindow = new SlaveWindow(this);
        WindowSystem.AddWindow(SlaveWindow);

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
        IpcProvider.Dispose();
        ExternalTaskLoader.Dispose();
        TaskRunner.Dispose();
        AutoCollector.Dispose();

        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        SlaveWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnLogin()
    {
        Log.Information("[XASlave] Character logged in.");

        // Trigger auto-collection if enabled
        if (Configuration.AutoCollectOnLogin)
        {
            SlaveWindow.ScheduleAutoCollection();
        }
    }

    private void OnLogout(int type, int code)
    {
        // Cancel any running task on logout — BUT skip if relogger suppresses it
        // (logout is expected during /ays relog character switches)
        if (TaskRunner.IsRunning && !TaskRunner.SuppressLogoutCancel)
        {
            Log.Information("[XASlave] Character logged out — cancelling running task.");
            TaskRunner.Cancel();
        }
        else if (TaskRunner.IsRunning)
        {
            Log.Information("[XASlave] Character logged out — relogger active, not cancelling.");
        }

        Log.Information("[XASlave] Character logged out — sending final save to XA Database.");
        IpcClient.Save();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            SlaveWindow.Toggle();
            return;
        }

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

        // Unknown subcommand — toggle window
        SlaveWindow.Toggle();
    }

    public void ToggleMainUi() => SlaveWindow.Toggle();
}
