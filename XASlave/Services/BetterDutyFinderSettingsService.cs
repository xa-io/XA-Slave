using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Config;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace XASlave.Services;

public unsafe sealed class BetterDutyFinderSettingsService : IDisposable
{
    private const string SetContentsFinderSettingsInitSignature =
        "E8 ?? ?? ?? ?? 49 8B 06 45 33 FF 49 8B CE 45 89 7E 20 FF 50 28 B0 01";
    private const string ContentsFinderAddonName = "ContentsFinder";
    private const string RaidFinderAddonName = "RaidFinder";
    private const float OverlayRaisedOffset = 28f;
    private const float OverlayButtonHeight = 24f;
    private const float OverlayButtonRounding = 4f;
    private const float OverlayButtonSpacing = 2f;
    private const float OverlayLanguageButtonWidth = 36f;
    private const float OverlayToggleButtonWidth = 76f;
    private const float OverlayLootButtonWidth = 70f;
    private const int OverlayRowCount = 2;

    private readonly IAddonLifecycle addonLifecycle;
    private readonly ISigScanner sigScanner;
    private readonly IGameConfig gameConfig;
    private readonly IPluginLog log;
    private SetContentsFinderSettingsInitDelegate? setContentsFinderSettingsInit;
    private bool enabled;
    private bool subscribed;
    private nint contentsFinderAddress;
    private nint raidFinderAddress;

    public BetterDutyFinderSettingsService(
        IAddonLifecycle addonLifecycle,
        ISigScanner sigScanner,
        IGameConfig gameConfig,
        IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.sigScanner = sigScanner;
        this.gameConfig = gameConfig;
        this.log = log;
    }

    public string StatusText { get; private set; } = "Disabled";
    public string LastActionText { get; private set; } = "No actions yet.";
    public bool IsReady => EnsureInitializer();

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            Unsubscribe();
            ClearTrackedAddons();
            StatusText = "Disabled";
            return false;
        }

        if (!EnsureInitializer())
            return false;

        enabled = true;
        Subscribe();
        RefreshTrackedAddons();
        StatusText = "Enabled - ContentsFinder and RaidFinder expose lifecycle-tracked duty-finder setting buttons inline.";
        return true;
    }

    public void Dispose()
    {
        enabled = false;
        Unsubscribe();
        ClearTrackedAddons();
    }

    public void DrawOverlay()
    {
        if (!enabled)
            return;

        DrawOverlayForAddon(ContentsFinderAddonName, ContentsFinderAddonName);
        DrawOverlayForAddon(RaidFinderAddonName, RaidFinderAddonName);
    }

    public bool TryApplyRegistrationMode(RegistrationMode mode)
    {
        if (mode == RegistrationMode.Default)
            return true;

        return ApplySettings(
            mode switch
            {
                RegistrationMode.Unrestricted => array =>
                {
                    array[(int)DutyFinderSetting.UnrestrictedParty] = 1;
                    array[(int)DutyFinderSetting.ExplorerMode] = 0;
                },
                RegistrationMode.Explorer => array =>
                {
                    array[(int)DutyFinderSetting.UnrestrictedParty] = 1;
                    array[(int)DutyFinderSetting.ExplorerMode] = 1;
                },
                _ => static _ => { },
            },
            mode switch
            {
                RegistrationMode.Unrestricted => "set duty registration mode to unrestricted party",
                RegistrationMode.Explorer => "set duty registration mode to explorer mode",
                _ => "left duty registration settings unchanged",
            });
    }

    private void DrawOverlayForAddon(string addonName, string overlayId)
    {
        if (!TryGetDrawableAddon(addonName, out var addon))
            return;

        var anchorNode = addon->GetNodeById(6);
        if (anchorNode == null)
            anchorNode = addon->GetNodeById(4);
        if (anchorNode == null)
            anchorNode = addon->RootNode;
        if (anchorNode == null)
            return;

        var rowAdvance = OverlayButtonHeight + OverlayButtonSpacing;
        var position = new Vector2(
            anchorNode->ScreenX + 4f,
            Math.Max(0f, anchorNode->ScreenY - OverlayRaisedOffset - ((OverlayRowCount - 1) * rowAdvance)));
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(OverlayButtonSpacing, OverlayButtonSpacing));

        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoBackground |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoDocking |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoNav;

        if (ImGui.Begin($"##BetterDutyFinderSettings_{overlayId}", flags))
        {
            DrawLanguageButton("JP", DutyFinderSetting.Ja);
            ImGui.SameLine();
            DrawLanguageButton("EN", DutyFinderSetting.En);
            ImGui.SameLine();
            DrawLanguageButton("DE", DutyFinderSetting.De);
            ImGui.SameLine();
            DrawLanguageButton("FR", DutyFinderSetting.Fr);
            ImGui.SameLine();
            DrawCyclingButton(GetLootRuleLabel(), DutyFinderSetting.LootRule);
            ImGui.SameLine();
            DrawToggleButton("InProg", DutyFinderSetting.JoinPartyInProgress);
            ImGui.SameLine();
            DrawToggleButton("LLR", DutyFinderSetting.LimitedLevelingRoulette);

            DrawToggleButton("Unsync", DutyFinderSetting.UnrestrictedParty);
            ImGui.SameLine();
            DrawToggleButton("Sync", DutyFinderSetting.LevelSync, disabled: GetCurrentSettingValue(DutyFinderSetting.UnrestrictedParty) == 0);
            ImGui.SameLine();
            DrawToggleButton("MinIL", DutyFinderSetting.MinimumIl);
            ImGui.SameLine();
            DrawToggleButton("NoEcho", DutyFinderSetting.SilenceEcho);
            ImGui.SameLine();
            DrawToggleButton("Explore", DutyFinderSetting.ExplorerMode);
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private void DrawLanguageButton(string label, DutyFinderSetting setting)
    {
        var active = GetCurrentSettingValue(setting) != 0;
        DrawButton(label, setting.ToString(), active, OverlayLanguageButtonWidth, () => ToggleSetting(setting));
    }

    private void DrawToggleButton(string label, DutyFinderSetting setting, bool disabled = false)
    {
        var active = GetCurrentSettingValue(setting) != 0;
        DrawButton(label, setting.ToString(), active, OverlayToggleButtonWidth, () => ToggleSetting(setting), disabled);
    }

    private void DrawCyclingButton(string label, DutyFinderSetting setting)
    {
        var active = GetCurrentSettingValue(setting) != 0;
        DrawButton(label, setting.ToString(), active, OverlayLootButtonWidth, () => ToggleSetting(setting));
    }

    private static void DrawButton(string label, string id, bool active, float width, Action onClick, bool disabled = false)
    {
        if (disabled)
            ImGui.BeginDisabled();

        var clicked = ImGui.InvisibleButton($"##BetterDutyFinder{id}", new Vector2(width, OverlayButtonHeight));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();

        var topColor = ImGui.ColorConvertFloat4ToU32(active
            ? new Vector4(64f / 255f, 110f / 255f, 72f / 255f, disabled ? 0.55f : 1f)
            : new Vector4(94f / 255f, 93f / 255f, 94f / 255f, disabled ? 0.45f : 1f));
        var bottomColor = ImGui.ColorConvertFloat4ToU32(active
            ? new Vector4(35f / 255f, 74f / 255f, 44f / 255f, disabled ? 0.55f : 1f)
            : new Vector4(52f / 255f, 51f / 255f, 52f / 255f, disabled ? 0.45f : 1f));
        var borderColor = ImGui.ColorConvertFloat4ToU32(active
            ? new Vector4(153f / 255f, 207f / 255f, 158f / 255f, disabled ? 0.55f : 1f)
            : new Vector4(168f / 255f, 169f / 255f, 168f / 255f, disabled ? 0.45f : 1f));
        var hoverOverlay = ImGui.ColorConvertFloat4ToU32(new Vector4(1.0f, 1.0f, 1.0f, disabled ? 0.0f : 0.08f));
        var activeOverlay = ImGui.ColorConvertFloat4ToU32(new Vector4(0.0f, 0.0f, 0.0f, disabled ? 0.0f : 0.12f));
        var textColor = disabled
            ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.72f, 0.72f, 0.72f, 0.62f))
            : ImGui.GetColorU32(ImGuiCol.Text);

        drawList.AddRectFilledMultiColor(min, max, topColor, topColor, bottomColor, bottomColor);
        drawList.AddRect(min, max, borderColor, OverlayButtonRounding, ImDrawFlags.None, 1.0f);

        if (!disabled && ImGui.IsItemActive())
            drawList.AddRectFilled(min, max, activeOverlay, OverlayButtonRounding);
        else if (!disabled && ImGui.IsItemHovered())
            drawList.AddRectFilled(min, max, hoverOverlay, OverlayButtonRounding);

        var textSize = ImGui.CalcTextSize(label);
        var textPos = new Vector2(
            min.X + Math.Max(6f, (width - textSize.X) * 0.5f),
            min.Y + Math.Max(0f, (OverlayButtonHeight - textSize.Y) * 0.5f));
        ImGui.PushClipRect(min, max, true);
        drawList.AddText(textPos, textColor, label);
        ImGui.PopClipRect();

        if (clicked && !disabled)
            onClick();

        if (disabled)
            ImGui.EndDisabled();
    }

    private void ToggleSetting(DutyFinderSetting setting)
    {
        if (setting is DutyFinderSetting.Ja or DutyFinderSetting.En or DutyFinderSetting.De or DutyFinderSetting.Fr)
        {
            var enabledLanguages =
                GetCurrentSettingValue(DutyFinderSetting.Ja) +
                GetCurrentSettingValue(DutyFinderSetting.En) +
                GetCurrentSettingValue(DutyFinderSetting.De) +
                GetCurrentSettingValue(DutyFinderSetting.Fr);

            if (enabledLanguages == 1 && GetCurrentSettingValue(setting) == 1)
                return;
        }

        ApplySettings(
            array =>
            {
                if (setting == DutyFinderSetting.LootRule)
                {
                    array[(int)setting] = (byte)((array[(int)setting] + 1) % 3);
                    return;
                }

                var nextValue = array[(int)setting] == 0 ? (byte)1 : (byte)0;
                array[(int)setting] = nextValue;

                if (setting == DutyFinderSetting.ExplorerMode && nextValue != 0)
                    array[(int)DutyFinderSetting.UnrestrictedParty] = 1;
            },
            $"toggled {GetSettingLabel(setting)}");
    }

    private bool ApplySettings(Action<byte[]> mutate, string action)
    {
        if (!EnsureInitializer())
            return false;

        var uiModule = UIModule.Instance();
        if (uiModule == null)
        {
            StatusText = "Unavailable - UIModule was not available.";
            return false;
        }

        try
        {
            var array = BuildCurrentSettingArray();
            mutate(array);

            fixed (byte* arrayPtr = array)
                setContentsFinderSettingsInit!(arrayPtr, uiModule);

            LastActionText = $"Last action: {action} at {DateTime.Now:HH:mm:ss}.";
            if (enabled)
                StatusText = "Enabled - ContentsFinder and RaidFinder expose lifecycle-tracked duty-finder setting buttons inline.";
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Better Duty Finder failed while applying duty-finder settings.");
            StatusText = "Unavailable - failed while applying the duty-finder setting update.";
            return false;
        }
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        addonLifecycle.RegisterListener(AddonEvent.PostDraw, ContentsFinderAddonName, OnDutyFinderAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, ContentsFinderAddonName, OnDutyFinderAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, ContentsFinderAddonName, OnDutyFinderAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostDraw, RaidFinderAddonName, OnDutyFinderAddon);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, RaidFinderAddonName, OnDutyFinderAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, RaidFinderAddonName, OnDutyFinderAddon);
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        addonLifecycle.UnregisterListener(OnDutyFinderAddon);
        subscribed = false;
    }

    private void OnDutyFinderAddon(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (type == AddonEvent.PreFinalize)
            {
                ClearTrackedAddon(args.AddonName);
                return;
            }

            if (!enabled || args.Addon.IsNull)
                return;

            var addon = (AtkUnitBase*)args.Addon.Address;
            if (IsDrawableAddon(addon))
                TrackAddon(args.AddonName, args.Addon.Address);
            else
                ClearTrackedAddon(args.AddonName);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Better Duty Finder failed while tracking the duty-finder addon lifecycle.");
        }
    }

    private void RefreshTrackedAddons()
    {
        RefreshTrackedAddon(ContentsFinderAddonName);
        RefreshTrackedAddon(RaidFinderAddonName);
    }

    private void RefreshTrackedAddon(string addonName)
    {
        var addon = AddonHelper.GetAddon(addonName);
        if (IsDrawableAddon(addon))
            TrackAddon(addonName, (nint)addon);
        else
            ClearTrackedAddon(addonName);
    }

    private bool TryGetDrawableAddon(string addonName, out AtkUnitBase* addon)
    {
        var trackedAddress = GetTrackedAddonAddress(addonName);
        addon = trackedAddress == nint.Zero ? null : (AtkUnitBase*)trackedAddress;
        if (IsDrawableAddon(addon))
            return true;

        addon = AddonHelper.GetAddon(addonName);
        if (!IsDrawableAddon(addon))
        {
            ClearTrackedAddon(addonName);
            return false;
        }

        TrackAddon(addonName, (nint)addon);
        return true;
    }

    private static bool IsDrawableAddon(AtkUnitBase* addon)
    {
        return addon != null && addon->IsVisible && addon->IsReady && addon->RootNode != null;
    }

    private nint GetTrackedAddonAddress(string addonName)
    {
        return addonName switch
        {
            ContentsFinderAddonName => contentsFinderAddress,
            RaidFinderAddonName => raidFinderAddress,
            _ => nint.Zero,
        };
    }

    private void TrackAddon(string addonName, nint address)
    {
        switch (addonName)
        {
            case ContentsFinderAddonName:
                contentsFinderAddress = address;
                break;
            case RaidFinderAddonName:
                raidFinderAddress = address;
                break;
        }
    }

    private void ClearTrackedAddon(string addonName)
    {
        switch (addonName)
        {
            case ContentsFinderAddonName:
                contentsFinderAddress = nint.Zero;
                break;
            case RaidFinderAddonName:
                raidFinderAddress = nint.Zero;
                break;
        }
    }

    private void ClearTrackedAddons()
    {
        contentsFinderAddress = nint.Zero;
        raidFinderAddress = nint.Zero;
    }

    private byte[] BuildCurrentSettingArray()
    {
        var count = Enum.GetValues<DutyFinderSetting>().Length;
        var array = new byte[27];

        for (var i = 0; i < count; i++)
        {
            var value = GetCurrentSettingValue((DutyFinderSetting)i);
            array[i] = value;
            array[i + count] = value;
        }

        array[26] = 1;
        return array;
    }

    private byte GetCurrentSettingValue(DutyFinderSetting setting)
    {
        var contentsFinder = ContentsFinder.Instance();
        return setting switch
        {
            DutyFinderSetting.Ja => TryGetUiConfig(UiConfigOption.ContentsFinderUseLangTypeJA),
            DutyFinderSetting.En => TryGetUiConfig(UiConfigOption.ContentsFinderUseLangTypeEN),
            DutyFinderSetting.De => TryGetUiConfig(UiConfigOption.ContentsFinderUseLangTypeDE),
            DutyFinderSetting.Fr => TryGetUiConfig(UiConfigOption.ContentsFinderUseLangTypeFR),
            DutyFinderSetting.JoinPartyInProgress => TryGetUiConfig(UiConfigOption.ContentsFinderSupplyEnable),
            DutyFinderSetting.LootRule => contentsFinder == null ? (byte)0 : (byte)contentsFinder->LootRules,
            DutyFinderSetting.UnrestrictedParty => contentsFinder != null && contentsFinder->IsUnrestrictedParty ? (byte)1 : (byte)0,
            DutyFinderSetting.LevelSync => contentsFinder != null && contentsFinder->IsLevelSync ? (byte)1 : (byte)0,
            DutyFinderSetting.MinimumIl => contentsFinder != null && contentsFinder->IsMinimalIL ? (byte)1 : (byte)0,
            DutyFinderSetting.SilenceEcho => contentsFinder != null && contentsFinder->IsSilenceEcho ? (byte)1 : (byte)0,
            DutyFinderSetting.ExplorerMode => contentsFinder != null && contentsFinder->IsExplorerMode ? (byte)1 : (byte)0,
            DutyFinderSetting.LimitedLevelingRoulette => contentsFinder != null && contentsFinder->IsLimitedLevelingRoulette ? (byte)1 : (byte)0,
            _ => 0,
        };
    }

    private byte TryGetUiConfig(UiConfigOption option)
    {
        return gameConfig.TryGet(option, out uint value) ? (byte)value : (byte)0;
    }

    private bool EnsureInitializer()
    {
        if (setContentsFinderSettingsInit != null)
            return true;

        try
        {
            var address = sigScanner.ScanText(SetContentsFinderSettingsInitSignature);
            setContentsFinderSettingsInit = Marshal.GetDelegateForFunctionPointer<SetContentsFinderSettingsInitDelegate>(address);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Better Duty Finder failed while resolving the duty-finder settings initializer.");
            StatusText = "Unavailable - failed to resolve the duty-finder settings initializer.";
            return false;
        }
    }

    private string GetLootRuleLabel()
    {
        return GetCurrentSettingValue(DutyFinderSetting.LootRule) switch
        {
            1 => "Greed",
            2 => "Master",
            _ => "Normal",
        };
    }

    private static string GetSettingLabel(DutyFinderSetting setting)
    {
        return setting switch
        {
            DutyFinderSetting.Ja => "Japanese language",
            DutyFinderSetting.En => "English language",
            DutyFinderSetting.De => "German language",
            DutyFinderSetting.Fr => "French language",
            DutyFinderSetting.LootRule => "loot rule",
            DutyFinderSetting.JoinPartyInProgress => "join-in-progress",
            DutyFinderSetting.UnrestrictedParty => "unrestricted party",
            DutyFinderSetting.LevelSync => "level sync",
            DutyFinderSetting.MinimumIl => "minimum item level",
            DutyFinderSetting.SilenceEcho => "silence echo",
            DutyFinderSetting.ExplorerMode => "explorer mode",
            DutyFinderSetting.LimitedLevelingRoulette => "limited leveling roulette",
            _ => "duty-finder setting",
        };
    }

    public enum RegistrationMode
    {
        Default,
        Unrestricted,
        Explorer,
    }

    private enum DutyFinderSetting
    {
        Ja = 0,
        En = 1,
        De = 2,
        Fr = 3,
        LootRule = 4,
        JoinPartyInProgress = 5,
        UnrestrictedParty = 6,
        LevelSync = 7,
        MinimumIl = 8,
        SilenceEcho = 9,
        ExplorerMode = 10,
        LimitedLevelingRoulette = 11,
    }

    private delegate void SetContentsFinderSettingsInitDelegate(byte* data, UIModule* module);
}
