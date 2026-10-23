using System;
using XASlave.Localization;
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
/// Main window for XA Slave - left-hand task menu with right-side content panel.
/// Tasks are automation jobs that interact with the game and push data to XA Database via IPC.
/// Partial class - task/panel UI split into Windows/Tasks/ and Windows/Panels/.
/// </summary>
public partial class SlaveWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private bool isDisposed;
    private readonly System.Threading.CancellationTokenSource workerCts = new();
    private int exclusiveWorkerRunning;
    private const string PluginVersion = BuildInfo.Version;
    private const string WebsiteUrl = "https://aethertek.io/";
    private const string DiscordUrl = "https://discord.gg/g2NmYxPQCa";
    private const string LandingPageIconRelativePath = "images\\icon.png";
    private const float DefaultTaskMenuWidth = 100f;
    private const float MinTaskMenuWidth = 30f;
    private const float MaxTaskMenuWidth = DefaultTaskMenuWidth * 2f;
    private const double FrameworkUpdateTimingDebugThresholdMilliseconds = 10.0;
    private const float TaskLayoutColumnGap = 3f;
    private static float UiScale => ImGuiHelpers.GlobalScale;
    private static float UiScaleSafe => ImGuiHelpers.GlobalScale;
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
        // Append task identities to preserve existing persisted enum values.
        // Field Operations
        EurekaInstanceHunter,
        EurekaLogogramCreator,
        // Utility
        WindowRenamer,
        PluginOperations,
        XAMods,
        // Reference
        DebugCommands,
        ExportData,
        RepoList,
        IpcCallsAvailable,
        CommandsReference,
        SplashScreen,
        PluginBuilder,
    }

    private enum MenuSection
    {
        AutomatedTasks,
        CityShenanigans,
        FcRelations,
        FieldOperations,
        Utility,
        Reference,
    }

    private static readonly (SlaveTask Task, string Label)[] TaskItems =
    {
        (SlaveTask.AutoRetainerTasks, UiLocale.Native("S83A8F587FFB9A4C2")),
        (SlaveTask.SaveToXaDatabase, UiLocale.Native("S0AED21970EED8844")),
    };

    private static readonly (SlaveTask Task, string Label)[] CityShenanigansItems =
    {
        (SlaveTask.AutoGlamWeather, UiLocale.Native("SEFBF04736214F41C")),
        (SlaveTask.CityChatFlooder, UiLocale.Native("S5C5514B66531FC2E")),
    };

    private static readonly (SlaveTask Task, string Label)[] FcItems =
    {
        (SlaveTask.Xagman, "Xagman"),
        (SlaveTask.MonthlyRelogger, UiLocale.Native("S4D398C5A9609297E")),
        (SlaveTask.PrepLogistics, UiLocale.Native("S2AE13753A8D90982")),
        (SlaveTask.AutoAcceptFcInvite, UiLocale.Native("S26AC3F3DFD044284")),
        (SlaveTask.MultiFcPermissions, UiLocale.Native("SA6711086459806CB")),
        (SlaveTask.CheckDuplicatePlots, UiLocale.Native("SA455BD4F4CF7E92B")),
        (SlaveTask.ReturnAltsToHomeworlds, UiLocale.Native("S99446740287A7C89")),
        (SlaveTask.RefreshArSubsBell, UiLocale.Native("S85CC8B46B386EB84")),
    };

    private static readonly (SlaveTask Task, string Label)[] FieldOperationsItems =
    {
        (SlaveTask.EurekaInstanceHunter, UiLocale.Native("S4B03ED11C88B1622")),
        (SlaveTask.EurekaLogogramCreator, UiLocale.Native("S4D24F60E78E1A812")),
    };

    private static readonly (SlaveTask Task, string Label)[] UtilityItems =
    {
        (SlaveTask.XAMods, "XA Mods"),
        (SlaveTask.WindowRenamer, UiLocale.Native("S05F3A5D5B4F5775C")),
        (SlaveTask.PluginOperations, UiLocale.Native("S7DD7E3FD85B28A2A")),
        (SlaveTask.PluginBuilder, UiLocale.Native("SA20967C40751B062")),
    };

    private static readonly (SlaveTask Task, string Label)[] ReferenceItems =
    {
#if DEBUG
        (SlaveTask.DebugCommands, UiLocale.Native("S5B74822E75E008EF")),
#endif
        (SlaveTask.ExportData, UiLocale.Native("S2ED227401538531A")),
        (SlaveTask.RepoList, UiLocale.Native("S273D7A925BD20F8A")),
        (SlaveTask.IpcCallsAvailable, UiLocale.Native("S03BD054B237A0FA2")),
        (SlaveTask.CommandsReference, UiLocale.Native("SB269DC4E81A528A4")),
        (SlaveTask.SplashScreen, UiLocale.Native("S86F4EE2A3639FB2B")),
    };

    private SlaveTask? selectedTask;
    private bool debugMenuVisible;
    private string debugResult = string.Empty;
    private global::XASlave.Localization.UiMessage? debugResultUiMessage;
    private DateTime debugResultExpiry = DateTime.MinValue;

    private ITaskPanel? selectedExternalTask;

    public SlaveWindow(Plugin plugin)
        : base("XA Slave###SlaveWindow", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
#if DEBUG
        debugMenuVisible = true;
#else
        debugMenuVisible = false;
#endif
        UpdateSizeConstraints(UiScaleSafe);
        RestoreLastSelectedTaskSelection();
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.ClientState.Login += OnExportDataLogin;
        Plugin.ClientState.Logout += OnExportDataLogoutHandler;
#if DEBUG
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

        // -- Resolution shortcuts (rightmost = added first) --
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
                    ImGui.TextUnformatted(UiLocale.FormatKey("S3339F918A7D9E893", w, h));
                    ImGui.TextDisabled(plugin.Configuration.CustomResolutionsEnabled
                        ? UiLocale.Get("S336CD00F920C09F7")
                        : UiLocale.Get("SB0CF66153D292FAB"));
                },
                Click = _ =>
                {
                    if (!plugin.SystemWindowMods.TryApplyCustomResolution(w, h, out var resMsg))
                        Plugin.ChatGui.PrintError($"[XASlave] {resMsg}");
                },
            });
            titleBarFavCustomColorResolvers[buttonIndex] = () => plugin.Configuration.CustomResolutionsEnabled ? FavIconColorOn : FavIconColorOff;
        }

        // -- Custom favourites --
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

        // -- AR Post-Process toggle --
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
                        ? UiLocale.Get("S3B8D40944683971B")
                        : UiLocale.Get("S1ED25BFBED04F52B"));
                },
                Click = _ =>
                {
                    plugin.Configuration.ArPostProcessEnabled = !plugin.Configuration.ArPostProcessEnabled;
                    plugin.Configuration.Save();
                },
            });
        }

        // -- AR Pre-Process toggle --
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
                        ? UiLocale.Get("SC78569009A8AA783")
                        : UiLocale.Get("SC4884A3C8A307CF5"));
                },
                Click = _ =>
                {
                    plugin.Configuration.ArPreProcessEnabled = !plugin.Configuration.ArPreProcessEnabled;
                    plugin.Configuration.Save();
                },
            });
        }

        // -- Auto-Glam Weather toggle --
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
                        ? UiLocale.Get("SC7C1792DB747BE71")
                        : UiLocale.Get("S5F7090986E98AEA5"));
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

        // -- Fav Mod List --
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
                    ImGui.TextUnformatted(UiLocale.FormatKey("S464CCCF62ECE75C8", listName));
                },
                Click = _ => { plugin.LoadModListPreset(listName, out var loadMsg); },
            });
        }

        // -- Disable All XA Mods --
        if (cfg.TitleBarFavDisableAllModsEnabled)
        {
            TitleBarButtons.Add(new TitleBarButton
            {
                Icon = FontAwesomeIcon.Recycle,
                IconOffset = TitleBarIconOffset,
                ShowTooltip = () =>
                {
                    using var tt = ImRaii.Tooltip();
                    ImGui.TextUnformatted(UiLocale.Get("S2B24BF8B797CBD75"));
                },
                Click = _ => plugin.DisableAllXAMods(),
            });
        }

        // -- Kill Game (requires Ctrl+Shift) --
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
                    if (!plugin.CanTriggerLogoutActionsMessage(out var blockedMessage))
                    {
                        ImGui.TextUnformatted(blockedMessage.Display);
                        return;
                    }

                    var held = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
                    if (held)
                        ImGui.TextUnformatted(UiLocale.Get("S8BBFA69C960F45CA"));
                    else
                        ImGui.TextUnformatted(UiLocale.Get("SF9C203CD2306F618"));
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
            options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.FromMenuTarget(menuLabel), menuLabel, UiLocale.Native("S456C3B74A97616BB")));

        foreach (var modInfo in plugin.GetTitleBarFavXAModInfos())
            options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.FromXAModKey(modInfo.Key), $"XA Mod: {modInfo.DisplayName}", "XA Mods"));

        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.SpecialRenderHideAddonsKeepNameplatesKey, UiLocale.Native("S6ECB725DD7371264"), UiLocale.Native("SB26660220C4D59AB")));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.SpecialRenderHideAddonsKeepChatKey, UiLocale.Native("S87AF1E4B608FFC77"), UiLocale.Native("SB26660220C4D59AB")));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.SpecialRenderHideChatKey, UiLocale.Native("S1ED9F9BF080A444B"), UiLocale.Native("SB26660220C4D59AB")));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.SpecialRenderHideActionBarsKey, UiLocale.Native("S0F9816C6DFF380D2"), UiLocale.Native("SB26660220C4D59AB")));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.SpecialRenderHideTargetInfoKey, UiLocale.Native("S6268111E849ED78F"), UiLocale.Native("SB26660220C4D59AB")));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.SpecialRenderHideNameplatesKey, UiLocale.Native("S1A218F4BA3356A8D"), UiLocale.Native("SB26660220C4D59AB")));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.SpecialRenderRestoreAllKey, UiLocale.Native("SA9C39CA5E456C9A7"), UiLocale.Native("SB26660220C4D59AB")));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.SitNowKey, UiLocale.Native("SC6017E42F83A1AEE"), UiLocale.Native("S024F9EF0DF86E54F")));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.DozeNowKey, UiLocale.Native("SAD122E8E9A11CBF5"), UiLocale.Native("S024F9EF0DF86E54F")));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.DisableAllXAModsKey, UiLocale.Native("S5F891DDBDE2EC40D"), UiLocale.Native("S2CC2B6F7F200E65C")));
        options.Add(new TitleBarFavSelectionOption(TitleBarFavSelectionKeys.StopAllKey, UiLocale.Native("S0716D61F057E2395"), UiLocale.Native("S2CC2B6F7F200E65C")));
        return options;
    }

    private string GetTitleBarFavSelectionLabel(string? selectionKey, string? legacyMenuTarget = null)
    {
        var normalizedSelectionKey = TitleBarFavSelectionKeys.Normalize(selectionKey, legacyMenuTarget);
        if (string.IsNullOrWhiteSpace(normalizedSelectionKey))
            return UiLocale.Get("S552E423045FCBD94");

        foreach (var option in GetTitleBarFavSelectionOptions())
        {
            if (option.Key.Equals(normalizedSelectionKey, StringComparison.OrdinalIgnoreCase))
                return GetLocalizedFavoriteLabel(option);
        }

        return normalizedSelectionKey;
    }

    private static string GetLocalizedFavoriteLabel(TitleBarFavSelectionOption option)
    {
        const string modPrefix = "XA Mod: ";
        return option.Category == "XA Mods" && option.Label.StartsWith(modPrefix, StringComparison.Ordinal)
            ? UiLocale.FormatKey("S60B3C83A05E52089", UiLocale.T(option.Label[modPrefix.Length..]))
            : UiLocale.T(option.Label);
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
                    ImGui.TextUnformatted(UiLocale.T(title));
                    if (subtitle.Length > 0)
                        ImGui.TextDisabled(UiLocale.T(subtitle));
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
                    ImGui.TextUnformatted(UiLocale.T(menuTarget));
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
                    ImGui.TextUnformatted($"{UiLocale.T(modInfo.DisplayName)} [{UiLocale.T(modInfo.ScopeLabel)}]");
                    ImGui.TextUnformatted(enabled ? UiLocale.Get("S259ED98F36260936") : UiLocale.Get("SC9D570E8531DCAF0"));
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
                UiLocale.Native("S560149D53269475F"),
                UiLocale.Native("S50AD2E4C44A54303"),
                () => ToggleSpecialRenderTitleBarSetting(
                    cfg => cfg.SpecialRenderHideAddonsKeepNameplatesEnabled,
                    (cfg, value) => cfg.SpecialRenderHideAddonsKeepNameplatesEnabled = value),
                () => IsSpecialRenderTitleBarToggleActive(cfg => cfg.SpecialRenderHideAddonsKeepNameplatesEnabled) ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.SpecialRenderHideAddonsKeepChatKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                UiLocale.Native("SB106BB941CC82200"),
                UiLocale.Native("S50AD2E4C44A54303"),
                () => ToggleSpecialRenderTitleBarSetting(
                    cfg => cfg.SpecialRenderHideAddonsKeepChatEnabled,
                    (cfg, value) => cfg.SpecialRenderHideAddonsKeepChatEnabled = value),
                () => IsSpecialRenderTitleBarToggleActive(cfg => cfg.SpecialRenderHideAddonsKeepChatEnabled) ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.SpecialRenderHideChatKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                UiLocale.Native("SB6533E35E8ABBD62"),
                UiLocale.Native("S50AD2E4C44A54303"),
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
                UiLocale.Native("SE3DA8F5412BFD89B"),
                UiLocale.Native("S50AD2E4C44A54303"),
                () => ToggleSpecialRenderTitleBarSetting(
                    cfg => cfg.SpecialRenderHideActionBarsEnabled,
                    (cfg, value) => cfg.SpecialRenderHideActionBarsEnabled = value),
                () => IsSpecialRenderTitleBarToggleActive(cfg => cfg.SpecialRenderHideActionBarsEnabled) ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.SpecialRenderHideTargetInfoKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                UiLocale.Native("S6E02900D6A36047A"),
                UiLocale.Native("S50AD2E4C44A54303"),
                () => ToggleSpecialRenderTitleBarSetting(
                    cfg => cfg.SpecialRenderHideTargetInfoEnabled,
                    (cfg, value) => cfg.SpecialRenderHideTargetInfoEnabled = value),
                () => IsSpecialRenderTitleBarToggleActive(cfg => cfg.SpecialRenderHideTargetInfoEnabled) ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.SpecialRenderHideNameplatesKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                UiLocale.Native("S807ADE6FB11DC534"),
                UiLocale.Native("S50AD2E4C44A54303"),
                () => ToggleSpecialRenderTitleBarSetting(
                    cfg => cfg.SpecialRenderHideNameplatesEnabled,
                    (cfg, value) => cfg.SpecialRenderHideNameplatesEnabled = value),
                () => IsSpecialRenderTitleBarToggleActive(cfg => cfg.SpecialRenderHideNameplatesEnabled) ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.SpecialRenderRestoreAllKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                UiLocale.Native("SF9FB14E71905DD48"),
                UiLocale.Native("S9D8B7431D11B154C"),
                RestoreAllSpecialRenderTitleBarSettings,
                () => plugin.Configuration.SpecialRenderModesEnabled || HasAnySpecialRenderStoredUiToggles() ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.SitNowKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                UiLocale.Native("SC6017E42F83A1AEE"),
                UiLocale.Native("S930B2432626B7102"),
                () => plugin.DozeSitAnywhere.RequestSit(),
                () => plugin.Configuration.DozeSitAnywhereEnabled ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.DozeNowKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                UiLocale.Native("SAD122E8E9A11CBF5"),
                UiLocale.Native("S930B2432626B7102"),
                () => plugin.DozeSitAnywhere.RequestDoze(),
                () => plugin.Configuration.DozeSitAnywhereEnabled ? FavIconColorOn : FavIconColorOff);
        }

        if (selectionKey.Equals(TitleBarFavSelectionKeys.DisableAllXAModsKey, StringComparison.OrdinalIgnoreCase))
        {
            return AddQuickActionButton(
                UiLocale.Native("S5F891DDBDE2EC40D"),
                UiLocale.Native("S4C4A2A1248AEA3CE"),
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
                UiLocale.Native("S0716D61F057E2395"),
                UiLocale.Native("S68B0A2D885D457FA"),
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
            || xagmanTradeSafetySessionActive
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

        if (xagmanRunning || xagmanTradeSafetySessionActive)
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

        foreach (var (task, label) in FieldOperationsItems)
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
            if (!TryGetVisibleTaskLabel(task, label, out var visibleLabel))
                continue;

            if (string.Equals(visibleLabel, menuLabel, StringComparison.OrdinalIgnoreCase))
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

    public void OpenEurekaInstanceHunterTask()
    {
        IsOpen = true;
        SelectBuiltInTask(SlaveTask.EurekaInstanceHunter);
    }

    public void OpenCommandsReferenceTask()
    {
        IsOpen = true;
        SelectBuiltInTask(SlaveTask.CommandsReference);
    }

    public bool ToggleDebugMenu(out string message) => ToggleDebugMenuLocalizedCore(out _, out message);
    internal bool ToggleDebugMenuMessage(out global::XASlave.Localization.UiMessage message)
    {
        var success = ToggleDebugMenuLocalizedCore(out var presentation, out var originalReply);
        message = global::XASlave.Localization.UiMessage.Capture(presentation, originalReply);
        return success;
    }

    private bool ToggleDebugMenuLocalizedCore(out global::XASlave.Localization.UiMessage? replyPresentation, out string message)
    {
        replyPresentation = null;
#if !DEBUG
        debugMenuVisible = false;
        if (plugin.Configuration.DebugMenuVisible)
        {
            plugin.Configuration.DebugMenuVisible = false;
            plugin.Configuration.SaveDeferred();
        }

        message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S21B510C0C190BDEB"));
        return false;
#else
        debugMenuVisible = !debugMenuVisible;
        if (plugin.Configuration.DebugMenuVisible != debugMenuVisible)
        {
            plugin.Configuration.DebugMenuVisible = debugMenuVisible;
            plugin.Configuration.Save();
        }

        if (debugMenuVisible)
        {
            IsOpen = true;
            SetMenuSectionExpanded(MenuSection.Reference, true);
            SelectBuiltInTask(SlaveTask.DebugCommands);
            message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S66341C96B4C32540"));
            return true;
        }

        if (selectedTask == SlaveTask.DebugCommands)
            ClearTaskSelection();

        message = global::XASlave.Localization.UiMessage.Store(ref replyPresentation, global::XASlave.Localization.UiMessage.Key("S506C365B678AF61F"));
        return true;
#endif
    }

    private void SetDebugResult(global::XASlave.Localization.UiMessage msg)
    {
        debugResult = global::XASlave.Localization.UiMessage.Store(ref debugResultUiMessage, global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Raw($"[{DateTime.Now:HH:mm:ss}] "), msg));
        debugResultExpiry = DateTime.UtcNow.AddSeconds(15);
        Plugin.Log.Information($"[XASlave] Debug: {msg}");
    }

    public void Dispose()
    {
        if (isDisposed)
            return;

        isDisposed = true;

        DetachDuringDispose("Worker cancellation", workerCts.Cancel);
        DetachDuringDispose("Plugin Builder windows", DisposePluginBuilder);

        DetachDuringDispose("Framework.Update", () => Plugin.Framework.Update -= OnFrameworkUpdate);
        DetachDuringDispose("ClientState.Login", () => Plugin.ClientState.Login -= OnExportDataLogin);
        DetachDuringDispose("ClientState.Logout", () => Plugin.ClientState.Logout -= OnExportDataLogoutHandler);
#if DEBUG
        DetachDuringDispose(UiLocale.Native("SE9D93BD5231580C6"), DisposePluginInspector);
        DetachDuringDispose("NamePlateGui.OnDataUpdate", () => Plugin.NamePlateGui.OnDataUpdate -= OnXaAbuseNamePlateUpdate);
        DetachDuringDispose("UiBuilder.Draw", () => Plugin.PluginInterface.UiBuilder.Draw -= DrawXaAbuseOverlay);
        DetachDuringDispose("AR RetainerListTaskButtonsDraw", () => plugin.IpcClient.AutoRetainerUnsubscribeRetainerListTaskButtonsDraw(OnDebugAutoRetainerDepositGilListTaskButtonsDraw));
        DetachDuringDispose("AR RetainerAdditionalTask", () => plugin.IpcClient.AutoRetainerUnsubscribeRetainerAdditionalTask(OnDebugAutoRetainerDepositGilAdditionalTask));
        DetachDuringDispose("AR RetainerPostProcess", () => plugin.IpcClient.AutoRetainerUnsubscribeRetainerPostProcess(OnDebugAutoRetainerDepositGilReadyForPostprocess));
        debugAutoRetainerDepositGilSubscribed = false;
#endif

        DetachDuringDispose("CancelScheduledAutoCollection", () => CancelScheduledAutoCollection(true));
        DetachDuringDispose("ReleaseRefreshSubsArSuppression", ReleaseRefreshSubsArSuppression);
        DetachDuringDispose("StopXagmanTask", StopXagmanTask);

#if DEBUG
        xaAbuseEnabled = false;
        DetachDuringDispose("NamePlateGui.RequestRedraw", Plugin.NamePlateGui.RequestRedraw);
#endif
        DetachDuringDispose("Worker cancellation source", workerCts.Dispose);
    }

    /// <summary>
    /// Owns every window-scoped background worker so unload cancels it, faults are observed, and
    /// movement-capable workers can share one re-entrancy gate.
    /// </summary>
    private bool RunWorker(
        string label,
        Func<System.Threading.CancellationToken, System.Threading.Tasks.Task> body,
        bool exclusive = false)
    {
        if (isDisposed || workerCts.IsCancellationRequested)
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("SF37458C796DC49DB", label));
            return false;
        }

        if (exclusive && System.Threading.Interlocked.CompareExchange(ref exclusiveWorkerRunning, 1, 0) != 0)
        {
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S905A12A28BF0E892", label));
            return false;
        }

        var token = workerCts.Token;
        try
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await body(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Expected during window/plugin teardown.
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, $"[XASlave] Worker '{label}' failed.");
                    SetDebugResult(global::XASlave.Localization.UiMessage.Format("S59C8945497B012FC", label, ex.Message));
                }
                finally
                {
                    if (exclusive)
                        System.Threading.Interlocked.Exchange(ref exclusiveWorkerRunning, 0);
                }
            }, System.Threading.CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            if (exclusive)
                System.Threading.Interlocked.Exchange(ref exclusiveWorkerRunning, 0);
            Plugin.Log.Error(ex, $"[XASlave] Could not start worker '{label}'.");
            SetDebugResult(global::XASlave.Localization.UiMessage.Format("S6712320D407C9581", label, ex.Message));
            return false;
        }
    }

    private bool RejectIfExclusiveWorkerBusy(string label)
    {
        if (System.Threading.Volatile.Read(ref exclusiveWorkerRunning) == 0)
            return false;

        SetDebugResult(global::XASlave.Localization.UiMessage.Format("S905A12A28BF0E892", label));
        return true;
    }

    private static void DetachDuringDispose(string label, System.Action action)
    {
        try { action(); }
        catch (Exception ex) { Plugin.Log.Warning(ex, $"[XASlave] SlaveWindow.Dispose: {label} failed."); }
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

    private static string FormatCheckEveryHours(int hours) => FormatCheckEveryHoursMessage(hours).Original;

    private static global::XASlave.Localization.UiMessage FormatCheckEveryHoursMessage(int hours)
    {
        var normalized = NormalizeCheckEveryHours(hours);
        if (normalized == 0)
            return global::XASlave.Localization.UiMessage.Key("SDE9F057A471CDB8D");

        if (normalized % 24 == 0)
        {
            var days = normalized / 24;
            return (days == 1 ? global::XASlave.Localization.UiMessage.Key("SFA665D95D29E83BC") : global::XASlave.Localization.UiMessage.Format("SAF78BEB0F18B8974", days));
        }

        return (normalized == 1 ? global::XASlave.Localization.UiMessage.Key("SF8B8883F0C9D8D82") : global::XASlave.Localization.UiMessage.Format("S4D0AA096B452CC31", normalized));
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
        autoCollectSkipMessage = global::XASlave.Localization.UiMessage.Store(ref autoCollectSkipMessageUiMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));

        if (arMultiEnabled)
            plugin.IpcClient.AutoRetainerSetSuppressed(true);

        if (!plugin.IsCurrentCharacterSyncDue(cadenceHours))
        {
            autoCollectSkipPending = true;
            autoCollectScheduledDelaySeconds = arMultiEnabled ? 1f : 0f;
            autoCollectSkipMessage = global::XASlave.Localization.UiMessage.Store(ref autoCollectSkipMessageUiMessage, global::XASlave.Localization.UiMessage.Format("S4E41A36F22D8C565", FormatCheckEveryHoursMessage(cadenceHours)));
        }

        if (arMultiEnabled && plugin.Configuration.AutoCollectDisableWhenArMultiEnabled)
        {
            autoCollectSkipPending = true;
            autoCollectScheduledDelaySeconds = 1f;
            autoCollectSkipMessage = global::XASlave.Localization.UiMessage.Store(ref autoCollectSkipMessageUiMessage, global::XASlave.Localization.UiMessage.Key("S76F60F72B081DB4F"));
        }

        Plugin.Log.Information($"[XASlave] Auto-collection scheduled (delay {autoCollectScheduledDelaySeconds}s, arMulti={arMultiEnabled}, skipPending={autoCollectSkipPending}).");
    }

    public void CancelScheduledAutoCollection(bool resumeAr)
    {
        var shouldResumeAr = resumeAr && autoCollectResumeArOnCompletion;
        autoCollectScheduledAt = null;
        autoCollectScheduledDelaySeconds = 0f;
        autoCollectSkipPending = false;
        autoCollectSkipMessage = global::XASlave.Localization.UiMessage.Store(ref autoCollectSkipMessageUiMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
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
            SetIpcResult(global::XASlave.Localization.UiMessage.Format("SF90F01FC5D5CA135", taskLabel));
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (isDisposed) return;
        UpdatePluginBuilderWindows();
#if DEBUG
        UpdateDebugNotificationTests();
#endif
        var totalStopwatch = plugin.Configuration.VerboseTaskLogging
            ? Stopwatch.StartNew()
            : null;
        MeasureFrameworkUpdateStep("SlaveWindow.EnforceSpecialRenderSafety", plugin.EnforceSpecialRenderSafetyOnFrameworkTick);
        MeasureFrameworkUpdateStep("SlaveWindow.UpdateAutoGlamForecast", UpdateAutoGlamForecast);
        MeasureFrameworkUpdateStep("SlaveWindow.UpdatePriorityTaskMonitors", UpdatePriorityTaskMonitors);
        MeasureFrameworkUpdateStep("SlaveWindow.UpdatePriorityTaskExternalStatus", UpdatePriorityTaskExternalStatus);
        MeasureFrameworkUpdateStep("SlaveWindow.OnExportDataFrameworkTick", OnExportDataFrameworkTick);
        MeasureFrameworkUpdateStep("SlaveWindow.UpdateXagmanFrameworkTick", UpdateXagmanFrameworkTick);

        if (!autoCollectScheduledAt.HasValue || !Plugin.PlayerState.IsLoaded || plugin.AutoCollector.IsRunning)
        {
            CompleteFrameworkUpdateTiming(totalStopwatch);
            return;
        }

        var delay = (float)(DateTime.UtcNow - autoCollectScheduledAt.Value).TotalSeconds;
        if (delay < autoCollectScheduledDelaySeconds || !plugin.AutoCollector.IsNormalCondition())
        {
            CompleteFrameworkUpdateTiming(totalStopwatch);
            return;
        }

        autoCollectScheduledAt = null;

        if (autoCollectSkipPending)
        {
            if (!string.IsNullOrWhiteSpace(autoCollectSkipMessage))
                SetIpcResult(global::XASlave.Localization.UiMessage.Capture(ref autoCollectSkipMessageUiMessage, autoCollectSkipMessage));

            if (autoCollectResumeArOnCompletion)
                plugin.IpcClient.AutoRetainerSetSuppressed(false);

            autoCollectScheduledDelaySeconds = 0f;
            autoCollectSkipPending = false;
            autoCollectSkipMessage = global::XASlave.Localization.UiMessage.Store(ref autoCollectSkipMessageUiMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
            autoCollectResumeArOnCompletion = false;
            CompleteFrameworkUpdateTiming(totalStopwatch);
            return;
        }

        var resumeArAfterCollection = autoCollectResumeArOnCompletion;
        autoCollectScheduledDelaySeconds = 0f;
        autoCollectSkipPending = false;
        autoCollectSkipMessage = global::XASlave.Localization.UiMessage.Store(ref autoCollectSkipMessageUiMessage, global::XASlave.Localization.UiMessage.Raw(string.Empty));
        autoCollectResumeArOnCompletion = false;
        MeasureFrameworkUpdateStep("SlaveWindow.RunAutoCollection", () => RunAutoCollection(resumeArAfterCollection));
        CompleteFrameworkUpdateTiming(totalStopwatch);
    }

    private void CompleteFrameworkUpdateTiming(Stopwatch? stopwatch)
    {
        if (stopwatch == null)
            return;

        stopwatch.Stop();
        LogFrameworkUpdateStepDuration("SlaveWindow.OnFrameworkUpdate", stopwatch.Elapsed.TotalMilliseconds);
    }

    private void MeasureFrameworkUpdateStep(string label, System.Action action)
    {
        if (!plugin.Configuration.VerboseTaskLogging)
        {
            action();
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        action();
        stopwatch.Stop();
        LogFrameworkUpdateStepDuration(label, stopwatch.Elapsed.TotalMilliseconds);
    }

    private void LogFrameworkUpdateStepDuration(string label, double elapsedMilliseconds)
    {
        if (!plugin.Configuration.VerboseTaskLogging
            || elapsedMilliseconds < FrameworkUpdateTimingDebugThresholdMilliseconds)
            return;

        Plugin.Log.Debug($"[XASlave] Framework tick step '{label}' took {elapsedMilliseconds:F1}ms.");
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
        Plugin.AssertGameThread();
        RefreshTitleBarFavIconColors();

        // -- Left panel: Task menu --
        var storedLeftWidth = ClampTaskMenuWidth(plugin.Configuration.TaskMenuWidth);
        if (Math.Abs(storedLeftWidth - plugin.Configuration.TaskMenuWidth) > 0.01f)
        {
            plugin.Configuration.TaskMenuWidth = storedLeftWidth;
            plugin.Configuration.Save();
        }
        var leftWidth = Scale(storedLeftWidth);

        var panelHeight = Math.Max(Scale(120f), ImGui.GetContentRegionAvail().Y - Scale(30f));
        {
            using var padding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, ScaledVector(TaskLayoutColumnGap, 0f));
            using var colors = ImRaii.PushColor(ImGuiCol.Separator, new Vector4(0.25f, 0.25f, 0.25f, 1.0f));
            colors.Push(ImGuiCol.SeparatorHovered, new Vector4(0.40f, 0.40f, 0.40f, 1.0f));
            colors.Push(ImGuiCol.SeparatorActive, new Vector4(0.55f, 0.55f, 0.55f, 1.0f));
            using var table = ImRaii.Table("SlaveLayout", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.NoSavedSettings, new Vector2(0f, panelHeight));
            if (table)
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
            }
        }

        // -- Status bar --
        ImGui.Separator();
        DrawStatusBar();
    }

    private void DrawTaskMenuPanel(float panelHeight)
    {
        using var border = ImRaii.PushColor(ImGuiCol.Border, new Vector4(0.25f, 0.25f, 0.25f, 1.0f));
        using (var child = ImRaii.Child("TaskMenu", new Vector2(0f, panelHeight), true))
        {
            if (child.Success)
            {
                DrawMenuSection(MenuSection.AutomatedTasks, UiLocale.Native("SFCA0F574403E0669"), TaskItems, new Vector4(0.4f, 0.8f, 1.0f, 1.0f));
                DrawMenuSection(MenuSection.CityShenanigans, UiLocale.Native("SB7AB3A8FEC4F7EEC"), CityShenanigansItems, new Vector4(1.0f, 0.7f, 0.4f, 1.0f));
                DrawMenuSection(MenuSection.FcRelations, UiLocale.Native("S8B64706A3D3FA23E"), FcItems, new Vector4(0.8f, 0.6f, 1.0f, 1.0f));
                DrawMenuSection(MenuSection.FieldOperations, UiLocale.Native("S9820D743FD29145C"), FieldOperationsItems, new Vector4(0.8f, 1.0f, 0.55f, 1.0f));
                DrawMenuSection(MenuSection.Utility, UiLocale.Native("S9450D833FA7707C7"), UtilityItems, new Vector4(0.6f, 1.0f, 0.6f, 1.0f));

                foreach (var ext in plugin.ExternalTaskLoader.Tasks)
                {
                    string label;
                    try
                    {
                        label = ext.Label;
                    }
                    catch (Exception ex)
                    {
                        ImGui.TextColored(
                            new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
                            UiLocale.FormatKey("S5D7FB8F7FF81E3FC", ex.Message));
                        continue;
                    }
                    if (!TryGetVisibleTaskLabel(label, out var visibleLabel))
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

                DrawMenuSection(MenuSection.Reference, UiLocale.Native("S71BF90935FC231D2"), ReferenceItems, new Vector4(0.6f, 0.6f, 0.6f, 1.0f));
            }
        }
    }

    private void DrawTaskContentPanel(float panelHeight)
    {
        using var border = ImRaii.PushColor(ImGuiCol.Border, new Vector4(0.25f, 0.25f, 0.25f, 1.0f));
        using (var child = ImRaii.Child("TaskContent", new Vector2(0f, panelHeight), true))
        {
            if (child.Success)
            {
                if (selectedExternalTask != null)
                {
                    try
                    {
                        using var externalFont = UiLocaleFonts.PreserveExternalFont();
                        Plugin.RunOnGameThread(selectedExternalTask.Draw)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (Exception ex)
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), UiLocale.FormatKey("S43F78154E1D78FBB", ex.Message));
                    }
                }
                else
                {
                    try
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
                            case SlaveTask.EurekaInstanceHunter:
                                DrawEurekaInstanceHunterTask();
                                break;
                            case SlaveTask.EurekaLogogramCreator:
                                plugin.EurekaLogogramCreator.EnsureDataLoaded();
                                DrawEurekaLogogramCreatorTask();
                                break;
                            case SlaveTask.WindowRenamer:
                                DrawWindowRenamerTask();
                                break;
                            case SlaveTask.PluginOperations:
                                DrawPluginOperationsTask();
                                break;
                            case SlaveTask.PluginBuilder:
                                DrawPluginBuilderPanel();
                                break;
                            case SlaveTask.XAMods:
                                DrawXAModsTask();
                                break;
                            case SlaveTask.DebugCommands:
#if DEBUG
                                DrawDebugCommands();
#endif
                                break;
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
                    catch (Exception ex)
                    {
                        Plugin.Log.Error(ex, $"[XASlave] Panel '{selectedTask}' threw during Draw.");
                        ImGui.TextColored(
                            new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
                            UiLocale.FormatKey("S3DA15639D209F5BE", ex.Message));
                    }
                }
            }
        }
    }

    // -----------------------------------------------
    //  Status Bar
    // -----------------------------------------------
    private bool TryGetVisibleTaskLabel(SlaveTask task, string rawLabel, out string visibleLabel)
    {
        if (task == SlaveTask.DebugCommands && !debugMenuVisible)
        {
            visibleLabel = string.Empty;
            return false;
        }

        return TryGetVisibleTaskLabel(rawLabel, out visibleLabel);
    }

    private bool TryGetVisibleTaskLabel(string rawLabel, out string visibleLabel)
    {
        const string testingMarker = "[IF=Testing]";
        if (rawLabel.StartsWith(testingMarker, StringComparison.OrdinalIgnoreCase))
        {
            if (!debugMenuVisible)
            {
                visibleLabel = string.Empty;
                return false;
            }

            visibleLabel = rawLabel.Substring(testingMarker.Length).TrimStart();
            return true;
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
        => GetPriorityTaskLabelMessage(task).Original;

    private static global::XASlave.Localization.UiMessage GetPriorityTaskLabelMessage(SlaveTask task)
    {
        return task switch
        {
            SlaveTask.CityChatFlooder => global::XASlave.Localization.UiMessage.Key("S5C5514B66531FC2E"),
            SlaveTask.AutoGlamWeather => global::XASlave.Localization.UiMessage.Key("SEFBF04736214F41C"),
            SlaveTask.MonthlyRelogger => global::XASlave.Localization.UiMessage.Key("S4D398C5A9609297E"),
            SlaveTask.CheckDuplicatePlots => global::XASlave.Localization.UiMessage.Key("SA455BD4F4CF7E92B"),
            SlaveTask.Xagman => global::XASlave.Localization.UiMessage.Key("S0A1690A11A18319C"),
            SlaveTask.ReturnAltsToHomeworlds => global::XASlave.Localization.UiMessage.Key("S99446740287A7C89"),
            SlaveTask.PrepLogistics => global::XASlave.Localization.UiMessage.Key("S2AE13753A8D90982"),
            SlaveTask.RefreshArSubsBell => global::XASlave.Localization.UiMessage.Key("S85CC8B46B386EB84"),
            SlaveTask.MultiFcPermissions => global::XASlave.Localization.UiMessage.Key("SA6711086459806CB"),
            SlaveTask.AutoAcceptFcInvite => global::XASlave.Localization.UiMessage.Key("S26AC3F3DFD044284"),
            _ => global::XASlave.Localization.UiMessage.Raw(string.Empty),
        };
    }

    private static global::XASlave.Localization.UiMessage GetPriorityTaskDtrLabel(SlaveTask task)
    {
        return task switch
        {
            SlaveTask.AutoGlamWeather => global::XASlave.Localization.UiMessage.Key("SA23D0405BB489334"),
            _ => GetPriorityTaskLabelMessage(task),
        };
    }

    private global::XASlave.Localization.UiMessage GetPriorityTaskStatusLabel(SlaveTask task)
    {
        return task switch
        {
            SlaveTask.Xagman => GetXagmanPriorityStatusLabel(),
            _ => GetPriorityTaskLabelMessage(task),
        };
    }

    private global::XASlave.Localization.UiMessage GetPriorityTaskExternalStatusLabel(SlaveTask task)
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

        if (xagmanRunning || xagmanTradeSafetySessionActive)
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
        bool isOpen;
        using (var colors = ImRaii.PushColor(ImGuiCol.Text, headerColor))
        using (var styles = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Scale(1f)))
        {
            colors.Push(ImGuiCol.Header, new Vector4(0.0f, 0.0f, 0.0f, 0.0f));
            colors.Push(ImGuiCol.HeaderHovered, new Vector4(0.0f, 0.0f, 0.0f, 0.0f));
            colors.Push(ImGuiCol.HeaderActive, new Vector4(0.0f, 0.0f, 0.0f, 0.0f));
            colors.Push(ImGuiCol.Border, new Vector4(0.25f, 0.25f, 0.25f, 1.0f));
            styles.Push(ImGuiStyleVar.FrameRounding, Scale(4f));
            ImGui.SetNextItemOpen(expanded, ImGuiCond.Always);
            isOpen = UiNative.CollapsingHeader($"{header}##{section}");
        }
        if (isOpen != expanded)
            SetMenuSectionExpanded(section, isOpen);
        if (!isOpen)
            return;

        foreach (var (task, label) in items)
        {
            if (!TryGetVisibleTaskLabel(task, label, out var visibleLabel))
                continue;

            var shouldPulse = ShouldPulseMenuTaskItem(task);
            using (ImRaii.PushColor(ImGuiCol.Text, GetPriorityTaskPulseColor(), shouldPulse))
            {
                var isSplashScreen = task == SlaveTask.SplashScreen;
                var isSelected = isSplashScreen
                    ? selectedExternalTask == null && selectedTask == null
                    : selectedExternalTask == null && selectedTask == task;
                if (UiNative.Selectable(visibleLabel, isSelected))
                {
                    if (isSplashScreen)
                        ClearTaskSelection();
                    else if (isSelected && ImGui.GetIO().KeyCtrl)
                        ClearTaskSelection();
                    else
                        SelectBuiltInTask(task);
                }
            }
        }
    }

    private bool ShouldPulseMenuTaskItem(SlaveTask task)
    {
        return task switch
        {
            SlaveTask.SaveToXaDatabase => IsSaveToXaDatabaseActive(),
            SlaveTask.AutoRetainerTasks => IsAutoRetainerHelperActive(),
            SlaveTask.EurekaInstanceHunter => plugin.EurekaInstanceId.IsScanning,
            SlaveTask.EurekaLogogramCreator => plugin.EurekaLogogramCreator.HasActiveOrQueuedAutoLogoAction,
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
            MenuSection.FieldOperations => plugin.Configuration.MenuFieldOperationsExpanded,
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
            case MenuSection.FieldOperations:
                if (plugin.Configuration.MenuFieldOperationsExpanded == expanded) return;
                plugin.Configuration.MenuFieldOperationsExpanded = expanded;
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

        using (ImRaii.TextWrapPos(0f))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.95f, 0.85f, 0.50f, 1.0f)))
                ImGui.TextWrapped(UiLocale.FormatKey("SF01C495E563A9095", PluginVersion));
            ImGui.Text(UiLocale.Get("S0F3ABD2B158D71AC"));
        }

        ImGui.Spacing();
        if (ImGui.Button("https://Aethertek.io"))
            OpenExternalLink(WebsiteUrl);

        var remainingWidth = ImGui.GetContentRegionAvail().X;
        var discordButtonWidth = ImGui.CalcTextSize(UiLocale.Get("S62D97F7509105F04")).X + (ImGui.GetStyle().FramePadding.X * 2f);
        if (remainingWidth >= discordButtonWidth + ImGui.GetStyle().ItemSpacing.X)
            ImGui.SameLine();
        if (UiNative.Button(UiLocale.Native("S62D97F7509105F04")))
            OpenExternalLink(DiscordUrl);

        var updatesButtonWidth = ImGui.CalcTextSize(UiLocale.Get("S77439943B6C3E9FD")).X + (ImGui.GetStyle().FramePadding.X * 2f);
        if (ImGui.GetContentRegionAvail().X >= updatesButtonWidth + ImGui.GetStyle().ItemSpacing.X)
            ImGui.SameLine();
        if (UiNative.Button(UiLocale.Native("S77439943B6C3E9FD")))
            plugin.UpdatesWindow.Toggle();

        var languageWidth = UiLocale.LanguageSelectorWidth(plugin.Configuration);
        var rowRight = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + languageWidth <= rowRight)
            ImGui.SameLine();
        UiLocale.DrawLanguageSelector("SplashLanguage", plugin.Configuration);

        ImGui.Separator();
        using (ImRaii.TextWrapPos(0f))
        {
            ImGui.Text(UiLocale.Get("S6CC66C7BF00B3E49"));

            ImGui.Spacing();
            DrawWrappedDisabledText(UiLocale.Native("S77A928E2B344C600"));
            DrawWrappedBulletText(UiLocale.Native("S3A98D2D8FA09EFB0"));
            DrawWrappedBulletText(UiLocale.Native("SC82AEBD5662B7DCD"));
            DrawWrappedBulletText(UiLocale.Native("SAE0CA6F0E0BC0B78"));
            DrawWrappedBulletText(UiLocale.Native("S28E089F6D3E25906"));

            ImGui.Spacing();
            DrawWrappedDisabledText(UiLocale.Native("S179E0374A95838FB"));
            DrawWrappedBulletText(UiLocale.Native("SD982A681DA958B7A"));
            DrawWrappedBulletText(UiLocale.Native("S4127C75A38C14D46"));
            DrawWrappedBulletText(UiLocale.Native("S3C9AFFB127664BCB"));

            ImGui.Spacing();
            DrawWrappedDisabledText(UiLocale.Native("SBE91940B79F46910"));
            DrawWrappedBulletText(UiLocale.Native("S19141D6CBBF42CC9"));
            DrawWrappedBulletText(UiLocale.Native("S6CC0677A06905CF9"));
        }
    }

    private static void DrawWrappedDisabledText(string text)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]))
        using (ImRaii.TextWrapPos(0f))
            ImGui.TextUnformatted(UiLocale.T(text));
    }

    private static void DrawWrappedBulletText(string text)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        using (ImRaii.TextWrapPos(0f))
            ImGui.TextUnformatted(UiLocale.T(text));
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
        ImGui.TextDisabled(UiLocale.FormatKey("SF01C495E563A9095", PluginVersion));
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.TextDisabled(UiLocale.Get("S9E55BBBD3FBADB6B"));

        if (plugin.AutoCollector.IsRunning)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), plugin.AutoCollector.LocalizedStatusText);
        }

        if (plugin.TaskRunner.IsRunning)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            var taskLabel = TryMapPriorityTaskName(plugin.TaskRunner.CurrentTaskName, out var activeTask)
                ? UiLocale.T(GetPriorityTaskLabel(activeTask))
                : plugin.TaskRunner.CurrentTaskNameDisplay;
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
            ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.3f, 1.0f), GetPriorityTaskStatusLabel(activePriorityTask).Display);
        }
        else if (plugin.EurekaInstanceId.IsScanning)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.35f, 1.0f, 0.45f, 1.0f), UiLocale.Get("SCA0CA696037588CF"));
        }
    }
}
