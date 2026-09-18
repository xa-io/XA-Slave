using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using XASlave.Data;
using XASlave.Services;

namespace XASlave.Windows;

public sealed class NearbyPlayersWindow : Window
{
    private readonly NearbyPlayersService service;
    private readonly XAPeepService peep;
    private readonly TitleBarButton lockButton;
    private readonly Dictionary<uint, ISharedImmediateTexture> jobIcons = new();
    private readonly List<int> matches = new();
    private readonly HashSet<ulong> drawn = new();
    private PlayerObservationSnapshot? searched;
    private string query = string.Empty;
    private readonly Dictionary<uint, string> jobs = new(), worlds = new();
    private int nearbyFrame = -1;

    public NearbyPlayersWindow(NearbyPlayersService service, XAPeepService peep) : base("XA Nearby###XANearbyMini")
    {
        this.service = service;
        this.peep = peep;
        Size = new Vector2(310, 330);
        SizeCondition = ImGuiCond.FirstUseEver;
        UpdateSizeConstraints();
        lockButton = new TitleBarButton
        {
            AvailableClickthrough = false,
            Icon = FontAwesomeIcon.LockOpen,
            Click = _ => service.ChangeSettings(s => s.WindowLocked = !s.WindowLocked),
        };
        TitleBarButtons.Add(lockButton);
        TitleBarButtons.Add(new TitleBarButton
        {
            AvailableClickthrough = false,
            Icon = FontAwesomeIcon.Bookmark,
            Click = _ => service.ChangeSettings(s => s.ShowSearchBar = !s.ShowSearchBar),
        });
    }

    public override void PreDraw()
    {
        WindowName = $"XA Nearby: {service.Snapshot.Players.Count}###XANearbyMini";
        UpdateSizeConstraints();
        Flags = service.Settings.WindowLocked ? ImGuiWindowFlags.NoResize : ImGuiWindowFlags.None;
        lockButton.Icon = service.Settings.WindowLocked ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen;
        base.PreDraw();
    }

    public void PrepareDraw() => IsOpen = service.WindowOpen;
    public override void OnClose() => service.WindowOpen = false;

    public override void PostDraw()
    {
        // ImGui closes the window before PostDraw, but Dalamud calls OnClose on the next draw.
        // Preserve the close now so PrepareDraw cannot reopen it from the previous service state.
        if (!IsOpen) service.WindowOpen = false;
        base.PostDraw();
    }

    private void UpdateSizeConstraints()
    {
        var scale = ImGuiHelpers.GlobalScale;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(230f * scale, 170f * scale),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(3, 2));
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(4, 2));
        try
        {
            if (!service.Enabled) { ImGui.TextWrapped("Enable XA Nearby in XA Mods."); return; }
            DrawNearby();
        }
        finally { ImGui.PopStyleVar(2); }
    }

    private string Job(uint id)
    {
        if (!jobs.TryGetValue(id, out var name)) jobs[id] = name = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().GetRowOrDefault(id)?.Abbreviation.ToString() ?? "?";
        return name;
    }

    private string World(uint id)
    {
        if (!worlds.TryGetValue(id, out var name)) worlds[id] = name = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>().GetRowOrDefault(id)?.Name.ToString() ?? "?";
        return name;
    }

    private static float Distance(PlayerObservation player, PlayerObservation? local)
        => local == null ? float.MaxValue : Vector3.Distance(local.Position, player.Position);

    private static bool IsFriend(PlayerObservation player)
        => (player.StatusFlags & Dalamud.Game.ClientState.Objects.Enums.StatusFlags.Friend) != 0;

    private unsafe void DrawNearby()
    {
        nearbyFrame = ImGui.GetFrameCount();
        if (service.Settings.ShowSearchBar)
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##Search", "Search name, class, FC or world...", ref query, 128);
        }
        else query = string.Empty; // Hiding search must never leave an invisible filter active.
        var cameraHeading = GetCameraHeading();
        var snapshot = service.Snapshot;
        searched = snapshot;
        var local = snapshot.LocalPlayer;
        matches.Clear();
        for (var i = 0; i < snapshot.Players.Count; i++)
        {
            var p = snapshot.Players[i];
            if (string.IsNullOrWhiteSpace(query) || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.CompanyTag.Contains(query, StringComparison.OrdinalIgnoreCase) || Job(p.JobId).Contains(query, StringComparison.OrdinalIgnoreCase)
                || World(p.HomeWorldId).Contains(query, StringComparison.OrdinalIgnoreCase)) matches.Add(i);
        }
        if (ImGui.BeginTable("Players", 5, ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable
            | ImGuiTableFlags.Sortable | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV,
            new Vector2(0, Math.Max(50, ImGui.GetContentRegionAvail().Y))))
        {
            try
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.NoHide);
                ImGui.TableSetupColumn("Distance", ImGuiTableColumnFlags.WidthFixed, 78 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("Class", ImGuiTableColumnFlags.DefaultHide | ImGuiTableColumnFlags.WidthFixed, 44 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("FC", ImGuiTableColumnFlags.DefaultHide | ImGuiTableColumnFlags.WidthFixed, 55 * ImGuiHelpers.GlobalScale);
                ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.DefaultHide | ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
                ImGui.TableHeadersRow();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Right-click table headings to choose columns.");
                var specs = ImGui.TableGetSortSpecs();
                var column = specs.SpecsCount > 0 ? specs.Specs.ColumnIndex : 0;
                var descending = specs.SpecsCount > 0 && specs.Specs.SortDirection == ImGuiSortDirection.Descending;
                matches.Sort((a, b) =>
                {
                    var left = snapshot.Players[a]; var right = snapshot.Players[b];
                    // Group friends before applying sort direction so descending never sends them to the bottom.
                    if (service.Settings.ShowFriendsOnTop && IsFriend(left) != IsFriend(right))
                        return IsFriend(left) ? -1 : 1;
                    var cmp = column switch
                    {
                        1 => Distance(left, local).CompareTo(Distance(right, local)),
                        2 => StringComparer.OrdinalIgnoreCase.Compare(Job(left.JobId), Job(right.JobId)),
                        3 => StringComparer.OrdinalIgnoreCase.Compare(left.CompanyTag, right.CompanyTag),
                        4 => StringComparer.OrdinalIgnoreCase.Compare(World(left.HomeWorldId), World(right.HomeWorldId)),
                        _ => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name),
                    };
                    if (cmp == 0) cmp = left.GameObjectId.CompareTo(right.GameObjectId);
                    return descending ? -cmp : cmp;
                });
                specs.SpecsDirty = false;
                foreach (var index in matches)
                {
                    var player = snapshot.Players[index];
                    ImGui.PushID(player.GameObjectId.ToString());
                    try
                    {
                        ImGui.TableNextRow();
                        if (ImGui.TableNextColumn())
                        {
                            var friend = IsFriend(player);
                            if (friend) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.78f, 0.48f, 1f, 1f));
                            try { ImGui.Selectable(player.Name, false, ImGuiSelectableFlags.SpanAllColumns); }
                            finally { if (friend) ImGui.PopStyleColor(); }
                            if (ImGui.IsItemHovered())
                            {
                                ImGui.SetTooltip(XAPeepData.FormatDisplayName(player.Name, player.HomeWorldId) + "\nRight-click for player actions.");
                            }
                            if (ImGui.BeginPopupContextItem("PlayerActions"))
                            {
                                try
                                {
                                    ImGui.TextUnformatted(XAPeepData.FormatDisplayName(player.Name, player.HomeWorldId));
                                    ImGui.Separator();
                                    if (ImGui.MenuItem("Locate on map")) service.Locate(player);
                                    if (ImGui.MenuItem("Target")) service.PlayerAction(player, NearbyPlayerAction.Target);
                                    if (ImGui.MenuItem("Examine")) service.PlayerAction(player, NearbyPlayerAction.Examine);
                                    if (ImGui.MenuItem("View adventurer plate")) service.PlayerAction(player, NearbyPlayerAction.AdventurerPlate);
                                    if (ImGui.MenuItem("Focus target")) service.PlayerAction(player, NearbyPlayerAction.FocusTarget);
                                    if (ImGui.MenuItem("Send tell")) service.PlayerAction(player, NearbyPlayerAction.Tell);
                                    if (ImGui.MenuItem("Invite to party")) service.PlayerAction(player, NearbyPlayerAction.Invite);
                                    if (ImGui.MenuItem("Copy name")) ImGui.SetClipboardText(XAPeepData.FormatDisplayName(player.Name, player.HomeWorldId));
                                }
                                finally { ImGui.EndPopup(); }
                            }
                        }
                        if (ImGui.TableNextColumn())
                        {
                            DrawDirection(player, local, cameraHeading);
                            ImGui.SameLine(0, 3);
                            ImGui.TextUnformatted(local == null ? "-" : $"{Distance(player, local):0} y");
                        }
                        if (ImGui.TableNextColumn()) ImGui.TextUnformatted(Job(player.JobId));
                        if (ImGui.TableNextColumn()) ImGui.TextUnformatted(player.CompanyTag);
                        if (ImGui.TableNextColumn()) ImGui.TextUnformatted(World(player.HomeWorldId));
                    }
                    finally { ImGui.PopID(); }
                }
            }
            finally { ImGui.EndTable(); }
        }
    }

    private static unsafe float? GetCameraHeading()
    {
        var manager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance();
        if (manager == null) return null;
        var camera = manager->GetActiveCamera();
        return camera != null && float.IsFinite(camera->DirH) ? camera->DirH : null;
    }

    private static void DrawDirection(PlayerObservation player, PlayerObservation? local, float? cameraHeading)
    {
        var size = ImGui.GetTextLineHeight();
        var center = ImGui.GetCursorScreenPos() + new Vector2(size / 2);
        ImGui.Dummy(new Vector2(size));
        if (local == null || !Finite(player.Position) || !Finite(local.Position) || cameraHeading == null) return;
        var delta = player.Position - local.Position;
        if (delta.X * delta.X + delta.Z * delta.Z < 0.01f) return;
        // Camera yaw zero looks toward -Z; project the ground direction into camera right/forward axes.
        var angle = MathF.Atan2(delta.X, delta.Z) - cameraHeading.Value - MathF.PI;
        var forward = new Vector2(-MathF.Sin(angle), -MathF.Cos(angle));
        var side = new Vector2(-forward.Y, forward.X);
        ImGui.GetWindowDrawList().AddTriangleFilled(center + forward * size * 0.45f,
            center - forward * size * 0.3f + side * size * 0.25f,
            center - forward * size * 0.3f - side * size * 0.25f, ImGui.GetColorU32(ImGuiCol.Text));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Direction relative to the camera");
    }

    private ISharedImmediateTexture? JobIcon(uint job)
    {
        if (job == 0 || job > 100) return null;
        if (!jobIcons.TryGetValue(job, out var icon)) jobIcons[job] = icon = Plugin.TextureProvider.GetFromGameIcon(62100 + job);
        return icon;
    }

    private bool CanDraw => service.Enabled && service.DisplayAvailable && service.Snapshot.LocalPlayer != null;

    public void DrawOverlay()
    {
        if (!CanDraw) return;
        drawn.Clear();
        var viewport = ImGui.GetMainViewport();
        var start = viewport.Pos + new Vector2(viewport.Size.X * 0.5f, viewport.Size.Y);
        var local = service.Snapshot.LocalPlayer!;
        if (Finite(local.Position) && Plugin.GameGui.WorldToScreen(local.Position, out var localScreen, out _) && Finite(localScreen)) start = localScreen;
        if (service.Settings.ShowNearbyLines && nearbyFrame == ImGui.GetFrameCount() && ReferenceEquals(searched, service.Snapshot))
        {
            foreach (var index in matches)
            {
                var player = service.Snapshot.Players[index];
                if (peep.HasTargeterVisual(player.GameObjectId)) continue;
                if (drawn.Add(player.GameObjectId)) DrawPlayer(player, start, false);
            }
        }
    }

    private void DrawPlayer(PlayerObservation player, Vector2 start, bool alert)
    {
        if (!Finite(player.Position)) return;
        Plugin.GameGui.WorldToScreen(player.Position, out var point, out var inView);
        if (!Finite(point)) return;
        var viewport = ImGui.GetMainViewport();
        var scale = ImGuiHelpers.GlobalScale * (float.IsFinite(service.Settings.Scale) ? Math.Max(0.1f, service.Settings.Scale) : 1f);
        if (!float.IsFinite(scale * 32)) return;
        var (end, marker) = LinePositions(point, inView, viewport.Pos, viewport.Size, scale);
        if (!Finite(end) || !Finite(marker)) return;
        var draw = ImGui.GetForegroundDrawList();
        var color = ImGui.GetColorU32(alert ? new Vector4(0.804f, 0.361f, 0.361f, 0.88f) : new Vector4(0, 0.749f, 1, 0.78f));
        var label = ImGui.GetColorU32(alert ? new Vector4(0.941f, 0.502f, 0.502f, 0.98f) : new Vector4(0.529f, 0.808f, 0.98f, 0.96f));
        var outline = ImGui.GetColorU32(new Vector4(0, 0, 0, 0.4f));
        draw.AddLine(start, end, outline, 5 * scale); draw.AddLine(start, end, color, 3 * scale);
        draw.AddCircleFilled(start, 3.5f * scale, outline, 16); draw.AddCircleFilled(start, 2.25f * scale, color, 16);
        if (inView)
        {
            draw.AddCircleFilled(marker, 5.5f * scale, outline, 16); draw.AddCircle(marker, 4 * scale, color, 16, 1.5f * scale);
            draw.AddCircleFilled(marker, 1.25f * scale, ImGui.GetColorU32(new Vector4(0.961f, 0.961f, 0.961f, 0.88f)), 16);
        }
        var text = player.Name;
        var iconSize = ImGui.GetTextLineHeight();
        var textSize = ImGui.CalcTextSize(text);
        var center = viewport.Pos + viewport.Size * 0.5f;
        var textAt = marker - new Vector2(marker.X >= center.X ? textSize.X + iconSize + 4 : 0, marker.Y >= center.Y ? textSize.Y : 0);
        var icon = JobIcon(player.JobId)?.GetWrapOrEmpty();
        if (icon != null) draw.AddImage(icon.Handle, textAt, textAt + new Vector2(iconSize));
        textAt.X += iconSize + 4;
        draw.AddText(textAt + Vector2.One, outline, text); draw.AddText(textAt, label, text);
    }

    internal static (Vector2 End, Vector2 Marker) LinePositions(Vector2 point, bool inView, Vector2 position, Vector2 size, float scale)
    {
        if (inView) return (point, point);
        var center = position + size * 0.5f;
        var direction = point - center;
        if (!Finite(direction) || direction.LengthSquared() < 0.001f) direction = Vector2.UnitY;
        var tx = Math.Abs(direction.X) > float.Epsilon ? Math.Max(size.X * 0.5f, 1) / Math.Abs(direction.X) : float.MaxValue;
        var ty = Math.Abs(direction.Y) > float.Epsilon ? Math.Max(size.Y * 0.5f, 1) / Math.Abs(direction.Y) : float.MaxValue;
        var edge = center + direction * Math.Min(tx, ty);
        var unit = Vector2.Normalize(direction);
        return (edge + unit * (32 * scale), edge - unit * (14 * scale));
    }

    private static bool Finite(Vector2 p) => float.IsFinite(p.X) && float.IsFinite(p.Y);
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
