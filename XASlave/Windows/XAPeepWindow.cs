using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using XASlave.Data;
using XASlave.Services;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace XASlave.Windows;

public sealed class XAPeepWindow : Window
{
    private readonly Plugin plugin;
    private readonly TitleBarButton lockButton;
    private static float UiScale => ImGuiHelpers.GlobalScale;
    private static float UiScaleSafe => ImGuiHelpers.GlobalScale;
    private readonly object pendingActionSync = new();
    private readonly Queue<XAPeepPendingAction> pendingRowActions = new();
    private XAPeepPendingAction? pendingFocusAction;
    private bool pendingActionDrainScheduled;
    private bool focusPreviewCaptured;
    private ulong cachedFocusTargetId = ulong.MaxValue;

    public XAPeepWindow(Plugin plugin)
        : base("XA Peep###XAPeepWindow", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        Size = ScaledVector(290f, 220f);
        SizeCondition = ImGuiCond.FirstUseEver;
        UpdateSizeConstraints(UiScaleSafe);

        lockButton = new TitleBarButton
        {
            AvailableClickthrough = false,
            Click = _ => ToggleResizeLock(),
            Icon = FontAwesomeIcon.LockOpen,
        };

        TitleBarButtons.Add(lockButton);
    }

    public override void PreDraw()
    {
        UpdateSizeConstraints(UiScale);
        Flags = plugin.Configuration.XAPeepWindowLocked
            ? ImGuiWindowFlags.NoResize
            : ImGuiWindowFlags.None;
        lockButton.Icon = plugin.Configuration.XAPeepWindowLocked
            ? FontAwesomeIcon.Lock
            : FontAwesomeIcon.LockOpen;
        base.PreDraw();
    }

    public override void OnOpen()
    {
        if (plugin.Configuration.XAPeepWindowOpen)
            return;

        plugin.Configuration.XAPeepWindowOpen = true;
        plugin.Configuration.Save();
    }

    public override void OnClose()
    {
        ReleaseFocusPreview();

        if (!plugin.Configuration.XAPeepWindowOpen)
            return;

        plugin.Configuration.XAPeepWindowOpen = false;
        plugin.Configuration.Save();
    }

    public override void Draw()
    {
        var service = plugin.XAPeep;

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Targeting you");
        ImGui.SameLine();
        DrawHelpMarker("Hover live rows to preview the focus target.\nLeft-click prints the player name. Right-click targets them.\nCtrl+Left Click examines. Ctrl+Right Click opens the adventurer plate.");
        ImGui.SameLine();
        if (ImGui.SmallButton("History"))
            plugin.OpenXAPeepHistoryUi();
        ImGui.SameLine();
        var clearHistoryModifierHeld = ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift;
        using (ImRaii.Disabled(!clearHistoryModifierHeld))
        {
            if (ImGui.SmallButton("Clear"))
                service.ClearHistory();
        }
        if (!clearHistoryModifierHeld)
        {
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Press and hold CTRL + SHIFT to allow clearing.");
        }

        ImGui.Spacing();

        var trackedPlayers = service.GetTrackedPlayers(200);
        if (!plugin.Configuration.XAPeepEnabled && trackedPlayers.Count == 0)
        {
            ReleaseFocusPreviewIfNeeded(false);
            ImGui.TextDisabled("Tracking is off.");
            return;
        }

        var previewActiveThisFrame = false;
        using var listBox = ImRaii.ListBox("##XAPeepList", new Vector2(-1f, -1f));
        if (!listBox)
        {
            ReleaseFocusPreviewIfNeeded(false);
            return;
        }

        if (trackedPlayers.Count == 0)
        {
            ImGui.TextDisabled("Nobody is targeting you.");
        }
        else
        {
            foreach (var player in trackedPlayers)
                DrawEntry(player, ref previewActiveThisFrame);
        }

        ReleaseFocusPreviewIfNeeded(previewActiveThisFrame);
    }

    private void DrawEntry(XAPeepTrackedPlayerView player, ref bool previewActiveThisFrame)
    {
        var line = $"{player.TotalTargetCount:00} - {player.CompactName} - {FormatCompactTime(player.LastSeenUtc)}";
        using (ImRaii.PushColor(
                   ImGuiCol.Text,
                   ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled],
                   !player.IsLive))
        {
            ImGui.Selectable(line, false, player.IsLive ? ImGuiSelectableFlags.None : ImGuiSelectableFlags.Disabled);
        }
        var hovered = player.IsLive && ImGui.IsItemHovered();
        var controlHeld = ImGui.GetIO().KeyCtrl;

        if (!hovered)
            return;

        if (player.GameObjectId != 0)
        {
            previewActiveThisFrame = true;
            UpdateFocusPreview(player.GameObjectId);
        }

        QueueRowAction(player, GetRequestedAction(hovered, controlHeld));
    }

    private void UpdateFocusPreview(ulong gameObjectId)
    {
        QueuePendingAction(new(XAPeepRowAction.FocusPreview, gameObjectId, string.Empty, string.Empty), focusAction: true);
    }

    private void ReleaseFocusPreviewIfNeeded(bool previewActiveThisFrame)
    {
        if (previewActiveThisFrame || !focusPreviewCaptured)
            return;

        ReleaseFocusPreview();
    }

    private void ReleaseFocusPreview()
    {
        QueuePendingAction(new(XAPeepRowAction.ReleaseFocusPreview, 0, string.Empty, string.Empty), focusAction: true);
    }

    private static void TargetPlayer(XAPeepPendingAction action, IGameObject? actor)
    {
        if (actor != null)
        {
            Plugin.TargetManager.Target = actor;
            return;
        }

        var targetName = string.IsNullOrWhiteSpace(action.CompactName)
            ? action.DisplayName
            : action.CompactName;
        AddonHelper.TargetByName(targetName);
    }

    private static unsafe void ExaminePlayer(XAPeepPendingAction action, IGameObject? actor)
    {
        if (actor == null)
        {
            Plugin.ToastGui.ShowError($"[XASlave] Could not examine {action.CompactName}: player is no longer nearby.");
            return;
        }

        var inspectAgent = AgentInspect.Instance();
        if (inspectAgent == null)
        {
            Plugin.ToastGui.ShowError($"[XASlave] Could not open examine for {action.CompactName}.");
            return;
        }

        inspectAgent->ExamineCharacter(actor.EntityId);
    }

    private static unsafe void ShowAdventurePlate(XAPeepPendingAction action, IGameObject? actor)
    {
        if (actor == null)
        {
            Plugin.ToastGui.ShowError($"[XASlave] Could not open adventurer plate for {action.CompactName}: player is no longer nearby.");
            return;
        }

        var charaCardAgent = AgentCharaCard.Instance();
        if (charaCardAgent == null)
        {
            Plugin.ToastGui.ShowError($"[XASlave] Could not open adventurer plate for {action.CompactName}.");
            return;
        }

        charaCardAgent->OpenCharaCard((CSGameObject*)actor.Address);
    }

    private static XAPeepRowAction GetRequestedAction(bool hovered, bool controlHeld)
    {
        if (!hovered)
            return XAPeepRowAction.None;

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            return controlHeld ? XAPeepRowAction.Examine : XAPeepRowAction.EchoName;

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            return controlHeld ? XAPeepRowAction.ShowAdventurePlate : XAPeepRowAction.Target;

        return XAPeepRowAction.None;
    }

    private void QueueRowAction(XAPeepTrackedPlayerView player, XAPeepRowAction action)
    {
        if (action == XAPeepRowAction.None)
            return;

        QueuePendingAction(new(action, player.GameObjectId, player.CompactName, player.DisplayName), focusAction: false);
    }

    private void QueuePendingAction(XAPeepPendingAction action, bool focusAction)
    {
        lock (pendingActionSync)
        {
            if (focusAction)
            {
                pendingFocusAction = action;
            }
            else
            {
                if (pendingRowActions.Count >= 16)
                    pendingRowActions.Dequeue();
                pendingRowActions.Enqueue(action);
            }

            if (pendingActionDrainScheduled)
                return;

            pendingActionDrainScheduled = true;
        }

        Plugin.ScheduleOnGameThread(DrainPendingActions);
    }

    private void DrainPendingActions()
    {
        XAPeepPendingAction? focusAction;
        XAPeepPendingAction[] rowActions;
        lock (pendingActionSync)
        {
            focusAction = pendingFocusAction;
            pendingFocusAction = null;
            rowActions = pendingRowActions.ToArray();
            pendingRowActions.Clear();
            pendingActionDrainScheduled = false;
        }

        try
        {
            if (focusAction.HasValue)
                ExecutePendingAction(focusAction.Value);
            foreach (var action in rowActions)
                ExecutePendingAction(action);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "[XASlave] XA Peep pending action failed.");
        }

        lock (pendingActionSync)
        {
            if (pendingActionDrainScheduled || (pendingFocusAction == null && pendingRowActions.Count == 0))
                return;
            pendingActionDrainScheduled = true;
        }
        Plugin.ScheduleOnGameThread(DrainPendingActions);
    }

    private void ExecutePendingAction(XAPeepPendingAction action)
    {
        var actor = action.GameObjectId == 0
            ? null
            : Plugin.ObjectTable.FirstOrDefault(obj => obj.GameObjectId == action.GameObjectId);

        switch (action.Action)
        {
            case XAPeepRowAction.EchoName:
                Plugin.ChatGui.Print(action.DisplayName);
                break;
            case XAPeepRowAction.Target:
                TargetPlayer(action, actor);
                break;
            case XAPeepRowAction.Examine:
                ExaminePlayer(action, actor);
                break;
            case XAPeepRowAction.ShowAdventurePlate:
                ShowAdventurePlate(action, actor);
                break;
            case XAPeepRowAction.FocusPreview:
                if (!focusPreviewCaptured)
                {
                    cachedFocusTargetId = Plugin.TargetManager.FocusTarget?.GameObjectId ?? ulong.MaxValue;
                    focusPreviewCaptured = true;
                }
                if (actor != null && Plugin.TargetManager.FocusTarget?.GameObjectId != actor.GameObjectId)
                    Plugin.TargetManager.FocusTarget = actor;
                break;
            case XAPeepRowAction.ReleaseFocusPreview:
                if (!focusPreviewCaptured)
                    break;
                Plugin.TargetManager.FocusTarget = cachedFocusTargetId == ulong.MaxValue
                    ? null
                    : Plugin.ObjectTable.FirstOrDefault(obj => obj.GameObjectId == cachedFocusTargetId);
                focusPreviewCaptured = false;
                cachedFocusTargetId = ulong.MaxValue;
                break;
        }
    }

    private static string FormatCompactTime(DateTime timestampUtc)
    {
        return timestampUtc == DateTime.MinValue
            ? "-"
            : timestampUtc.ToLocalTime().ToString("HH:mm");
    }

    private static void DrawHelpMarker(string helpText)
    {
        ImGui.TextDisabled("(?)");
        if (!ImGui.IsItemHovered())
            return;

        using (ImRaii.Tooltip())
        {
            using (ImRaii.TextWrapPos(Scale(420f)))
            {
                ImGui.TextUnformatted(helpText);
            }
        }
    }

    private void UpdateSizeConstraints(float scale)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(230f * scale, 170f * scale),
            MaximumSize = new Vector2(420f * scale, 480f * scale),
        };
    }

    private static float Scale(float value)
        => value * UiScale;

    private static Vector2 ScaledVector(float x, float y)
        => ImGuiHelpers.ScaledVector2(x, y);

    private void ToggleResizeLock()
    {
        plugin.Configuration.XAPeepWindowLocked = !plugin.Configuration.XAPeepWindowLocked;
        plugin.Configuration.Save();
    }

    private enum XAPeepRowAction
    {
        None,
        EchoName,
        Target,
        Examine,
        ShowAdventurePlate,
        FocusPreview,
        ReleaseFocusPreview,
    }

    private readonly record struct XAPeepPendingAction(
        XAPeepRowAction Action,
        ulong GameObjectId,
        string CompactName,
        string DisplayName);
}
