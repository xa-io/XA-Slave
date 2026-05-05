using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using ActionRow = Lumina.Excel.Sheets.Action;

namespace XASlave.Services;

public unsafe sealed class BetterCastBarService : IDisposable
{
    public const int SlidecastModeNone = 0;
    public const int SlidecastModeZone = 1;
    public const int SlidecastModeLine = 2;

    private const string CastBarAddonName = "_CastBar";

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly ExcelSheet<ActionRow>? actionSheet;
    private bool enabled;
    private bool subscribed;
    private nint trackedAddonAddress;
    private bool nodeStateCaptured;
    private TextNodeLayout interruptedTextOriginal;
    private TextNodeLayout actionNameTextOriginal;
    private TextNodeLayout castingTextOriginal;
    private TextNodeLayout castTimeTextOriginal;
    private ResNodeLayout iconOriginal;
    private Vector2 interruptedTextPosition = new(0f, 11f);
    private int interruptedTextSize = 18;
    private Vector2 actionNameTextPosition = new(48f, 0f);
    private int actionNameTextSize = 12;
    private Vector2 castingTextPosition = Vector2.Zero;
    private int castingTextSize = 12;
    private Vector2 castTimeTextPosition = new(130f, 30f);
    private int castTimeTextSize = 20;
    private int iconAlpha = 255;
    private Vector2 iconPosition = new(0f, 3f);
    private Vector2 iconScale = Vector2.One;
    private int slidecastMode = SlidecastModeZone;
    private int slidecastThresholdMs = 500;
    private int slidecastLineWidth = 3;
    private int slidecastLineHeight;
    private Vector4 slidecastNotReadyColor = new(0.8f, 0.3f, 0.3f, 1f);
    private Vector4 slidecastReadyColor = new(0.3f, 0.8f, 0.3f, 1f);

    public BetterCastBarService(
        IAddonLifecycle addonLifecycle,
        IObjectTable objectTable,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.addonLifecycle = addonLifecycle;
        this.objectTable = objectTable;
        this.log = log;
        actionSheet = dataManager.GetExcelSheet<ActionRow>();
    }

    public string StatusText { get; private set; } = "Disabled";
    public string LastActionText { get; private set; } = "No actions yet.";
    public bool IsCastBarVisible { get; private set; }
    public string CurrentCastName { get; private set; } = "None";
    public int CurrentCastTimeMs { get; private set; }
    public int CurrentCastRemainingMs { get; private set; }
    public float CurrentCastProgress { get; private set; }
    public bool IsSlidecastReady { get; private set; }

    public static int NormalizeSlidecastMode(int value)
    {
        return value is SlidecastModeNone or SlidecastModeZone or SlidecastModeLine
            ? value
            : SlidecastModeZone;
    }

    public void ApplyConfiguration(Configuration configuration)
    {
        interruptedTextPosition = configuration.BetterCastBarInterruptedTextPosition;
        interruptedTextSize = Math.Clamp(configuration.BetterCastBarInterruptedTextSize, 1, 255);
        actionNameTextPosition = configuration.BetterCastBarActionNamePosition;
        actionNameTextSize = Math.Clamp(configuration.BetterCastBarActionNameSize, 1, 255);
        castingTextPosition = configuration.BetterCastBarCastingTextPosition;
        castingTextSize = Math.Clamp(configuration.BetterCastBarCastingTextSize, 1, 255);
        castTimeTextPosition = configuration.BetterCastBarCastTimeTextPosition;
        castTimeTextSize = Math.Clamp(configuration.BetterCastBarCastTimeTextSize, 1, 255);
        iconAlpha = Math.Clamp(configuration.BetterCastBarIconAlpha, 0, 255);
        iconPosition = configuration.BetterCastBarIconPosition;
        iconScale = configuration.BetterCastBarIconScale;
        slidecastMode = NormalizeSlidecastMode(configuration.BetterCastBarSlidecastMode);
        slidecastThresholdMs = Math.Clamp(configuration.BetterCastBarSlidecastThresholdMs, 0, 5000);
        slidecastLineWidth = Math.Clamp(configuration.BetterCastBarSlidecastLineWidth, 1, 20);
        slidecastLineHeight = Math.Clamp(configuration.BetterCastBarSlidecastLineHeight, 0, 100);
        slidecastNotReadyColor = configuration.BetterCastBarSlidecastNotReadyColor;
        slidecastReadyColor = configuration.BetterCastBarSlidecastReadyColor;

        if (enabled)
            StatusText = BuildStatusText();
    }

    public bool SetEnabled(bool value)
    {
        if (value == enabled)
            return enabled;

        if (!value)
        {
            enabled = false;
            RestoreVisibleCastBar();
            Unsubscribe();
            ClearCastState();
            StatusText = "Disabled";
            return false;
        }

        enabled = true;
        Subscribe();
        StatusText = BuildStatusText();
        return true;
    }

    public void DrawOverlay()
    {
        if (!enabled)
            return;

        UpdateCastState();
        if (!IsCastBarVisible || slidecastMode == SlidecastModeNone)
            return;

        var addon = AddonHelper.GetAddon(CastBarAddonName);
        if (addon == null || !addon->IsVisible)
            return;

        var progressBarNode = addon->GetNodeById(11);
        if (progressBarNode == null)
            return;

        var totalCastMs = Math.Max(1, CurrentCastTimeMs);
        var slidecastProgress = Math.Clamp((float)(totalCastMs - slidecastThresholdMs) / totalCastMs, 0f, 1f);
        IsSlidecastReady = CurrentCastProgress >= slidecastProgress;

        var barX = progressBarNode->ScreenX;
        var barY = progressBarNode->ScreenY;
        var barWidth = Math.Max(1f, progressBarNode->Width * progressBarNode->ScaleX);
        var barHeight = Math.Max(1f, progressBarNode->Height * progressBarNode->ScaleY);
        var markerX = barX + (barWidth * slidecastProgress);
        var color = IsSlidecastReady ? slidecastReadyColor : slidecastNotReadyColor;

        var drawList = ImGui.GetForegroundDrawList();
        switch (slidecastMode)
        {
            case SlidecastModeZone:
            {
                var fillColor = new Vector4(color.X, color.Y, color.Z, MathF.Min(color.W * 0.20f, 0.35f));
                drawList.AddRectFilled(
                    new Vector2(markerX, barY),
                    new Vector2(barX + barWidth, barY + barHeight),
                    ImGui.ColorConvertFloat4ToU32(fillColor));
                drawList.AddLine(
                    new Vector2(markerX, barY),
                    new Vector2(markerX, barY + barHeight),
                    ImGui.ColorConvertFloat4ToU32(color),
                    Math.Max(1f, slidecastLineWidth));
                break;
            }
            case SlidecastModeLine:
            {
                var extraHeight = Math.Max(0, slidecastLineHeight);
                drawList.AddRectFilled(
                    new Vector2(markerX - (slidecastLineWidth / 2f), barY - extraHeight),
                    new Vector2(markerX + (slidecastLineWidth / 2f), barY + barHeight + extraHeight),
                    ImGui.ColorConvertFloat4ToU32(color));
                break;
            }
        }
    }

    public void Dispose()
    {
        enabled = false;
        RestoreVisibleCastBar();
        Unsubscribe();
        ClearCastState();
    }

    private string BuildStatusText()
    {
        var modeText = slidecastMode switch
        {
            SlidecastModeZone => "zone",
            SlidecastModeLine => "line",
            _ => "disabled",
        };

        return $"Enabled - _CastBar nodes are restyled locally and the slidecast {modeText} overlay is active.";
    }

    private void Subscribe()
    {
        if (subscribed)
            return;

        addonLifecycle.RegisterListener(AddonEvent.PostDraw, CastBarAddonName, OnCastBarAddon);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, CastBarAddonName, OnCastBarAddon);
        subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!subscribed)
            return;

        addonLifecycle.UnregisterListener(OnCastBarAddon);
        subscribed = false;
    }

    private void OnCastBarAddon(AddonEvent type, AddonArgs args)
    {
        if (args.Addon.IsNull)
            return;

        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon == null)
                return;

            switch (type)
            {
                case AddonEvent.PostDraw when enabled && addon->IsVisible:
                    CaptureNodeState(addon);
                    ApplyNodeLayout(addon);
                    UpdateCastState();
                    break;
                case AddonEvent.PreFinalize:
                    RestoreNodeLayout(addon);
                    ClearTrackedNodeState();
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[XASlave] Better Cast Bar failed while handling _CastBar.");
        }
    }

    private void UpdateCastState()
    {
        if (objectTable.LocalPlayer is not IBattleChara battleChara || !battleChara.IsCasting || battleChara.TotalCastTime <= 0f)
        {
            ClearCastState();
            return;
        }

        IsCastBarVisible = true;
        CurrentCastTimeMs = (int)Math.Max(0f, battleChara.TotalCastTime * 1000f);
        CurrentCastRemainingMs = (int)Math.Max(0f, (battleChara.TotalCastTime - battleChara.CurrentCastTime) * 1000f);
        CurrentCastProgress = Math.Clamp(battleChara.CurrentCastTime / battleChara.TotalCastTime, 0f, 1f);
        CurrentCastName = ResolveCastName(battleChara.CastActionId);
    }

    private string ResolveCastName(uint castActionId)
    {
        if (castActionId == 0 || actionSheet == null)
            return "None";

        return actionSheet.TryGetRow(castActionId, out var actionRow) && !string.IsNullOrWhiteSpace(actionRow.Name.ToString())
            ? actionRow.Name.ToString()
            : $"Action {castActionId}";
    }

    private void CaptureNodeState(AtkUnitBase* addon)
    {
        var addonAddress = (nint)addon;
        if (nodeStateCaptured && trackedAddonAddress == addonAddress)
            return;

        interruptedTextOriginal = CaptureTextNode(addon->GetTextNodeById(2));
        actionNameTextOriginal = CaptureTextNode(addon->GetTextNodeById(4));
        castingTextOriginal = CaptureTextNode(addon->GetTextNodeById(6));
        castTimeTextOriginal = CaptureTextNode(addon->GetTextNodeById(7));
        iconOriginal = CaptureResNode(addon->GetNodeById(8));
        trackedAddonAddress = addonAddress;
        nodeStateCaptured = true;
        LastActionText = $"Last action: captured the original _CastBar node layout at {DateTime.Now:HH:mm:ss}.";
    }

    private static TextNodeLayout CaptureTextNode(AtkTextNode* node)
    {
        return node == null
            ? default
            : new TextNodeLayout(node->X, node->Y, node->FontSize);
    }

    private static ResNodeLayout CaptureResNode(AtkResNode* node)
    {
        return node == null
            ? default
            : new ResNodeLayout(node->X, node->Y, node->ScaleX, node->ScaleY);
    }

    private void ApplyNodeLayout(AtkUnitBase* addon)
    {
        ApplyTextLayout(addon->GetTextNodeById(2), interruptedTextPosition, interruptedTextSize);
        ApplyTextLayout(addon->GetTextNodeById(4), actionNameTextPosition, actionNameTextSize);
        ApplyTextLayout(addon->GetTextNodeById(6), castingTextPosition, castingTextSize);
        ApplyTextLayout(addon->GetTextNodeById(7), castTimeTextPosition, castTimeTextSize);

        var iconNode = addon->GetNodeById(8);
        if (iconNode != null)
        {
            iconNode->SetPositionFloat(iconPosition.X, iconPosition.Y);
            iconNode->SetScale(iconScale.X, iconScale.Y);
            iconNode->SetAlpha((byte)Math.Clamp(iconAlpha, 0, 255));
        }
    }

    private static void ApplyTextLayout(AtkTextNode* node, Vector2 position, int fontSize)
    {
        if (node == null)
            return;

        node->SetPositionFloat(position.X, position.Y);
        node->FontSize = (byte)Math.Clamp(fontSize, 1, 255);
    }

    private void RestoreVisibleCastBar()
    {
        var addon = AddonHelper.GetAddon(CastBarAddonName);
        if (addon == null)
        {
            ClearTrackedNodeState();
            return;
        }

        RestoreNodeLayout(addon);
        ClearTrackedNodeState();
        LastActionText = $"Last action: restored the original _CastBar node layout at {DateTime.Now:HH:mm:ss}.";
    }

    private void RestoreNodeLayout(AtkUnitBase* addon)
    {
        if (!nodeStateCaptured || addon == null)
            return;

        RestoreTextLayout(addon->GetTextNodeById(2), interruptedTextOriginal);
        RestoreTextLayout(addon->GetTextNodeById(4), actionNameTextOriginal);
        RestoreTextLayout(addon->GetTextNodeById(6), castingTextOriginal);
        RestoreTextLayout(addon->GetTextNodeById(7), castTimeTextOriginal);

        var iconNode = addon->GetNodeById(8);
        if (iconNode != null)
        {
            iconNode->SetPositionFloat(iconOriginal.X, iconOriginal.Y);
            iconNode->SetScale(iconOriginal.ScaleX, iconOriginal.ScaleY);
            iconNode->SetAlpha(255);
        }
    }

    private static void RestoreTextLayout(AtkTextNode* node, TextNodeLayout layout)
    {
        if (node == null)
            return;

        node->SetPositionFloat(layout.X, layout.Y);
        node->FontSize = layout.FontSize == 0 ? (byte)12 : layout.FontSize;
    }

    private void ClearTrackedNodeState()
    {
        trackedAddonAddress = nint.Zero;
        nodeStateCaptured = false;
        interruptedTextOriginal = default;
        actionNameTextOriginal = default;
        castingTextOriginal = default;
        castTimeTextOriginal = default;
        iconOriginal = default;
    }

    private void ClearCastState()
    {
        IsCastBarVisible = false;
        CurrentCastName = "None";
        CurrentCastTimeMs = 0;
        CurrentCastRemainingMs = 0;
        CurrentCastProgress = 0f;
        IsSlidecastReady = false;
    }

    private readonly record struct TextNodeLayout(float X, float Y, byte FontSize);
    private readonly record struct ResNodeLayout(float X, float Y, float ScaleX, float ScaleY);
}
