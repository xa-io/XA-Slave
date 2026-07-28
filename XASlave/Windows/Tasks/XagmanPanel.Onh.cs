using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using XASlave.Data;

namespace XASlave.Windows;

// Xagman "Outside Network Helper" (ONH) support.
//
// Normal Xagman coordinates Tony and Franchise Owner clients over a local TCP peer hub
// (XagmanPeerService). ONH mode is for two DIFFERENT players on DIFFERENT machines with no
// shared peer network: coordination happens in game via proximity + 1-gil trade handshakes
// instead of peer messages. Franchise Owner gives items; Tony only receives.
//
// This file holds the additive UI + clipboard character-list plumbing. The runtime handshake
// state machine (1-gil start/resume + done signals, proximity movement, gil/inventory
// monitoring, Tony rotation on full inventory) lives in XagmanPanel.OnhRuntime.cs and reuses the
// existing Dropbox/inventory/movement/relog/travel helpers - the networked trading path is
// not modified.
public partial class SlaveWindow
{
    private string xagmanOnhFriendAddBuffer = string.Empty;

    // Serializable clipboard payload for sharing a selected-character roster between partners.
    // Role is the EXPORTER's role: a Tony exports Tony characters (imported into the partner FO's
    // "Tony List"); a Franchise Owner exports FO characters (imported into the partner Tony's "FO List").
    private sealed class XagmanOnhCharacterListPackage
    {
        public int SchemaVersion { get; set; } = 1;
        public string Kind { get; set; } = "xagman-onh-characters";
        public string Role { get; set; } = string.Empty;
        public DateTime ExportedAtUtc { get; set; }
        public List<string> Characters { get; set; } = new();
    }

    // Returns the imported friend roster for the current role, plus a display label.
    // Tony holds the partner's Franchise Owner list; Franchise Owner holds the partner's Tony list.
    private (List<string> List, string Label) GetXagmanOnhFriendContext(Configuration cfg)
    {
        if (cfg.XagmanRole == XagmanRole.Tony)
            return (cfg.XagmanOnhFriendFoCharacters, "Franchise Owner List (imported)");
        return (cfg.XagmanOnhFriendTonyCharacters, "Tony List (imported)");
    }

    private List<string> GetXagmanOnhSelectedOwnCharacterNames(Configuration cfg)
    {
        if (cfg.XagmanRole == XagmanRole.Tony)
            return GetSelectedXagmanTonyCharacters()
                .Select(entry => entry.CharacterNameWorld)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
        return GetSelectedXagmanFranchiseCharacters()
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
    }

    private static List<string> NormalizeXagmanOnhCharacterNames(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in names)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var name = raw.Trim();
            if (name.Length == 0)
                continue;
            if (seen.Add(name))
                result.Add(name);
        }
        return result;
    }

    private void DrawXagmanOnhModeCheckbox(Configuration cfg)
    {
        var onh = cfg.XagmanOutsideNetworkHelper;
        if (ImGui.Checkbox("Outside Network Helper##xagmanOnh", ref onh))
        {
            cfg.XagmanOutsideNetworkHelper = onh;
            cfg.Save();
            // Enabling ONH means there is no peer network: ensure the local peer service is disconnected.
            if (onh && plugin.XagmanPeers.IsStarted)
                plugin.SetXagmanPeerConnectionsEnabled(false);
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Outside Network Helper: transfer items with another player who is NOT on your peer network\n" +
                "(a different person on a different PC). There is no peer connection - you coordinate in game.\n" +
                "Franchise Owner gives items; Tony only receives. A 1-gil trade is the ready/done handshake.\n" +
                "Export your selected characters and send the list to your partner; import the list they send you.\n" +
                "Each side picks their own meet world and location. Peer networking is disabled in this mode.");
        }
        if (cfg.XagmanOutsideNetworkHelper)
            ImGui.TextDisabled("Peer networking disabled. Import/export character lists below and set your own meet world + location.");
    }

    private void DrawXagmanOnhExportRow(Configuration cfg)
    {
        var selectedCount = GetXagmanOnhSelectedOwnCharacterNames(cfg).Count;
        if (ImGui.Button($"Export Selected Characters ({selectedCount})##xagmanOnhExport"))
            ExportXagmanOnhSelectedCharacters(cfg);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Copy your selected characters to the clipboard as an Outside Network Helper list.\nSend it to your partner so they can import it.");
    }

    private void ExportXagmanOnhSelectedCharacters(Configuration cfg)
    {
        var names = NormalizeXagmanOnhCharacterNames(GetXagmanOnhSelectedOwnCharacterNames(cfg));
        if (names.Count == 0)
        {
            arImportStatus = "Xagman ONH: select at least one character to export.";
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
            return;
        }
        var package = new XagmanOnhCharacterListPackage
        {
            Role = cfg.XagmanRole.ToString(),
            ExportedAtUtc = DateTime.UtcNow,
            Characters = names,
        };
        var json = JsonSerializer.Serialize(package, xagmanItemListJsonOptions);
        ImGui.SetClipboardText(json);
        arImportStatus = $"Xagman ONH: copied {names.Count} {cfg.XagmanRole} character(s) to clipboard.";
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
    }

    private bool TryImportXagmanOnhFriendCharacters(Configuration cfg, out string message)
    {
        var raw = ImGui.GetClipboardText() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            message = "Xagman ONH: clipboard is empty.";
            return false;
        }

        List<string> names;
        string? exporterRole = null;
        if (raw.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                var package = JsonSerializer.Deserialize<XagmanOnhCharacterListPackage>(raw, xagmanItemListJsonOptions);
                if (package?.Characters == null || package.Characters.Count == 0)
                {
                    message = "Xagman ONH: clipboard JSON had no characters.";
                    return false;
                }
                names = package.Characters;
                exporterRole = package.Role;
            }
            catch (Exception ex)
            {
                message = $"Xagman ONH: import failed - {ex.Message}";
                return false;
            }
        }
        else
        {
            names = raw
                .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        names = NormalizeXagmanOnhCharacterNames(names);
        if (names.Count == 0)
        {
            message = "Xagman ONH: no valid character names found.";
            return false;
        }

        var (list, _) = GetXagmanOnhFriendContext(cfg);
        list.Clear();
        list.AddRange(names);
        cfg.Save();

        var directionNote = string.Empty;
        if (!string.IsNullOrWhiteSpace(exporterRole) && Enum.TryParse<XagmanRole>(exporterRole, true, out var parsedRole))
        {
            var expected = cfg.XagmanRole == XagmanRole.Tony ? XagmanRole.FranchiseOwner : XagmanRole.Tony;
            if (parsedRole != expected)
                directionNote = $" (warning: list role was {parsedRole}, but a {cfg.XagmanRole} should import a {expected} list)";
        }
        message = $"Xagman ONH: imported {names.Count} character(s){directionNote}.";
        return true;
    }

    private void DrawXagmanOnhFriendListSection(Configuration cfg)
    {
        var (list, label) = GetXagmanOnhFriendContext(cfg);
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), label);
        ImGui.TextDisabled(cfg.XagmanRole == XagmanRole.Tony
            ? "Franchise Owner characters you will receive items from. Tony processes one at a time and never gives items (1-gil handshake only)."
            : "Tony characters you can give items to. Give your items to whichever Tony from this list is at the meet spot.");

        if (ImGui.Button("Import from Clipboard##xagmanOnhFriendImport"))
        {
            TryImportXagmanOnhFriendCharacters(cfg, out var msg);
            arImportStatus = msg;
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Import a partner's character list from the clipboard (XA Slave ONH JSON, or one Name@World per line).");
        ImGui.SameLine();
        if (ImGui.Button("Clear##xagmanOnhFriendClear"))
        {
            list.Clear();
            cfg.Save();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Scale(200f));
        ImGui.InputTextWithHint("##xagmanOnhFriendAdd", "Add Name@World", ref xagmanOnhFriendAddBuffer, 64);
        ImGui.SameLine();
        if (ImGui.Button("Add##xagmanOnhFriendAddBtn") && !string.IsNullOrWhiteSpace(xagmanOnhFriendAddBuffer))
        {
            var name = xagmanOnhFriendAddBuffer.Trim();
            if (!list.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(name);
                cfg.Save();
            }
            xagmanOnhFriendAddBuffer = string.Empty;
        }
        if (!string.IsNullOrEmpty(arImportStatus) && DateTime.UtcNow < arImportStatusExpiry)
        {
            ImGui.SameLine();
            ImGui.TextColored(GetXagmanStatusColor(arImportStatus), arImportStatus);
        }

        if (ImGui.BeginTable("xagmanOnhFriendTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, ScaledVector(0f, 150f)))
        {
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, Scale(30f));
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            for (var i = 0; i < list.Count; i++)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(list[i]);
                ImGui.TableNextColumn();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
                if (ImGui.SmallButton($"X##xagmanOnhFriendRm{i}"))
                {
                    list.RemoveAt(i);
                    cfg.Save();
                    ImGui.PopStyleColor();
                    break;
                }
                ImGui.PopStyleColor();
            }
            ImGui.EndTable();
        }
        ImGui.TextDisabled($"{list.Count} character(s) imported.");
    }

    // The queue section is repurposed to show the imported partner roster. The handshake runtime
    // (XagmanPanel.OnhRuntime.cs) drives live status via xagmanStatusText / the task log; per-row
    // completion marks in this table are not wired yet.
    private void DrawXagmanOnhQueueView(Configuration cfg)
    {
        var (list, label) = GetXagmanOnhFriendContext(cfg);
        ImGui.TextDisabled($"Outside Network Helper queue - {label}");
        if (list.Count == 0)
        {
            ImGui.TextDisabled("No imported characters yet. Import a partner list in the section above.");
            return;
        }
        if (ImGui.BeginTable("xagmanOnhQueueTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, ScaledVector(0f, 150f)))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, Scale(30f));
            ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, Scale(110f));
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            for (var i = 0; i < list.Count; i++)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextDisabled((i + 1).ToString(CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(list[i]);
                ImGui.TableNextColumn();
                ImGui.TextDisabled("Pending");
            }
            ImGui.EndTable();
        }
        ImGui.TextDisabled("Live handshake status shows in the task status line and log; per-row completion marks are not wired yet.");
    }
}
