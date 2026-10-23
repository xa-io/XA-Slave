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
// instead of peer messages. Tony invites one owner, collects, then supplies its tell request.
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
            return (cfg.XagmanOnhFriendFoCharacters, global::XASlave.Localization.UiMessage.Key("SA00EB9D8A982EB59").Display);
        return (cfg.XagmanOnhFriendTonyCharacters, global::XASlave.Localization.UiMessage.Key("SD6F12FCBD66A9823").Display);
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
        if (global::XASlave.Localization.UiNative.Checkbox((global::XASlave.Localization.UiLocale.Native("S590E803E0812A76C") + "##xagmanOnh"), ref onh))
        {
            cfg.XagmanOutsideNetworkHelper = onh;
            cfg.Save();
            // Enabling ONH means there is no peer network: ensure the local peer service is disconnected.
            if (onh && plugin.XagmanPeers.IsStarted)
                plugin.SetXagmanPeerConnectionsEnabled(false);
        }
        ImGui.SameLine();
        ImGui.TextDisabled((global::XASlave.Localization.UiMessage.Key("S86303AA595DE54D3")).Display);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                global::XASlave.Localization.UiLocale.Get("SB6EA6D7BD0D682F4"));
        }
        if (cfg.XagmanOutsideNetworkHelper)
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S7F139DDB3CDD36B3"));
    }

    private void DrawXagmanOnhExportRow(Configuration cfg)
    {
        var selectedCount = GetXagmanOnhSelectedOwnCharacterNames(cfg).Count;
        if (global::XASlave.Localization.UiNative.Button((string.Format(global::XASlave.Localization.UiLocale.Native("S4580ADD9AEFD95DE"), selectedCount) + $"##xagmanOnhExport"), display: global::XASlave.Localization.UiLocale.FormatKey("S4580ADD9AEFD95DE", selectedCount)))
            ExportXagmanOnhSelectedCharacters(cfg);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("S566D8CBA3D840FEF"));
    }

    private void ExportXagmanOnhSelectedCharacters(Configuration cfg)
    {
        var names = NormalizeXagmanOnhCharacterNames(GetXagmanOnhSelectedOwnCharacterNames(cfg));
        if (names.Count == 0)
        {
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Key("S76C108BB7BDD8C01"));
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
        arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Format("S7C575C885AB97873", names.Count, cfg.XagmanRole));
        arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
    }

    private bool TryImportXagmanOnhFriendCharacters(Configuration cfg, out global::XASlave.Localization.UiMessage message)
    {
        var raw = ImGui.GetClipboardText() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            message = global::XASlave.Localization.UiMessage.Key("S361224C8378ABCC6");
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
                    message = global::XASlave.Localization.UiMessage.Key("S0F789A51D03C5F49");
                    return false;
                }
                names = package.Characters;
                exporterRole = package.Role;
            }
            catch (Exception ex)
            {
                message = global::XASlave.Localization.UiMessage.Format("SB6AB054EEB7D8525", ex.Message);
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
            message = global::XASlave.Localization.UiMessage.Key("S49666CC97422B5B6");
            return false;
        }

        var (list, _) = GetXagmanOnhFriendContext(cfg);
        list.Clear();
        list.AddRange(names);
        cfg.Save();

        var directionNote = global::XASlave.Localization.UiMessage.Raw(string.Empty);
        if (!string.IsNullOrWhiteSpace(exporterRole) && Enum.TryParse<XagmanRole>(exporterRole, true, out var parsedRole))
        {
            var expected = cfg.XagmanRole == XagmanRole.Tony ? XagmanRole.FranchiseOwner : XagmanRole.Tony;
            if (parsedRole != expected)
                directionNote = global::XASlave.Localization.UiMessage.Format("SAD171453EDD14538", parsedRole, cfg.XagmanRole, expected);
        }
        message = global::XASlave.Localization.UiMessage.Format("S29664E16D485ECD7", names.Count, directionNote);
        return true;
    }

    private void DrawXagmanOnhFriendListSection(Configuration cfg)
    {
        var (list, label) = GetXagmanOnhFriendContext(cfg);
        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), label);
        ImGui.TextDisabled(((cfg.XagmanRole == XagmanRole.Tony ? global::XASlave.Localization.UiMessage.Key("S34B17FE52223FF18") : global::XASlave.Localization.UiMessage.Key("S1F805080DAB492FD"))).Display);

        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("SE50BF6DD11D7F47E") + "##xagmanOnhFriendImport")))
        {
            TryImportXagmanOnhFriendCharacters(cfg, out var msg);
            arImportStatus = global::XASlave.Localization.UiMessage.Store(ref arImportStatusUiMessage, global::XASlave.Localization.UiMessage.Capture(msg, msg.Original));
            arImportStatusExpiry = DateTime.UtcNow.AddSeconds(8);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(global::XASlave.Localization.UiLocale.Get("SB7565C8167E77A43"));
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S83B12C2216EFB4FD") + "##xagmanOnhFriendClear")))
        {
            list.Clear();
            cfg.Save();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Scale(200f));
        ImGui.InputTextWithHint("##xagmanOnhFriendAdd", global::XASlave.Localization.UiLocale.Get("S3014DF7679CBA947"), ref xagmanOnhFriendAddBuffer, 64);
        ImGui.SameLine();
        if (global::XASlave.Localization.UiNative.Button((global::XASlave.Localization.UiLocale.Native("S9FD728C66C9A256B") + "##xagmanOnhFriendAddBtn")) && !string.IsNullOrWhiteSpace(xagmanOnhFriendAddBuffer))
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
            ImGui.TextColored(GetXagmanStatusColor(arImportStatus), global::XASlave.Localization.UiMessage.Resolve(ref arImportStatusUiMessage, arImportStatus));
        }

        using (var imguiScope236 = ImRaii.Table("xagmanOnhFriendTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, ScaledVector(0f, 150f)))
        if (imguiScope236)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S1405A63E8A78B85C"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, Scale(30f));
            ImGui.TableSetupScrollFreeze(0, 1);
            global::XASlave.Localization.UiNative.TableHeadersRow();
            for (var i = 0; i < list.Count; i++)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(list[i]);
                ImGui.TableNextColumn();
                using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f)))
                {
                    if (global::XASlave.Localization.UiNative.SmallButton((global::XASlave.Localization.UiLocale.Native("S4B68AB3847FEDA7D") + $"##xagmanOnhFriendRm{i}")))
                    {
                        list.RemoveAt(i);
                        cfg.Save();
                        break;
                    }
                }
            }

        }
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("SCFD020B242DFDEF9", list.Count));
    }

    // Show imported names alongside the live offline queue and run-local outcomes.
    private void DrawXagmanOnhQueueView(Configuration cfg)
    {
        var (list, label) = GetXagmanOnhFriendContext(cfg);
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.FormatKey("SAD1C68924E3212A6", label));
        if (list.Count == 0)
        {
            ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S18A65816B1963195"));
            return;
        }
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S3B9F6E0AF28BBA41"));
        using (var imguiScope275 = ImRaii.Table("xagmanOnhQueueTable", 3,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable |
            ImGuiTableFlags.Hideable | ImGuiTableFlags.ContextMenuInBody, ScaledVector(0f, 150f)))
        if (imguiScope275)
        {
            global::XASlave.Localization.UiNative.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, Scale(30f));
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S1405A63E8A78B85C"), ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoHide);
            global::XASlave.Localization.UiNative.TableSetupColumn(global::XASlave.Localization.UiLocale.Native("S920E413C7D411B61"), ImGuiTableColumnFlags.WidthFixed, Scale(110f));
            ImGui.TableSetupScrollFreeze(0, 1);
            global::XASlave.Localization.UiNative.TableHeadersRow();
            for (var i = 0; i < list.Count; i++)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextDisabled((i + 1).ToString(CultureInfo.InvariantCulture));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(list[i]);
                ImGui.TableNextColumn();
                var name = GetCharacterNameFromKey(list[i]);
                var position = onhWaitingOwners.FindIndex(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
                var status = ((onhFailedOwners.Contains(name) ? global::XASlave.Localization.UiMessage.Key("S031A8F0F659DF890") : (onhRetiredTonys.Contains(name) ? global::XASlave.Localization.UiMessage.Key("S9280DCBD5E5A2D4A") : (xagmanOnhCompletedPartners.Contains(name) ? global::XASlave.Localization.UiMessage.Key("S22A970D2E5B1CC23") : (name.Equals(xagmanOnhEngagedPartner, StringComparison.OrdinalIgnoreCase) ? global::XASlave.Localization.UiMessage.Key("S92340695899BD2D8") : (position >= 0 ? global::XASlave.Localization.UiMessage.Format("SBBF393609432CA35", position + 1) : global::XASlave.Localization.UiMessage.Key("S331551B0DE4157C9"))))))).Display;
                ImGui.TextDisabled(status);
            }

        }
        ImGui.TextDisabled(global::XASlave.Localization.UiLocale.Get("S5B08ACEA1AB7D3AD"));
    }
}
