using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Microsoft.Data.Sqlite;
using XASlave.Data;
using XASlave.Services;
using XASlave.Services.Tasks;

namespace XASlave.Windows;

/// <summary>
/// Main window for XA Slave — left-hand task menu with right-side content panel.
/// Tasks are automation jobs that interact with the game and push data to XA Database via IPC.
/// Partial class — task/panel UI split into Windows/Tasks/ and Windows/Panels/.
/// </summary>
public partial class SlaveWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private const string PluginVersion = BuildInfo.Version;
    private const string WebsiteUrl = "https://aethertek.io/";
    private const string DiscordUrl = "https://discord.gg/g2NmYxPQCa";
    private const string LandingPageIconRelativePath = "images\\icon.png";
    private const float DefaultTaskMenuWidth = 100f;
    private const float MinTaskMenuWidth = 30f;
    private const float MaxTaskMenuWidth = DefaultTaskMenuWidth * 2f;
    private const double FrameworkUpdateTimingDebugThresholdMilliseconds = 5.0;
    private const double FrameworkUpdateTimingWarningThresholdMilliseconds = 25.0;
    private const float TaskLayoutColumnGap = 3f;
    private static float UiScale => ImGuiHelpers.GlobalScale;
    private static float UiScaleSafe => ImGuiHelpers.GlobalScaleSafe;
    private static Vector2 TitleBarIconOffset => ScaledVector(0f, 1f);
    private static readonly int[] CheckEveryHourOptions = { 0, 6, 12, 24, 48, 72, 168, 336, 720, 1440, 2160 };

    // Task menu
    private enum SlaveTask
    {
        // Tasks
        SaveToXaDatabase,
        CityChatFlooder,
        // City Shenanigans
        AutoGlamWeather,
        AutoRetainerTasks,
        // FC Relations
        MonthlyRelogger,
        CheckDuplicatePlots,
        PrepLogistics,
        Xagman,
        ReturnAltsToHomeworlds,
        RefreshArSubsBell,
        MultiFcPermissions,
        AutoAcceptFcInvite,
        // Utility
        WindowRenamer,
        PluginOperations,
        XAMods,
        // Reference
#if XA_SLAVE_TESTING_BUILD
        DebugCommands,
#endif
        ExportData,
        RepoList,
        IpcCallsAvailable,
        CommandsReference,
        SplashScreen,
    }

    private enum MenuSection
    {
        AutomatedTasks,
        CityShenanigans,
        FcRelations,
        Utility,
        Reference,
    }

    private static readonly (SlaveTask Task, string Label)[] TaskItems =
    {
        (SlaveTask.AutoRetainerTasks, "AutoRetainer Helper"),
        (SlaveTask.SaveToXaDatabase, "Save to XA Database"),
    };

    private static readonly (SlaveTask Task, string Label)[] CityShenanigansItems =
    {
        (SlaveTask.AutoGlamWeather, "Auto-Glam Weather"),
        (SlaveTask.CityChatFlooder, "City Chat Flooder"),
    };

    private static readonly (SlaveTask Task, string Label)[] FcItems =
    {
        (SlaveTask.Xagman, "Xagman"),
        (SlaveTask.MonthlyRelogger, "Monthly Relogger"),
        (SlaveTask.PrepLogistics, "Prep Logistics"),
        (SlaveTask.AutoAcceptFcInvite, "Auto-Accept FC Invites"),
        (SlaveTask.MultiFcPermissions, "FC Permissions Updater"),
        (SlaveTask.CheckDuplicatePlots, "Check Duplicate Plots"),
        (SlaveTask.ReturnAltsToHomeworlds, "Return Alts To Homeworlds"),
        (SlaveTask.RefreshArSubsBell, "Refresh Sub/Bell/Chest"),
    };

    private static readonly (SlaveTask Task, string Label)[] UtilityItems =
    {
        (SlaveTask.XAMods, "XA Mods"),
        (SlaveTask.WindowRenamer, "Window Renamer"),
        (SlaveTask.PluginOperations, "Plugin Operations"),
    };

    private static readonly (SlaveTask Task, string Label)[] ReferenceItems =
    {
#if XA_SLAVE_TESTING_BUILD
        (SlaveTask.DebugCommands, "Debug / Test"),
#endif
        (SlaveTask.ExportData, "Export Data"),
        (SlaveTask.RepoList, "Repo List"),
        (SlaveTask.IpcCallsAvailable, "IPC Calls Available"),
        (SlaveTask.CommandsReference, "Commands"),
        (SlaveTask.SplashScreen, "Splash Screen"),
    };

    private SlaveTask? selectedTask;

    private ITaskPanel? selectedExternalTask;

    public SlaveWindow(Plugin plugin)
        : base("XA Slave###SlaveWindow", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        UpdateSizeConstraints(UiScaleSafe);
        RestoreLastSelectedTaskSelection();
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.ClientState.Login += OnExportDataLogin;
        Plugin.ClientState.Logout += OnExportDataLogoutHandler;
#if XA_SLAVE_TESTING_BUILD
        Plugin.NamePlateGui.OnDataUpdate += OnXaAbuseNamePlateUpdate;
        Plugin.PluginInterface.UiBuilder.Draw += DrawXaAbuseOverlay;
#endif

        // Initialize Xagman peer event handlers for TCP task control
        InitializeXagmanPeerEventHandlers();

        RebuildTitleBarFavButtons();
    }

    public override void PreDraw()
    {
        UpdateSizeConstraints(UiScale);
        WindowName = plugin.Configuration.ShowVersionInUpdatesTitle
            ? $"XA Slave v{PluginVersion}###SlaveWindow"
            : "XA Slave###SlaveWindow";
        RefreshTitleBarFavIconColors();
    }

    private static readonly Vector4 FavIconColorOn  = new(1.00f, 1.00f, 1.00f, 1.0f);
    private static readonly Vector4 FavIconColorOff = new(0.45f, 0.45f, 0.45f, 1.0f);

    // indices into TitleBarButtons for buttons whose icon color must update each frame
    private int titleBarFavArPreIdx  = -1;
    private int titleBarFavArPostIdx = -1;
    private int titleBarFavGlamIdx   = -1;
    private int titleBarFavKillIdx   = -1;
    private readonly Dictionary<int, Func<Vector4>> titleBarFavCustomColorResolvers = new();

    private void RefreshTitleBarFavIconColors()
    {
        if (titleBarFavGlamIdx >= 0 && titleBarFavGlamIdx < TitleBarButtons.Count)
        {
            var btn = TitleBarButtons[titleBarFavGlamIdx];
            btn.IconColor = glamWeatherRunning ? FavIconColorOn : FavIconColorOff;
            TitleBarButtons[titleBarFavGlamIdx] = btn;
        }

        if (titleBarFavArPreIdx >= 0 && titleBarFavArPreIdx < TitleBarButtons.Count)
        {
            var btn = TitleBarButtons[titleBarFavArPreIdx];
            btn.IconColor = plugin.Configuration.ArPreProcessEnabled ? FavIconColorOn : FavIconColorOff;
            TitleBarButtons[titleBarFavArPreIdx] = btn;
        }

        if (titleBarFavArPostIdx >= 0 && titleBarFavArPostIdx < TitleBarButtons.Count)
        {
            var btn = TitleBarButtons[titleBarFavArPostIdx];
            btn.IconColor = plugin.Configuration.ArPostProcessEnabled ? FavIconColorOn : FavIconColorOff;
            TitleBarButtons[titleBarFavArPostIdx] = btn;
        }

        if (titleBarFavKillIdx >= 0 && titleBarFavKillIdx < TitleBarButtons.Count)
        {
            var btn = TitleBarButtons[titleBarFavKillIdx];
            btn.IconColor = (ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift) ? FavIconColorOn : FavIconColorOff;
            TitleBarButtons[titleBarFavKillIdx] = btn;
        }

        foreach (var (buttonIndex, colorResolver) in titleBarFavCustomColorResolvers)
        {
            if (buttonIndex < 0 || buttonIndex >= TitleBarButtons.Count)
                continue;

            var btn = TitleBarButtons[buttonIndex];
            btn.IconColor = colorResolver();
            TitleBarButtons[buttonIndex] = btn;
        }
    }


    private static readonly FontAwesomeIcon[] CustomFavSlotIcons =
    {
        FontAwesomeIcon.Bookmark,
        FontAwesomeIcon.Hashtag,
        FontAwesomeIcon.ThumbsUp,
        FontAwesomeIcon.Star,
    };

    private static FontAwesomeIcon GetCustomFavButtonIcon(int slotIndex, string selectionKey)
    {
        if (selectionKey.Equals(TitleBarFavSelectionKeys.SitNowKey, StringComparison.OrdinalIgnoreCase))
            return FontAwesomeIcon.Chair;

        if (selectionKey.Equals(TitleBarFavSelectionKeys.DozeNowKey, StringComparison.OrdinalIgnoreCase))
            return FontAwesomeIcon.Moon;

        return slotIndex < CustomFavSlotIcons.Length ? CustomFavSlotIcons[slotIndex] : FontAwesomeIcon.Star;
    }

    private readonly record struct TitleBarFavSelectionOption(
        string Key,
        string Label,
        string Category);

    private void UpdateSizeConstraints(float scale)
    {
        const float baseMinWidth = 300f;
        const float baseMinHeight = 240f;
        const float baseTitleWidth = 160f;
        const float perButtonWidth = 22f;

        var minWidth = Math.Max(baseMinWidth, baseTitleWidth + TitleBarButtons.Count * perButtonWidth) * scale;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(minWidth, baseMinHeight * scale),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    private static float Scale(float value)
        => value * UiScale;

    private static Vector2 ScaledVector(float x, float y)
        => ImGuiHelpers.ScaledVector2(x, y);

    private static float Unscale(float value)
        => value / Math.Max(UiScaleSafe, 0.01f);

    private void UpdateMinWidthForTitleBarButtons()
        => UpdateSizeConstraints(UiScaleSafe);

    internal void RebuildTitleBarFavButtons()
    {
        TitleBarButtons.Clear();
        titleBarFavArPreIdx  = -1;
        titleBarFavArPostIdx = -1;
        titleBarFavGlamIdx   = -1;
        titleBarFavKillIdx   = -1;
        titleBarFavCustomColorResolvers.Clear();

        var cfg = plugin.Configuration;

        // ── Resolution shortcuts (rightmost = added first) ──
        for (var ri = cfg.TitleBarFavResolutionItems.Count - 1; ri >= 0; ri--)
        {
            var item = cfg.TitleBarFavResolutionItems[ri];
            if (!item.Enabled) continue;
            var w = item.Width;
            var h = item.Height;
            var buttonIndex = TitleBarButtons.Count;
            TitleBarButtons.Add(new TitleBarButton
            {
                Icon = FontAwesomeIcon.Desktop,
                IconOffset = TitleBarIconOffset,
                ShowTooltip = () =>
                {
                    using var tt = ImRaii.Tooltip();
                    ImGui.TextUnformatted($"Resolution: {w}x{h}");
                    ImGui.TextDisabled(plugin.Configuration.CustomResolutionsEnabled
                        ? "Custom Resolutions is enabled."
                        : "Enable XA Mods > Custom Resolutions first.");
                },
                Click = _ =>
                {
                    if (!plugin.SystemWindowMods.TryApplyCustomResolution(w, h, out var resMsg))
                        Plugin.ChatGui.PrintError($"[XASlave] {resMsg}");
                },
            });
            titleBarFavCustomColorResolvers[buttonIndex] = () => plugin.Configuration.CustomResolutionsEnabled ? FavIconColorOn : FavIconColorOff;
        }

        // ── Custom favourites ──
        for (var ci = cfg.TitleBarFavCustomItems.Count - 1; ci >= 0; ci--)
        {
            var item = cfg.TitleBarFavCustomItems[ci];
            if (!item.Enabled)
                continue;

            var selectionKey = TitleBarFavSelectionKeys.Normalize(item.SelectionKey, item.MenuTarget);
            if (string.IsNullOrWhiteSpace(selectionKey))
                continue;

            TryAddTitleBarFavCustomButton(ci, selectionKey);
        }

        // ── AR Post-Process toggle ──
        if (cfg.TitleBarFavArPostProcessEnabled)
        {
            titleBarFavArPostIdx = TitleBarButtons.Count;
            TitleBarButtons.Add(new TitleBarButton
            {
                Icon = FontAwesomeIcon.FlagCheckered,
                IconOffset = TitleBarIconOffset,
                ShowTooltip = () =>
                {
                    using var tt = ImRaii.Tooltip();
                    ImGui.TextUnformatted(plugin.Configuration.ArPostProcessEnabled
                        ? "AR Post-Process: On — click to disable"
                        : "AR Post-Process: Off — click to enable");
                },
                Click = _ =>
                {
                    plugin.Configuration.ArPostProcessEnabled = !plugin.Configuration.ArPostProcessEnabled;
                    plugin.Configuration.Save();
                },
            });
        }

        // ── AR Pre-Process toggle ──
        if (cfg.TitleBarFavArPreProcessEnabled)
        {
            titleBarFavArPreIdx = TitleBarButtons.Count;
            TitleBarButtons.Add(new TitleBarButton
            {
                Icon = FontAwesomeIcon.GasPump,
                IconOffset = TitleBarIconOffset,
                ShowTooltip = () =>
                {
                    using var tt = ImRaii.Tooltip();
                    ImGui.TextUnformatted(plugin.Configuration.ArPreProcessEnabled
                        ? "AR Pre-Process: On — click to disable"
                        : "AR Pre-Process: Off — click to enable");
                },
                Click = _ =>
                {
                    plugin.Configuration.ArPreProcessEnabled = !plugin.Configuration.ArPreProcessEnabled;
                    plugin.Configuration.Save();
                },
            });
        }

        // ── Auto-Glam Weather toggle ──
        if (cfg.TitleBarFavGlamWeatherEnabled)
        {
            titleBarFavGlamIdx = TitleBarButtons.Count;
            TitleBarButtons.Add(new TitleBarButton
            {
                Icon = FontAwesomeIcon.CloudSun,
                IconOffset = TitleBarIconOffset,
                ShowTooltip = () =>
                {
                    using var tt = ImRaii.Tooltip();
                    ImGui.TextUnformatted(glamWeatherRunning
                        ? "Auto-Glam Weather: Running — click to stop"
                        : "Auto-Glam Weather: Stopped — click to start");
                },
                Click = _ =>
                {
                    if (glamWeatherRunning)
                        StopAutoGlamWeatherTask();
                    else
                        StartAutoGlamWeatherTask();
                },
            });
        }

        // ── Fav Mod List ──
        if (cfg.TitleBarFavModListEnabled && !string.IsNullOrWhiteSpace(cfg.TitleBarFavModListName))
        {
            var listName = cfg.TitleBarFavModListName;
            TitleBarButtons.Add(new TitleBarButton
            {
                Icon = FontAwesomeIcon.List,
                IconOffset = TitleBarIconOffset,
                ShowTooltip = () =>
                {
                    using var tt = ImRaii.Tooltip();
                    ImGui.TextUnformatted($"Load XA Mod List: {listName}");
                },
                Click = _ => { plugin.LoadModListPreset(listName, out var loadMsg); },
            });
        }

        // ── Disable All XA Mods ──
        if (cfg.TitleBarFavDisableAllModsEnabled)
        {
            TitleBarButtons.Add(new TitleBarButton
            {
                Icon = FontAwesomeIcon.Recycle,
                IconOffset = TitleBarIconOffset,
                ShowTooltip = () =>
                {
                    using var tt = ImRaii.Tooltip();
                    ImGui.TextUnformatted("Disable All XA Mods");
                },
                Click = _ => plugin.DisableAllXAMods(),
            });
        }

        // ── Kill Game (requires Ctrl+Shift) ──
        if (cfg.TitleBarFavKillGameEnabled && cfg.InstantLogoutEnabled)
        {
            titleBarFavKillIdx = TitleBarButtons.Count;
            TitleBarButtons.Add(new TitleBarButton
            {
                Icon = FontAwesomeIcon.Skull,
                IconOffset = TitleBarIconOffset,
                ShowTooltip = () =>
                {
                    using var tt = ImRaii.Tooltip();
                    if (!plugin.CanTriggerLogoutActions(out var blockedMessage))
                    {
                        ImGui.TextUnformatted(blockedMessage);
                        return;
                    }

                    var held = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
                    if (held)
                        ImGui.TextUnformatted("/xa killgame — release Ctrl+Shift to cancel");
                    else
                        ImGui.TextUnformatted("/xa killgame — hold Ctrl+Shift to unlock");
                },
                Click = _ =>
                {
                    if (ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift)
                    {
                        if (!plugin.TryRequestKillGameAction(out var message))
                            Plugin.ChatGui.PrintError($"[XASlave] {message}");
                    }
                },
            });
        }

        UpdateMinWidthForTitleBarButtons();
    }

    private IReadOnlyList<TitleBarFavSelectionOption> GetTitleBarFavSelectionOptions()
    {
        var options = new List<TitleBarFavSelectionOption>(AllBuiltInMenuLabels.Length + 48);

        foreach (var menuLabel in AllBuiltInMenuLabels)
            options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.FromMenuTarget(menuLabel), menuLabel, "Open Panel"));

        foreach (var modInfo in plugin.GetTitleBarFavXAModInfos())
            options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.FromXAModKey(modInfo.Key), $"XA Mod: {modInfo.DisplayName}", "XA Mods"));

        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.SpecialRenderHideAddonsKeepNameplatesKey, "Hide addons / keep nameplates", "Special Rendering Modes"));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.SpecialRenderHideAddonsKeepChatKey, "Hide addons / keep chat", "Special Rendering Modes"));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.SpecialRenderHideChatKey, "Hide chat", "Special Rendering Modes"));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.SpecialRenderHideActionBarsKey, "Hide action bars", "Special Rendering Modes"));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.SpecialRenderHideTargetInfoKey, "Hide target info", "Special Rendering Modes"));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.SpecialRenderHideNameplatesKey, "Hide nameplates", "Special Rendering Modes"));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.SpecialRenderRestoreAllKey, "Restore all", "Special Rendering Modes"));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.SitNowKey, "Sit now", "Doze & Sit Anywhere"));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.DozeNowKey, "Doze now", "Doze & Sit Anywhere"));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.DisableAllXAModsKey, "All XA Mods Off", "Quick Actions"));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.StopAllKey, "Stop All Automated Tasks", "Quick Actions"));
        return options;
    }

    private string GetTitleBarFavSelectionLabel(string? selectionKey, string? legacyMenuTarget = null)
    {
        var normalizedSelectionKey = TitleBarFavSelectionKeys.Normalize(selectionKey, legacyMenuTarget);
        if (string.IsNullOrWhiteSpace(normalizedSelectionKey))
            return "(none)";

        foreach (var option in GetTitleBarFavSelectionOptions())
        {
            if (option.Key.Equals(normalizedSelectionKey, StringComparison.OrdinalIgnoreCase))
                return option.Label;
        }

        return normalizedSelectionKey;
    }

    private bool HasAnySpecialRenderStoredUiToggles()
    {
        var cfg = plugin.Configuration;
        return cfg.SpecialRenderHideAddonsKeepNameplatesEnabled
            || cfg.SpecialRenderHideAddonsKeepChatEnabled
            || cfg.SpecialRenderHideChatEnabled
            || cfg.SpecialRenderHideActionBarsEnabled
            || cfg.SpecialRenderHideTargetInfoEnabled
            || cfg.SpecialRenderHideNameplatesEnabled;
    }

    private bool IsSpecialRenderTitleBarToggleActive(Func<Configuration, bool> selector)
    {
        return plugin.Configuration.SpecialRenderModesEnabled && selector(plugin.Configuration);
    }

    private void ToggleSpecialRenderTitleBarSetting(Func<Configuration, bool> selector, Action<Configuration, bool> store)
    {
        var cfg = plugin.Configuration;
        var shouldEnable = !cfg.SpecialRenderModesEnabled || !selector(cfg);
        store(cfg, shouldEnable);

        if (!cfg.SpecialRenderModesEnabled)
            cfg.SpecialRenderModesEnabled = plugin.SetSpecialRenderModesEnabled(true);
        else
            plugin.ApplySpecialRenderModesConfiguration();

        cfg.Save();
    }

    private void RestoreAllSpecialRenderTitleBarSettings()
    {
        plugin.RestoreSpecialRenderModes(clearStoredUiToggles: true);
        plugin.Configuration.Save();
    }

    private bool TryAddTitleBarFavCustomButton(int slotIndex, string selectionKey)
    {
        var slotIcon = GetCustomFavButtonIcon(slotIndex, selectionKey);
        bool AddQuickActionButton(string title, string subtitle, System.Action click, Func<Vector4>? colorResolver = null)
        {
            var buttonIndex = TitleBarButtons.Count;
            TitleBarButtons.Add(new TitleBarButton
            {
                Icon = slotIcon,
                IconOffset = TitleBarIconOffset,
                ShowTooltip = () =>
                {
                    using var tt = ImRaii.Tooltip();
                    ImGui.TextUnformatted(title);
                    if (subtitle.Length > 0)
                        ImGui.TextDisabled(subtitle);
                },
                Click = _ => click(),
            });

            if (colorResolver != null)
                titleBarFavCustomColorResolvers[buttonIndex] = colorResolver;

            return true;
        }

        if (TitleBarFavSelectionKeys.TryGetMenuTarget(selectionKey, out var menuTarget))
        {
            TitleBarButtons.Add(new TitleBarButton
            {
                Icon = slotIcon,
                IconOffset = TitleBarIconOffset,
                ShowTooltip = () =>
                {
                    using var tt = ImRaii.Tooltip();
                    ImGui.TextUnformatted(menuTarget);
                },
                Click = _ => NavigateToMenuLabel(menuTarget),
            });
            return true;
        }

        if (TitleBarFavSelectionKeys.TryGetXAModKey(selectionKey, out var xamodKey)
            && plugin.TryGetTitleBarFavXAModInfo(xamodKey, out var modInfo))
        {
            var buttonIndex = TitleBarButtons.Count;
            TitleBarButtons.Add(new TitleBarButton
            {
                Icon = slotIcon,
                IconOffset = TitleBarIconOffset,
                ShowTooltip = () =>
                {
                    using var tt = ImRaii.Tooltip();
                    var enabled = plugin.IsXAModEnabled(xamodKey);
                    ImGui.TextUnformatted($"{modInfo.DisplayName} [{modInfo.ScopeLabel}]");
                    ImGui.TextUnformatted(enabled ? "On - click to disable" : "Off - click to enable");
                },
                Click = _button =>
                {
                    plugin.ToggleXAModByKey(xamodKey, out var _, out var _);
                    EnsureKillGameTitleBarDependency();
                },
            });
            titleBarFavCustomColorResolvers[buttonIndex] = () => plugin.IsXAModEnabled(xamodKey) ? FavIconColorOn : FavIconColorOff;
            return true;
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.SpecialRenderHideAddonsKeepNameplatesKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                "Special Rendering Modes: Hide addons / keep nameplates",
                "Turns on the stored UI preset and enables Special Rendering Modes if needed.",
                () => ToggleSpecialRenderTitleBarSetting(
                    cfg => cfg.SpecialRenderHideAddonsKeepNameplatesEnabled,
                    (cfg, value) => cfg.SpecialRenderHideAddonsKeepNameplatesEnabled = value),
                () => IsSpecialRenderTitleBarToggleActive(cfg => cfg.SpecialRenderHideAddonsKeepNameplatesEnabled) ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.SpecialRenderHideAddonsKeepChatKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                "Special Rendering Modes: Hide addons / keep chat",
                "Turns on the stored UI preset and enables Special Rendering Modes if needed.",
                () => ToggleSpecialRenderTitleBarSetting(
                    cfg => cfg.SpecialRenderHideAddonsKeepChatEnabled,
                    (cfg, value) => cfg.SpecialRenderHideAddonsKeepChatEnabled = value),
                () => IsSpecialRenderTitleBarToggleActive(cfg => cfg.SpecialRenderHideAddonsKeepChatEnabled) ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.SpecialRenderHideChatKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                "Special Rendering Modes: Hide chat",
                "Turns on the stored UI preset and enables Special Rendering Modes if needed.",
                () =>
                {
                    var shouldEnable = !plugin.Configuration.SpecialRenderModesEnabled || !plugin.Configuration.SpecialRenderHideChatEnabled;
                    if (shouldEnable && !plugin.CanEnableSpecialRenderHideChat(out var message))
                    {
                        Plugin.ChatGui.PrintError($"[XASlave] {message}");
                        return;
                    }

                    ToggleSpecialRenderTitleBarSetting(
                        cfg => cfg.SpecialRenderHideChatEnabled,
                        (cfg, value) => cfg.SpecialRenderHideChatEnabled = value);
                },
                () => IsSpecialRenderTitleBarToggleActive(cfg => cfg.SpecialRenderHideChatEnabled) ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.SpecialRenderHideActionBarsKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                "Special Rendering Modes: Hide action bars",
                "Turns on the stored UI preset and enables Special Rendering Modes if needed.",
                () => ToggleSpecialRenderTitleBarSetting(
                    cfg => cfg.SpecialRenderHideActionBarsEnabled,
                    (cfg, value) => cfg.SpecialRenderHideActionBarsEnabled = value),
                () => IsSpecialRenderTitleBarToggleActive(cfg => cfg.SpecialRenderHideActionBarsEnabled) ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.SpecialRenderHideTargetInfoKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                "Special Rendering Modes: Hide target info",
                "Turns on the stored UI preset and enables Special Rendering Modes if needed.",
                () => ToggleSpecialRenderTitleBarSetting(
                    cfg => cfg.SpecialRenderHideTargetInfoEnabled,
                    (cfg, value) => cfg.SpecialRenderHideTargetInfoEnabled = value),
                () => IsSpecialRenderTitleBarToggleActive(cfg => cfg.SpecialRenderHideTargetInfoEnabled) ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.SpecialRenderHideNameplatesKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                "Special Rendering Modes: Hide nameplates",
                "Turns on the stored UI preset and enables Special Rendering Modes if needed.",
                () => ToggleSpecialRenderTitleBarSetting(
                    cfg => cfg.SpecialRenderHideNameplatesEnabled,
                    (cfg, value) => cfg.SpecialRenderHideNameplatesEnabled = value),
                () => IsSpecialRenderTitleBarToggleActive(cfg => cfg.SpecialRenderHideNameplatesEnabled) ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.SpecialRenderRestoreAllKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                "Special Rendering Modes: Restore all",
                "Restores world and UI visibility, then clears the stored Special Rendering Modes UI toggles.",
                RestoreAllSpecialRenderTitleBarSettings,
                () => plugin.Configuration.SpecialRenderModesEnabled || HasAnySpecialRenderStoredUiToggles() ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.SitNowKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                "Sit now",
                "Requires XA Mods > Doze & Sit Anywhere to be enabled.",
                () => plugin.DozeSitAnywhere.RequestSit(),
                () => plugin.Configuration.DozeSitAnywhereEnabled ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.DozeNowKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                "Doze now",
                "Requires XA Mods > Doze & Sit Anywhere to be enabled.",
                () => plugin.DozeSitAnywhere.RequestDoze(),
                () => plugin.Configuration.DozeSitAnywhereEnabled ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.DisableAllXAModsKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                "All XA Mods Off",
                "Disable every XA Mod toggle.",
                () =>
                {
                    plugin.DisableAllXAMods();
                    EnsureKillGameTitleBarDependency();
                },
                () => plugin.HasAnyEnabledXAMods() ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.StopAllKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                "Stop All Automated Tasks",
                "Stop active XA task flows and disconnect Xagman.",
                StopAllTitleBarOperations,
                () => HasStopAllTitleBarTargets() ? FavIconColorOn : FavIconColorOff);
        }

        return false;
    }

    private void EnsureKillGameTitleBarDependency()
    {
        if (!plugin.Configuration.TitleBarFavKillGameEnabled || plugin.Configuration.InstantLogoutEnabled)
            return;

        plugin.Configuration.TitleBarFavKillGameEnabled = false;
        plugin.Configuration.Save();
        RebuildTitleBarFavButtons();
    }

    private bool HasStopAllTitleBarTargets()
    {
        return plugin.TaskRunner.IsRunning
            || plugin.AutoCollector.IsRunning
            || plugin.ArPostProcessor.IsRunning
            || glamWeatherRunning
            || fcFloaterRunning
            || xagmanRunning
            || plugin.XagmanPeers.IsStarted;
    }

    private void StopAllTitleBarOperations()
    {
        if (glamWeatherRunning)
            StopAutoGlamWeatherTask();

        if (fcFloaterRunning)
            StopAutoAcceptFcInviteTask();

        if (plugin.TaskRunner.IsRunning)
        {
            if (TryMapPriorityTaskName(plugin.TaskRunner.CurrentTaskName, out var activeTask))
                StopPriorityTask(activeTask);
            else
                plugin.TaskRunner.Cancel();
        }

        if (plugin.AutoCollector.IsRunning)
            plugin.AutoCollector.Cancel();

        if (plugin.ArPostProcessor.IsRunning)
            plugin.ArPostProcessor.Cancel();

        ReleaseRefreshSubsArSuppression();

        if (xagmanRunning)
            StopXagmanTask();

        if (plugin.XagmanPeers.IsStarted)
            plugin.SetXagmanPeerConnectionsEnabled(false);

        UpdatePriorityTaskExternalStatus();
    }

    private void NavigateToMenuLabel(string menuLabel)
    {
        if (string.IsNullOrWhiteSpace(menuLabel)) return;

        // Search all built-in section arrays for matching label
        foreach (var (task, label) in TaskItems)
        {
            if (string.Equals(label, menuLabel, StringComparison.OrdinalIgnoreCase))
            {
                IsOpen = true;
                SelectBuiltInTask(task);
                return;
            }
        }

        foreach (var (task, label) in CityShenanigansItems)
        {
            if (string.Equals(label, menuLabel, StringComparison.OrdinalIgnoreCase))
            {
                IsOpen = true;
                SelectBuiltInTask(task);
                return;
            }
        }

        foreach (var (task, label) in FcItems)
        {
            if (string.Equals(label, menuLabel, StringComparison.OrdinalIgnoreCase))
            {
                IsOpen = true;
                SelectBuiltInTask(task);
                return;
            }
        }

        foreach (var (task, label) in UtilityItems)
        {
            if (string.Equals(label, menuLabel, StringComparison.OrdinalIgnoreCase))
            {
                IsOpen = true;
                SelectBuiltInTask(task);
                return;
            }
        }

        foreach (var (task, label) in ReferenceItems)
        {
            if (string.Equals(label, menuLabel, StringComparison.OrdinalIgnoreCase))
            {
                IsOpen = true;
                if (task == SlaveTask.SplashScreen)
                    ClearTaskSelection();
                else
                    SelectBuiltInTask(task);
                return;
            }
        }
    }

    public void RebindXagmanPeerEventHandlers()
    {
        InitializeXagmanPeerEventHandlers();
    }

    public void OpenXAModsTask()
    {
        IsOpen = true;
        SelectBuiltInTask(SlaveTask.XAMods);
    }

    public void OpenCommandsReferenceTask()
    {
        IsOpen = true;
        SelectBuiltInTask(SlaveTask.CommandsReference);
    }

    public void Dispose()
    {
        CancelScheduledAutoCollection(true);
        ReleaseRefreshSubsArSuppression();
        StopXagmanTask();
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.ClientState.Login -= OnExportDataLogin;
        Plugin.ClientState.Logout -= OnExportDataLogoutHandler;
#if XA_SLAVE_TESTING_BUILD
        xaAbuseEnabled = false;
        Plugin.NamePlateGui.RequestRedraw();
        Plugin.NamePlateGui.OnDataUpdate -= OnXaAbuseNamePlateUpdate;
        Plugin.PluginInterface.UiBuilder.Draw -= DrawXaAbuseOverlay;
#endif
    }

    private void RestoreLastSelectedTaskSelection()
    {
        selectedTask = null;
        selectedExternalTask = null;
    }

    private void PersistLastSelectedTaskSelection()
    {
        var cfg = plugin.Configuration;
        var builtInTaskName = selectedTask?.ToString() ?? string.Empty;
        var externalTaskName = selectedExternalTask?.Name ?? string.Empty;

        if (string.Equals(cfg.LastSelectedBuiltInTask, builtInTaskName, StringComparison.Ordinal)
            && string.Equals(cfg.LastSelectedExternalTaskName, externalTaskName, StringComparison.Ordinal))
            return;

        cfg.LastSelectedBuiltInTask = builtInTaskName;
        cfg.LastSelectedExternalTaskName = externalTaskName;
        cfg.Save();
    }

    private void SelectBuiltInTask(SlaveTask task)
    {
        selectedTask = task;
        selectedExternalTask = null;
        PersistLastSelectedTaskSelection();
    }

    private void SelectExternalTask(ITaskPanel task)
    {
        selectedTask = null;
        selectedExternalTask = task;
        PersistLastSelectedTaskSelection();
    }

    private void ClearTaskSelection()
    {
        selectedTask = null;
        selectedExternalTask = null;
        PersistLastSelectedTaskSelection();
    }

    private static int NormalizeCheckEveryHours(int hours)
    {
        return CheckEveryHourOptions.Contains(hours) ? hours : 0;
    }

    private static int GetCheckEveryIndex(int hours)
    {
        var normalized = NormalizeCheckEveryHours(hours);
        for (var i = 0; i < CheckEveryHourOptions.Length; i++)
        {
            if (CheckEveryHourOptions[i] == normalized)
                return i;
        }

        return 0;
    }

    private static string FormatCheckEveryHours(int hours)
    {
        var normalized = NormalizeCheckEveryHours(hours);
        if (normalized == 0)
            return "Always";

        if (normalized % 24 == 0)
        {
            var days = normalized / 24;
            return days == 1 ? "1 day" : $"{days} days";
        }

        return normalized == 1 ? "1 hour" : $"{normalized} hours";
    }

    public void ScheduleAutoCollection()
    {
        CancelScheduledAutoCollection(false);

        if (TryGetActivePriorityTask(out var activeTask, out var activeLabel) && IsFcRelationPriorityTask(activeTask))
        {
            Plugin.Log.Information($"[XASlave] Auto-collection login checkpoint skipped because {activeLabel} has priority.");
            return;
        }

        var arMultiEnabled = plugin.IpcClient.AutoRetainerGetMultiModeEnabled();
        if (arMultiEnabled && plugin.Configuration.ArPreProcessEnabled)
        {
            Plugin.Log.Information("[XASlave] Auto-collection login checkpoint skipped because AR pre-processing already owns the login checkpoint.");
            return;
        }

        var cadenceHours = NormalizeCheckEveryHours(plugin.Configuration.AutoCollectCheckEveryHours);
        autoCollectScheduledAt = DateTime.UtcNow;
        autoCollectScheduledDelaySeconds = plugin.Configuration.AutoCollectDelaySeconds;
        autoCollectResumeArOnCompletion = arMultiEnabled;
        autoCollectSkipPending = false;
        autoCollectSkipMessage = string.Empty;

        if (arMultiEnabled)
            plugin.IpcClient.AutoRetainerSetSuppressed(true);

        if (!plugin.IsCurrentCharacterSyncDue(cadenceHours))
        {
            autoCollectSkipPending = true;
            autoCollectScheduledDelaySeconds = arMultiEnabled ? 1f : 0f;
            autoCollectSkipMessage = $"XA Database sync skipped — last sync is still within the {FormatCheckEveryHours(cadenceHours)} window.";
        }

        if (arMultiEnabled && plugin.Configuration.AutoCollectDisableWhenArMultiEnabled)
        {
            autoCollectSkipPending = true;
            autoCollectScheduledDelaySeconds = 1f;
            autoCollectSkipMessage = "AR Multi enabled, skipping XA Database push.";
        }

        Plugin.Log.Information($"[XASlave] Auto-collection scheduled (delay {autoCollectScheduledDelaySeconds}s, arMulti={arMultiEnabled}, skipPending={autoCollectSkipPending}).");
    }

    public void CancelScheduledAutoCollection(bool resumeAr)
    {
        var shouldResumeAr = resumeAr && autoCollectResumeArOnCompletion;
        autoCollectScheduledAt = null;
        autoCollectScheduledDelaySeconds = 0f;
        autoCollectSkipPending = false;
        autoCollectSkipMessage = string.Empty;
        autoCollectResumeArOnCompletion = false;

        if (shouldResumeAr)
            plugin.IpcClient.AutoRetainerSetSuppressed(false);
    }

    private void HaltAutoCollectionForPriorityTask(string taskLabel)
    {
        var hadScheduled = autoCollectScheduledAt.HasValue;
        var collectorRunning = plugin.AutoCollector.IsRunning;
        if (!hadScheduled && !collectorRunning)
            return;

        Plugin.Log.Information($"[XASlave] Halting Save to XA Database auto-collection because {taskLabel} took priority.");
        CancelScheduledAutoCollection(true);

        if (collectorRunning)
        {
            plugin.AutoCollector.AddLog($"Auto-collection cancelled because {taskLabel} took priority.");
            plugin.AutoCollector.Cancel();
        }
        else
        {
            plugin.AutoCollector.AddLog($"Scheduled auto-collection skipped because {taskLabel} took priority.");
            SetIpcResult($"{taskLabel} took priority over login auto-collection.");
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var totalStopwatch = Stopwatch.StartNew();
        MeasureFrameworkUpdateStep("SlaveWindow.EnforceSpecialRenderSafety", plugin.EnforceSpecialRenderSafetyOnFrameworkTick);
        MeasureFrameworkUpdateStep("SlaveWindow.UpdatePriorityTaskMonitors", UpdatePriorityTaskMonitors);
        MeasureFrameworkUpdateStep("SlaveWindow.UpdatePriorityTaskExternalStatus", UpdatePriorityTaskExternalStatus);
        MeasureFrameworkUpdateStep("SlaveWindow.OnExportDataFrameworkTick", OnExportDataFrameworkTick);
        MeasureFrameworkUpdateStep("SlaveWindow.UpdateXagmanFrameworkTick", UpdateXagmanFrameworkTick);

        if (!autoCollectScheduledAt.HasValue || !Plugin.PlayerState.IsLoaded || plugin.AutoCollector.IsRunning)
        {
            totalStopwatch.Stop();
            LogFrameworkUpdateStepDuration("SlaveWindow.OnFrameworkUpdate", totalStopwatch.Elapsed.TotalMilliseconds);
            return;
        }

        var delay = (float)(DateTime.UtcNow - autoCollectScheduledAt.Value).TotalSeconds;
        if (delay < autoCollectScheduledDelaySeconds || !plugin.AutoCollector.IsNormalCondition())
        {
            totalStopwatch.Stop();
            LogFrameworkUpdateStepDuration("SlaveWindow.OnFrameworkUpdate", totalStopwatch.Elapsed.TotalMilliseconds);
            return;
        }

        autoCollectScheduledAt = null;

        if (autoCollectSkipPending)
        {
            if (!string.IsNullOrWhiteSpace(autoCollectSkipMessage))
                SetIpcResult(autoCollectSkipMessage);

            if (autoCollectResumeArOnCompletion)
                plugin.IpcClient.AutoRetainerSetSuppressed(false);

            autoCollectScheduledDelaySeconds = 0f;
            autoCollectSkipPending = false;
            autoCollectSkipMessage = string.Empty;
            autoCollectResumeArOnCompletion = false;
            totalStopwatch.Stop();
            LogFrameworkUpdateStepDuration("SlaveWindow.OnFrameworkUpdate", totalStopwatch.Elapsed.TotalMilliseconds);
            return;
        }

        var resumeArAfterCollection = autoCollectResumeArOnCompletion;
        autoCollectScheduledDelaySeconds = 0f;
        autoCollectSkipPending = false;
        autoCollectSkipMessage = string.Empty;
        autoCollectResumeArOnCompletion = false;
        MeasureFrameworkUpdateStep("SlaveWindow.RunAutoCollection", () => RunAutoCollection(resumeArAfterCollection));
        totalStopwatch.Stop();
        LogFrameworkUpdateStepDuration("SlaveWindow.OnFrameworkUpdate", totalStopwatch.Elapsed.TotalMilliseconds);
    }

    private void MeasureFrameworkUpdateStep(string label, System.Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        stopwatch.Stop();
        LogFrameworkUpdateStepDuration(label, stopwatch.Elapsed.TotalMilliseconds);
    }

    private void LogFrameworkUpdateStepDuration(string label, double elapsedMilliseconds)
    {
        if (elapsedMilliseconds < FrameworkUpdateTimingDebugThresholdMilliseconds)
            return;

        var message = $"[XASlave] Framework tick step '{label}' took {elapsedMilliseconds:F1}ms.";
        if (elapsedMilliseconds >= FrameworkUpdateTimingWarningThresholdMilliseconds)
            Plugin.Log.Warning(message);
        else
            Plugin.Log.Debug(message);
    }

    private void UpdatePriorityTaskMonitors()
    {
        if (glamWeatherRunning)
        {
            var glamInterval = Math.Max(1f, plugin.Configuration.AutoGlamWeatherCheckIntervalSeconds);
            if ((DateTime.UtcNow - glamLastCheck).TotalSeconds >= glamInterval)
            {
                glamLastCheck = DateTime.UtcNow;
                CheckWeatherAndChangeGlamour();
            }
        }

        if (fcFloaterRunning)
        {
            var elapsed = (DateTime.UtcNow - fcFloaterStartTime).TotalSeconds;
            var remaining = (fcFloaterTimeoutMinutes * 60) - elapsed;
            if (remaining <= 0)
            {
                fcFloaterRunning = false;
                plugin.TaskRunner.AddLog($"[FC Floater] Timeout reached ({fcFloaterTimeoutMinutes} minutes). Stopping.");
            }
            else if ((DateTime.UtcNow - fcFloaterLastCheck).TotalSeconds >= fcFloaterCheckInterval)
            {
                fcFloaterLastCheck = DateTime.UtcNow;
                PollFcInvitations();
            }
        }
    }

    public override void Draw()
    {
        RefreshTitleBarFavIconColors();

        // ── Left panel: Task menu ──
        var storedLeftWidth = ClampTaskMenuWidth(plugin.Configuration.TaskMenuWidth);
        if (Math.Abs(storedLeftWidth - plugin.Configuration.TaskMenuWidth) > 0.01f)
        {
            plugin.Configuration.TaskMenuWidth = storedLeftWidth;
            plugin.Configuration.Save();
        }
        var leftWidth = Scale(storedLeftWidth);

        var panelHeight = Math.Max(Scale(120f), ImGui.GetContentRegionAvail().Y - Scale(30f));
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, ScaledVector(TaskLayoutColumnGap, 0f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.25f, 0.25f, 0.25f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.SeparatorHovered, new Vector4(0.40f, 0.40f, 0.40f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.SeparatorActive, new Vector4(0.55f, 0.55f, 0.55f, 1.0f));
        if (ImGui.BeginTable("SlaveLayout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.NoSavedSettings, new Vector2(0f, panelHeight)))
        {
            ImGui.TableSetupColumn("TaskMenuColumn", ImGuiTableColumnFlags.WidthFixed, leftWidth);
            ImGui.TableSetupColumn("TaskContentColumn", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            DrawTaskMenuPanel(panelHeight);
            var currentWidth = ClampTaskMenuWidth(Unscale(ImGui.GetColumnWidth(0)));

            if (Math.Abs(plugin.Configuration.TaskMenuWidth - currentWidth) > 0.5f)
            {
                plugin.Configuration.TaskMenuWidth = currentWidth;
                plugin.Configuration.Save();
            }

            ImGui.TableNextColumn();
            DrawTaskContentPanel(panelHeight);
            ImGui.EndTable();
        }
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();

        // ── Status bar ──
        ImGui.Separator();
        DrawStatusBar();
    }

    private void DrawTaskMenuPanel(float panelHeight)
    {
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.25f, 0.25f, 0.25f, 1.0f));
        using (var child = ImRaii.Child("TaskMenu", new Vector2(0f, panelHeight), true))
        {
            if (child.Success)
            {
                DrawMenuSection(MenuSection.AutomatedTasks, "Automated Tasks", TaskItems, new Vector4(0.4f, 0.8f, 1.0f, 1.0f));
                DrawMenuSection(MenuSection.CityShenanigans, "City Shenanigans", CityShenanigansItems, new Vector4(1.0f, 0.7f, 0.4f, 1.0f));
                DrawMenuSection(MenuSection.FcRelations, "FC Relations", FcItems, new Vector4(0.8f, 0.6f, 1.0f, 1.0f));
                DrawMenuSection(MenuSection.Utility, "Utility", UtilityItems, new Vector4(0.6f, 1.0f, 0.6f, 1.0f));

                foreach (var ext in plugin.ExternalTaskLoader.Tasks)
                {
                    if (!TryGetVisibleTaskLabel(ext.Label, out var visibleLabel))
                        continue;
                    var isSelected = selectedExternalTask == ext;
                    if (ImGui.Selectable(visibleLabel, isSelected))
                    {
                        if (isSelected && ImGui.GetIO().KeyCtrl)
                            ClearTaskSelection();
                        else
                            SelectExternalTask(ext);
                    }
                }

                DrawMenuSection(MenuSection.Reference, "Reference", ReferenceItems, new Vector4(0.6f, 0.6f, 0.6f, 1.0f));
            }
        }
        ImGui.PopStyleColor();
    }

    private void DrawTaskContentPanel(float panelHeight)
    {
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.25f, 0.25f, 0.25f, 1.0f));
        using (var child = ImRaii.Child("TaskContent", new Vector2(0f, panelHeight), true))
        {
            if (child.Success)
            {
                if (selectedExternalTask != null)
                {
                    try { selectedExternalTask.Draw(); }
                    catch (Exception ex)
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"Error: {ex.Message}");
                    }
                }
                else
                {
                    switch (selectedTask)
                    {
                        case null:
                            DrawDefaultLandingPage();
                            break;
                        case SlaveTask.SaveToXaDatabase:
                            DrawSaveToXaDatabaseTask();
                            break;
                        case SlaveTask.MonthlyRelogger:
                            DrawMonthlyReloggerTask();
                            break;
                        case SlaveTask.CheckDuplicatePlots:
                            DrawCheckDuplicatePlotsTask();
                            break;
                        case SlaveTask.Xagman:
                            DrawXagmanTask();
                            break;
                        case SlaveTask.ReturnAltsToHomeworlds:
                            DrawReturnAltsToHomeworldsTask();
                            break;
                        case SlaveTask.CityChatFlooder:
                            DrawCityChatFlooder();
                            break;
                        case SlaveTask.AutoGlamWeather:
                            DrawAutoGlamWeatherTask();
                            break;
                        case SlaveTask.AutoRetainerTasks:
                            DrawArPostProcessTask();
                            break;
                        case SlaveTask.PrepLogistics:
                            DrawPrepLogisticsTask();
                            break;
                        case SlaveTask.RefreshArSubsBell:
                            DrawRefreshArSubsBellTask();
                            break;
                        case SlaveTask.AutoAcceptFcInvite:
                            DrawAutoAcceptFcInviteTask();
                            break;
                        case SlaveTask.MultiFcPermissions:
                            DrawMultiFcPermissionsTask();
                            break;
                        case SlaveTask.WindowRenamer:
                            DrawWindowRenamerTask();
                            break;
                        case SlaveTask.PluginOperations:
                            DrawPluginOperationsTask();
                            break;
                        case SlaveTask.XAMods:
                            DrawXAModsTask();
                            break;
#if XA_SLAVE_TESTING_BUILD
                        case SlaveTask.DebugCommands:
                            DrawDebugCommands();
                            break;
#endif
                        case SlaveTask.ExportData:
                            DrawExportData();
                            break;
                        case SlaveTask.RepoList:
                            DrawRepoList();
                            break;
                        case SlaveTask.IpcCallsAvailable:
                            DrawIpcCallsAvailable();
                            break;
                        case SlaveTask.CommandsReference:
                            DrawCommandsReference();
                            break;
                    }
                }
            }
        }
        ImGui.PopStyleColor();
    }

    // ───────────────────────────────────────────────
    //  Status Bar
    // ───────────────────────────────────────────────
    private static bool TryGetVisibleTaskLabel(string rawLabel, out string visibleLabel)
    {
        const string testingMarker = "[IF=Testing]";
        if (rawLabel.StartsWith(testingMarker, StringComparison.OrdinalIgnoreCase))
        {
#if XA_SLAVE_TESTING_BUILD
            visibleLabel = rawLabel.Substring(testingMarker.Length).TrimStart();
            return true;
#else
            visibleLabel = string.Empty;
            return false;
#endif
        }

        visibleLabel = rawLabel;
        return true;
    }

    private static bool IsPriorityTask(SlaveTask task)
    {
        return task is SlaveTask.CityChatFlooder
            or SlaveTask.AutoGlamWeather
            or SlaveTask.MonthlyRelogger
            or SlaveTask.CheckDuplicatePlots
            or SlaveTask.Xagman
            or SlaveTask.ReturnAltsToHomeworlds
            or SlaveTask.PrepLogistics
            or SlaveTask.RefreshArSubsBell
            or SlaveTask.MultiFcPermissions
            or SlaveTask.AutoAcceptFcInvite;
    }

    private static bool IsFcRelationPriorityTask(SlaveTask task)
    {
        return task is SlaveTask.MonthlyRelogger
            or SlaveTask.CheckDuplicatePlots
            or SlaveTask.Xagman
            or SlaveTask.ReturnAltsToHomeworlds
            or SlaveTask.PrepLogistics
            or SlaveTask.RefreshArSubsBell
            or SlaveTask.MultiFcPermissions;
    }

    private static bool TryMapPriorityTaskName(string taskName, out SlaveTask task)
    {
        switch (taskName)
        {
            case "City Chat Flooder":
                task = SlaveTask.CityChatFlooder;
                return true;
            case "Auto-Accept FC Invites":
            case "FC Floater: Process Invite":
                task = SlaveTask.AutoAcceptFcInvite;
                return true;
            case "Monthly Relogger":
                task = SlaveTask.MonthlyRelogger;
                return true;
            case "Check Duplicate Plots":
                task = SlaveTask.CheckDuplicatePlots;
                return true;
            case "Xagman":
                task = SlaveTask.Xagman;
                return true;
            case "Return Alts To Homeworlds":
                task = SlaveTask.ReturnAltsToHomeworlds;
                return true;
            case "Prep Logistics":
                task = SlaveTask.PrepLogistics;
                return true;
            case "Refresh AR Subs/Bell":
            case "Refresh Sub/Bell/Chest":
                task = SlaveTask.RefreshArSubsBell;
                return true;
            case "FC Permissions Updater":
                task = SlaveTask.MultiFcPermissions;
                return true;
            default:
                task = default;
                return false;
        }
    }

    private static string GetPriorityTaskLabel(SlaveTask task)
    {
        return task switch
        {
            SlaveTask.CityChatFlooder => "City Chat Flooder",
            SlaveTask.AutoGlamWeather => "Auto-Glam Weather",
            SlaveTask.MonthlyRelogger => "Monthly Relogger",
            SlaveTask.CheckDuplicatePlots => "Check Duplicate Plots",
            SlaveTask.Xagman => "Xagman",
            SlaveTask.ReturnAltsToHomeworlds => "Return Alts To Homeworlds",
            SlaveTask.PrepLogistics => "Prep Logistics",
            SlaveTask.RefreshArSubsBell => "Refresh Sub/Bell/Chest",
            SlaveTask.MultiFcPermissions => "FC Permissions Updater",
            SlaveTask.AutoAcceptFcInvite => "Auto-Accept FC Invites",
            _ => string.Empty,
        };
    }

    private static string GetPriorityTaskDtrLabel(SlaveTask task)
    {
        return task switch
        {
            SlaveTask.AutoGlamWeather => "Auto-Glam",
            _ => GetPriorityTaskLabel(task),
        };
    }

    private string GetPriorityTaskStatusLabel(SlaveTask task)
    {
        return task switch
        {
            SlaveTask.Xagman => GetXagmanPriorityStatusLabel(),
            _ => GetPriorityTaskLabel(task),
        };
    }

    private string GetPriorityTaskExternalStatusLabel(SlaveTask task)
    {
        return task switch
        {
            SlaveTask.Xagman => GetXagmanPriorityStatusLabel(),
            _ => GetPriorityTaskDtrLabel(task),
        };
    }

    private bool TryGetActivePriorityTask(out SlaveTask task, out string label)
    {
        if (plugin.TaskRunner.IsRunning && TryMapPriorityTaskName(plugin.TaskRunner.CurrentTaskName, out task))
        {
            label = GetPriorityTaskLabel(task);
            return true;
        }

        if (xagmanRunning)
        {
            task = SlaveTask.Xagman;
            label = GetPriorityTaskLabel(task);
            return true;
        }

        if (glamWeatherRunning)
        {
            task = SlaveTask.AutoGlamWeather;
            label = GetPriorityTaskLabel(task);
            return true;
        }

        if (fcFloaterRunning)
        {
            task = SlaveTask.AutoAcceptFcInvite;
            label = GetPriorityTaskLabel(task);
            return true;
        }

        task = default;
        label = string.Empty;
        return false;
    }

    private bool IsTaskActive(SlaveTask task)
    {
        return TryGetActivePriorityTask(out var activeTask, out _) && activeTask == task;
    }

    private void UpdatePriorityTaskExternalStatus()
    {
        if (plugin.TaskRunner.IsRunning)
        {
            plugin.TaskRunner.ClearExternalStatus();
            return;
        }

        if (TryGetActivePriorityTask(out var activeTask, out _))
            plugin.TaskRunner.SetExternalStatus(GetPriorityTaskExternalStatusLabel(activeTask));
        else
            plugin.TaskRunner.ClearExternalStatus();
    }

    private void StopPriorityTask(SlaveTask task)
    {
        if (task == SlaveTask.AutoGlamWeather)
        {
            StopAutoGlamWeatherTask();
            return;
        }

        if (task == SlaveTask.Xagman)
        {
            StopXagmanTask();
            return;
        }

        if (task == SlaveTask.AutoAcceptFcInvite)
        {
            StopAutoAcceptFcInviteTask();
            return;
        }

        if (task == SlaveTask.RefreshArSubsBell)
            ReleaseRefreshSubsArSuppression();

        if (plugin.TaskRunner.IsRunning && TryMapPriorityTaskName(plugin.TaskRunner.CurrentTaskName, out var runningTask) && runningTask == task)
            plugin.TaskRunner.Cancel();

        UpdatePriorityTaskExternalStatus();
    }

    private static Vector4 GetPriorityTaskPulseColor()
    {
        var pulse = (MathF.Sin((float)ImGui.GetTime() * 3.25f) + 1f) * 0.5f;
        var colorScale = 1f - (0.6f * pulse);
        return new Vector4(colorScale, 1f, colorScale, 1f);
    }

    private float ClampTaskMenuWidth(float width)
    {
        return Math.Clamp(width <= 0f ? DefaultTaskMenuWidth : width, MinTaskMenuWidth, MaxTaskMenuWidth);
    }

    /// <summary>Renders a menu section with header and selectable items.</summary>
    private void DrawMenuSection(MenuSection section, string header, (SlaveTask Task, string Label)[] items, Vector4 headerColor)
    {
        ImGui.Spacing();
        var expanded = GetMenuSectionExpanded(section);
        ImGui.PushStyleColor(ImGuiCol.Text, headerColor);
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.0f, 0.0f, 0.0f, 0.0f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.0f, 0.0f, 0.0f, 0.0f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.0f, 0.0f, 0.0f, 0.0f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.25f, 0.25f, 0.25f, 1.0f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, Scale(1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Scale(4f));
        ImGui.SetNextItemOpen(expanded, ImGuiCond.Always);
        var isOpen = ImGui.CollapsingHeader($"{header}##{section}");
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(5);
        if (isOpen != expanded)
            SetMenuSectionExpanded(section, isOpen);
        if (!isOpen)
            return;

        foreach (var (task, label) in items)
        {
            if (!TryGetVisibleTaskLabel(label, out var visibleLabel))
                continue;

            var shouldPulse = ShouldPulseMenuTaskItem(task);
            if (shouldPulse)
                ImGui.PushStyleColor(ImGuiCol.Text, GetPriorityTaskPulseColor());

            var isSplashScreen = task == SlaveTask.SplashScreen;
            var isSelected = isSplashScreen
                ? selectedExternalTask == null && selectedTask == null
                : selectedExternalTask == null && selectedTask == task;
            if (ImGui.Selectable(visibleLabel, isSelected))
            {
                if (isSplashScreen)
                    ClearTaskSelection();
                else if (isSelected && ImGui.GetIO().KeyCtrl)
                    ClearTaskSelection();
                else
                    SelectBuiltInTask(task);
            }

            if (shouldPulse)
                ImGui.PopStyleColor();
        }
    }

    private bool ShouldPulseMenuTaskItem(SlaveTask task)
    {
        return task switch
        {
            SlaveTask.SaveToXaDatabase => IsSaveToXaDatabaseActive(),
            SlaveTask.AutoRetainerTasks => IsAutoRetainerHelperActive(),
            _ => IsPriorityTask(task) && IsTaskActive(task),
        };
    }

    private bool GetMenuSectionExpanded(MenuSection section)
    {
        return section switch
        {
            MenuSection.AutomatedTasks => plugin.Configuration.MenuAutomatedTasksExpanded,
            MenuSection.CityShenanigans => plugin.Configuration.MenuCityShenanigansExpanded,
            MenuSection.FcRelations => plugin.Configuration.MenuFcRelationsExpanded,
            MenuSection.Utility => plugin.Configuration.MenuUtilityExpanded,
            MenuSection.Reference => plugin.Configuration.MenuReferenceExpanded,
            _ => true,
        };
    }

    private void SetMenuSectionExpanded(MenuSection section, bool expanded)
    {
        switch (section)
        {
            case MenuSection.AutomatedTasks:
                if (plugin.Configuration.MenuAutomatedTasksExpanded == expanded) return;
                plugin.Configuration.MenuAutomatedTasksExpanded = expanded;
                break;
            case MenuSection.CityShenanigans:
                if (plugin.Configuration.MenuCityShenanigansExpanded == expanded) return;
                plugin.Configuration.MenuCityShenanigansExpanded = expanded;
                break;
            case MenuSection.FcRelations:
                if (plugin.Configuration.MenuFcRelationsExpanded == expanded) return;
                plugin.Configuration.MenuFcRelationsExpanded = expanded;
                break;
            case MenuSection.Utility:
                if (plugin.Configuration.MenuUtilityExpanded == expanded) return;
                plugin.Configuration.MenuUtilityExpanded = expanded;
                break;
            case MenuSection.Reference:
                if (plugin.Configuration.MenuReferenceExpanded == expanded) return;
                plugin.Configuration.MenuReferenceExpanded = expanded;
                break;
            default:
                return;
        }

        plugin.Configuration.Save();
    }

    private bool IsSaveToXaDatabaseActive()
    {
        return plugin.Configuration.AutoCollectOnLogin || autoCollectScheduledAt.HasValue || plugin.AutoCollector.IsRunning;
    }

    private bool IsAutoRetainerHelperActive()
    {
        return plugin.Configuration.ArShipExplorationBailoutEnabled
            || plugin.Configuration.ArPreProcessEnabled
            || plugin.Configuration.ArPostProcessEnabled
            || plugin.ArPostProcessor.IsRegistered
            || plugin.ArPostProcessor.IsRunning;
    }

    private void DrawDefaultLandingPage()
    {
        DrawDefaultLandingPageIcon();

        ImGui.PushTextWrapPos(0f);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.85f, 0.50f, 1.0f));
        ImGui.TextWrapped($"XA Slave v{PluginVersion}");
        ImGui.PopStyleColor();
        ImGui.Text("General information, support links, and setup guidance for XA Slave.");
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        if (ImGui.Button("https://Aethertek.io"))
            OpenExternalLink(WebsiteUrl);

        var remainingWidth = ImGui.GetContentRegionAvail().X;
        var discordButtonWidth = ImGui.CalcTextSize("Join Discord").X + (ImGui.GetStyle().FramePadding.X * 2f);
        if (remainingWidth >= discordButtonWidth + ImGui.GetStyle().ItemSpacing.X)
            ImGui.SameLine();
        if (ImGui.Button("Join Discord"))
            OpenExternalLink(DiscordUrl);

        var updatesButtonWidth = ImGui.CalcTextSize("Update History").X + (ImGui.GetStyle().FramePadding.X * 2f);
        if (ImGui.GetContentRegionAvail().X >= updatesButtonWidth + ImGui.GetStyle().ItemSpacing.X)
            ImGui.SameLine();
        if (ImGui.Button("Update History"))
            plugin.UpdatesWindow.Toggle();

        ImGui.Separator();
        ImGui.PushTextWrapPos(0f);
        ImGui.Text("XA Slave is still in a very early phase. Expect ongoing changes, new automation surfaces, and many months of additional features.");

        ImGui.Spacing();
        DrawWrappedDisabledText("General Terms");
        DrawWrappedBulletText("Treat every automation workflow as supervised until you trust the exact task and configuration you are running.");
        DrawWrappedBulletText("Enable Show Log when you need visibility, or enable Plugin Operations > Verbose Task Logging when reporting issues.");
        DrawWrappedBulletText("Make sure supporting plugins and character data are configured before running tasks that depend on AutoRetainer, XA Database, Lifestream, or Dropbox.");
        DrawWrappedBulletText("Use Logout on Complete or Enable AR Multi on Complete when you do not want characters left idle in game after a run.");

        ImGui.Spacing();
        DrawWrappedDisabledText("First Time");
        DrawWrappedBulletText("Import characters from AutoRetainer before expecting XA Slave task lists to populate cleanly.");
        DrawWrappedBulletText("Pull XA Database info before relying on matching/selection features that use stored inventory or character data.");
        DrawWrappedBulletText("Running Monthly Relogger across your roster first is the best way to sweep and normalize character data.");

        ImGui.Spacing();
        DrawWrappedDisabledText("Support");
        DrawWrappedBulletText("The website includes general plugin information and project-facing links.");
        DrawWrappedBulletText("The Discord server is the fastest route for reporting issues, feature requests, and feedback.");
        ImGui.PopTextWrapPos();
    }

    private static void DrawWrappedDisabledText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    private static void DrawWrappedBulletText(string text)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private void DrawDefaultLandingPageIcon()
    {
        var pluginDirectory = Plugin.PluginInterface.AssemblyLocation.DirectoryName;
        if (string.IsNullOrWhiteSpace(pluginDirectory))
            return;

        var imagePath = Path.Combine(pluginDirectory, LandingPageIconRelativePath);
        if (!File.Exists(imagePath))
            return;

        var sharedTexture = Plugin.TextureProvider.GetFromFile(imagePath);
        if (!sharedTexture.TryGetWrap(out var wrap, out _))
            return;

        var targetHeight = Scale(110f);
        var scale = targetHeight / MathF.Max(1f, (float)wrap.Height);
        var size = new Vector2(wrap.Width * scale, wrap.Height * scale);
        ImGui.Image(wrap.Handle, size);
        ImGui.Spacing();
    }

    private static void OpenExternalLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"[XASlave] Failed to open external link: {url}");
        }
    }

    private void DrawStatusBar()
    {
        ImGui.TextDisabled($"XA Slave v{PluginVersion}");
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.TextDisabled("/xa to toggle");

        if (plugin.AutoCollector.IsRunning)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), plugin.AutoCollector.StatusText);
        }

        if (plugin.TaskRunner.IsRunning)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            var taskLabel = TryMapPriorityTaskName(plugin.TaskRunner.CurrentTaskName, out var activeTask)
                ? GetPriorityTaskLabel(activeTask)
                : plugin.TaskRunner.CurrentTaskName;
            var label = plugin.TaskRunner.TotalItems > 0
                ? $"{taskLabel}: {plugin.TaskRunner.CompletedItems}/{plugin.TaskRunner.TotalItems}"
                : taskLabel;
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), label);
        }
        else if (TryGetActivePriorityTask(out var activePriorityTask, out _))
        {
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), GetPriorityTaskStatusLabel(activePriorityTask));
        }
    }
}
