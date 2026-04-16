using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;
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
    private bool focusPreviewCaptured;
    private ulong cachedFocusTargetId = ulong.MaxValue;

    public XAPeepWindow(Plugin plugin)
        : base("XA Peep###XAPeepWindow", ImGuiWindowFlags.None)
    {
        this.plugin = plugin;
        Size = new Vector2(290f, 220f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(230f, 170f),
            MaximumSize = new Vector2(420f, 480f),
        };

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
        if (!clearHistoryModifierHeld)
            ImGui.BeginDisabled();
        if (ImGui.SmallButton("Clear"))
            service.ClearHistory();
        if (!clearHistoryModifierHeld)
        {
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Press and hold CTRL + SHIFT to allow clearing.");
        }

        ImGui.Spacing();

        var trackedPlayers = service.GetTrackedPlayers(200);
        var liveActorIndex = BuildLiveActorIndex(trackedPlayers);
        if (!plugin.Configuration.XAPeepEnabled && trackedPlayers.Count == 0)
        {
            ReleaseFocusPreviewIfNeeded(false);
            ImGui.TextDisabled("Tracking is off.");
            return;
        }

        var previewActiveThisFrame = false;
        if (!ImGui.BeginListBox("##XAPeepList", new Vector2(-1f, -1f)))
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
                DrawEntry(player, liveActorIndex, ref previewActiveThisFrame);
        }

        ImGui.EndListBox();
        ReleaseFocusPreviewIfNeeded(previewActiveThisFrame);
    }

    private void DrawEntry(XAPeepTrackedPlayerView player, IReadOnlyDictionary<ulong, IGameObject> liveActorIndex, ref bool previewActiveThisFrame)
    {
        var line = $"{player.TotalTargetCount:00} - {player.CompactName} - {FormatCompactTime(player.LastSeenUtc)}";
        if (!player.IsLive)
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);

        ImGui.Selectable(line, false, player.IsLive ? ImGuiSelectableFlags.None : ImGuiSelectableFlags.Disabled);
        var actor = TryGetVisibleActor(player, liveActorIndex);
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        var controlHeld = ImGui.GetIO().KeyCtrl;

        if (!player.IsLive)
            ImGui.PopStyleColor();

        if (!hovered)
            return;

        if (actor != null)
        {
            previewActiveThisFrame = true;
            UpdateFocusPreview(actor);
        }

        ExecuteRowAction(player, actor, GetRequestedAction(hovered, controlHeld));
    }

    private static Dictionary<ulong, IGameObject> BuildLiveActorIndex(IEnumerable<XAPeepTrackedPlayerView> trackedPlayers)
    {
        var liveIds = trackedPlayers
            .Where(player => player.IsLive && player.GameObjectId != 0)
            .Select(player => player.GameObjectId)
            .ToHashSet();

        if (liveIds.Count == 0)
            return new();

        var liveActorIndex = new Dictionary<ulong, IGameObject>(liveIds.Count);
        foreach (var obj in Plugin.ObjectTable)
        {
            if (!liveIds.Contains(obj.GameObjectId) || liveActorIndex.ContainsKey(obj.GameObjectId))
                continue;

            liveActorIndex[obj.GameObjectId] = obj;
        }

        return liveActorIndex;
    }

    private static IGameObject? TryGetVisibleActor(XAPeepTrackedPlayerView player, IReadOnlyDictionary<ulong, IGameObject> liveActorIndex)
    {
        if (player.GameObjectId == 0)
            return null;

        return liveActorIndex.TryGetValue(player.GameObjectId, out var actor)
            ? actor
            : null;
    }

    private void UpdateFocusPreview(IGameObject actor)
    {
        if (!focusPreviewCaptured)
        {
            cachedFocusTargetId = Plugin.TargetManager.FocusTarget?.GameObjectId ?? ulong.MaxValue;
            focusPreviewCaptured = true;
        }

        if (Plugin.TargetManager.FocusTarget?.GameObjectId != actor.GameObjectId)
            Plugin.TargetManager.FocusTarget = actor;
    }

    private void ReleaseFocusPreviewIfNeeded(bool previewActiveThisFrame)
    {
        if (previewActiveThisFrame || !focusPreviewCaptured)
            return;

        ReleaseFocusPreview();
    }

    private void ReleaseFocusPreview()
    {
        if (!focusPreviewCaptured)
            return;

        if (cachedFocusTargetId == ulong.MaxValue)
        {
            Plugin.TargetManager.FocusTarget = null;
        }
        else
        {
            Plugin.TargetManager.FocusTarget = Plugin.ObjectTable.FirstOrDefault(obj => obj.GameObjectId == cachedFocusTargetId);
        }

        focusPreviewCaptured = false;
        cachedFocusTargetId = ulong.MaxValue;
    }

    private static void TargetPlayer(XAPeepTrackedPlayerView player, IGameObject? actor)
    {
        if (actor != null)
        {
            Plugin.TargetManager.Target = actor;
            return;
        }

        var targetName = string.IsNullOrWhiteSpace(player.CompactName)
            ? player.DisplayName
            : player.CompactName;
        AddonHelper.TargetByName(targetName);
    }

    private static unsafe void ExaminePlayer(XAPeepTrackedPlayerView player, IGameObject? actor)
    {
        if (actor == null)
        {
            Plugin.ToastGui.ShowError($"[XASlave] Could not examine {player.CompactName}: player is no longer nearby.");
            return;
        }

        var inspectAgent = AgentInspect.Instance();
        if (inspectAgent == null)
        {
            Plugin.ToastGui.ShowError($"[XASlave] Could not open examine for {player.CompactName}.");
            return;
        }

        inspectAgent->ExamineCharacter(actor.EntityId);
    }

    private static unsafe void ShowAdventurePlate(XAPeepTrackedPlayerView player, IGameObject? actor)
    {
        if (actor == null)
        {
            Plugin.ToastGui.ShowError($"[XASlave] Could not open adventurer plate for {player.CompactName}: player is no longer nearby.");
            return;
        }

        var charaCardAgent = AgentCharaCard.Instance();
        if (charaCardAgent == null)
        {
            Plugin.ToastGui.ShowError($"[XASlave] Could not open adventurer plate for {player.CompactName}.");
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

    private static void ExecuteRowAction(XAPeepTrackedPlayerView player, IGameObject? actor, XAPeepRowAction action)
    {
        switch (action)
        {
            case XAPeepRowAction.EchoName:
                Plugin.ChatGui.Print(player.DisplayName);
                break;
            case XAPeepRowAction.Target:
                TargetPlayer(player, actor);
                break;
            case XAPeepRowAction.Examine:
                ExaminePlayer(player, actor);
                break;
            case XAPeepRowAction.ShowAdventurePlate:
                ShowAdventurePlate(player, actor);
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

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(420f);
        ImGui.TextUnformatted(helpText);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

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
    }
}
