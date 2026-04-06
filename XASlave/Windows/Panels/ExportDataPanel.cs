using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Microsoft.Data.Sqlite;
using XASlave.Data;

namespace XASlave.Windows;

/// <summary>
/// Export Data panel — partial class split from SlaveWindow.
/// Reference-tab panel that exports a timestamped multi-character table file
/// from AutoRetainer, Lifestream, and XA Database data.
/// </summary>
public partial class SlaveWindow
{
    // ───────────────────────────────────────────────
    //  Export Data — constants
    // ───────────────────────────────────────────────
    private static readonly int[] ExportIntervalOptions = { 0, 6, 12, 24, 48, 72 };
    private static readonly string[] ExportIntervalLabels = { "Always", "6 hours", "12 hours", "24 hours", "48 hours", "72 hours" };
    private const string ExportTaskName = "Export Data";
    private const string ExportTimestampToken = "{timestamp}";
    private const uint ExportVentureCofferItemId = 32161;

    private static readonly string[] ExportBaseHeaderColumns =
    {
        "Name", "World", "Region", "GIL", "RGIL", "TRS", "FCP", "MGP", "VC", "CF", "MRK", "HJL", "leveA", "GCR", "FCR", "FCS", "FCL", "S1", "S2", "S3", "S4",
        "fcsize", "fcdistrict", "fcward", "fcplot", "pfcsize", "pfcdistrict", "pfcward", "pfcplot"
    };

    private static readonly Dictionary<uint, int> ExportTreasureValues = new()
    {
        { 22500, 8000 },
        { 22501, 9000 },
        { 22502, 10000 },
        { 22503, 13000 },
        { 22504, 27000 },
        { 22505, 28500 },
        { 22506, 30000 },
        { 22507, 34500 },
    };

    private static readonly Dictionary<int, string> ExportResidentialDistrictNames = new()
    {
        { 2, "The Lavender Beds" },
        { 8, "Mist" },
        { 9, "The Goblet" },
        { 70, "Empyreum" },
        { 111, "Shirogane" },
    };

    private static readonly string[] ExportVoyagePartShortcodes = { "S", "U", "W", "C", "Y", "S+", "U+", "W+", "C+", "Y+" };

    private static readonly Regex ExportPlotNumberRegex = new(@"\bPlot\s+(?<plot>\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExportWardNumberRegex = new(@"\b(?:Ward\s+(?<wardAfter>\d+)|(?<wardBefore>\d+)(?:st|nd|rd|th)\s+Ward)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExportParentheticalTextRegex = new(@"\s*\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex ExportOwnerSuffixRegex = new(@"\s*\[[^\]]+\]\s*$", RegexOptions.Compiled);
    private static readonly Regex ExportWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex ExportTimestampTokenRegex = new(@"\{timestamp\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ───────────────────────────────────────────────
    //  Export Data — instance state
    // ───────────────────────────────────────────────
    private readonly JsonSerializerOptions exportJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private string exportStatusMessage = string.Empty;
    private Vector4 exportStatusColor = new(0.4f, 1.0f, 0.4f, 1.0f);
    private DateTime exportStatusExpiryUtc = DateTime.MinValue;
    private DateTime exportNextAutomaticAttemptUtc = DateTime.MinValue;
    private DateTime exportLastFrameworkTickUtc = DateTime.MinValue;
    private ulong exportLastAlwaysOnRunContentId;
    private string exportPluginConfigsBasePath = string.Empty;
    private bool exportInitialized;

    // ───────────────────────────────────────────────
    //  Export Data — lifecycle
    // ───────────────────────────────────────────────
    private void InitializeExportData()
    {
        if (exportInitialized)
            return;

        var autoRetainerPath = plugin.ArConfigReader.GetAutoRetainerConfigPath();
        var configDir = Plugin.PluginInterface.GetPluginConfigDirectory();
        exportPluginConfigsBasePath = Path.GetDirectoryName(Path.GetDirectoryName(autoRetainerPath) ?? configDir) ?? configDir;

        // Migrate legacy separate-file settings into Configuration if output path is still empty
        MigrateExportDataLegacySettings();

        exportInitialized = true;
    }

    private void MigrateExportDataLegacySettings()
    {
        var cfg = plugin.Configuration;
        if (!string.IsNullOrWhiteSpace(cfg.ExportDataOutputPath))
            return;

        var configDir = Plugin.PluginInterface.GetPluginConfigDirectory();
        var legacyPaths = new[]
        {
            Path.Combine(configDir, "ExportData.settings.json"),
        };

        foreach (var legacyPath in legacyPaths)
        {
            if (!File.Exists(legacyPath))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(legacyPath));
                var root = doc.RootElement;

                if (root.TryGetProperty("AlwaysOn", out var alwaysOn) && alwaysOn.ValueKind == JsonValueKind.True)
                    cfg.ExportDataAlwaysOn = true;

                if (root.TryGetProperty("RunEveryHours", out var runEvery) && runEvery.TryGetInt32(out var hours))
                    cfg.ExportDataRunEveryHours = ExportNormalizeInterval(hours);

                if (root.TryGetProperty("OutputPath", out var outputPath) && outputPath.ValueKind == JsonValueKind.String)
                    cfg.ExportDataOutputPath = outputPath.GetString()?.Trim() ?? string.Empty;

                if (root.TryGetProperty("LastSuccessfulRunUtc", out var lastRun) && lastRun.ValueKind == JsonValueKind.String)
                    cfg.ExportDataLastSuccessfulRunUtc = lastRun.GetString() ?? string.Empty;

                cfg.Save();
                break;
            }
            catch { /* ignore corrupt legacy files */ }
        }
    }

    private void OnExportDataFrameworkTick()
    {
        if (!plugin.Configuration.ExportDataAlwaysOn || DateTime.UtcNow < exportNextAutomaticAttemptUtc)
            return;

        if ((DateTime.UtcNow - exportLastFrameworkTickUtc).TotalSeconds < 2)
            return;

        exportLastFrameworkTickUtc = DateTime.UtcNow;

        if (!Plugin.PlayerState.IsLoaded || string.IsNullOrWhiteSpace(plugin.Configuration.ExportDataOutputPath) || !File.Exists(GetExportAutoRetainerConfigPath()))
            return;

        var runEveryHours = plugin.Configuration.ExportDataRunEveryHours;
        if (runEveryHours == 0)
        {
            var contentId = Plugin.PlayerState.ContentId;
            if (contentId == 0 || contentId == exportLastAlwaysOnRunContentId)
                return;

            if (ExportTryWriteSnapshot("Always"))
                exportLastAlwaysOnRunContentId = contentId;

            exportNextAutomaticAttemptUtc = DateTime.UtcNow.AddSeconds(30);
            return;
        }

        var lastRunUtc = ExportParseUtc(plugin.Configuration.ExportDataLastSuccessfulRunUtc);
        if (lastRunUtc.HasValue && DateTime.UtcNow - lastRunUtc.Value < TimeSpan.FromHours(runEveryHours))
            return;

        ExportTryWriteSnapshot("Scheduled");
        exportNextAutomaticAttemptUtc = DateTime.UtcNow.AddMinutes(1);
    }

    private void OnExportDataLogin()
    {
        exportLastAlwaysOnRunContentId = 0;
        exportNextAutomaticAttemptUtc = DateTime.UtcNow.AddSeconds(10);
    }

    private void OnExportDataLogoutHandler(int type, int code)
    {
        exportLastAlwaysOnRunContentId = 0;
    }

    // ───────────────────────────────────────────────
    //  Export Data — Draw
    // ───────────────────────────────────────────────
    private void DrawExportData()
    {
        InitializeExportData();

        var cyan = new Vector4(0.4f, 0.8f, 1.0f, 1.0f);
        var green = new Vector4(0.4f, 1.0f, 0.4f, 1.0f);
        var yellow = new Vector4(1.0f, 0.8f, 0.3f, 1.0f);
        var red = new Vector4(1.0f, 0.4f, 0.4f, 1.0f);

        var arConfigExists = File.Exists(GetExportAutoRetainerConfigPath());
        var lifestreamExists = File.Exists(GetExportLifestreamConfigPath());
        var xaDatabaseAvailable = plugin.IpcClient.IsXaDatabaseAvailable() && File.Exists(plugin.IpcClient.GetDbPath());

        ImGui.TextColored(cyan, ExportTaskName);
        ImGui.TextDisabled("Reference export panel for persisted character data.");
        ImGui.TextDisabled("Writes a timestamped table file using AutoRetainer, Lifestream, and XA Database data when available.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text("Required:");
        ImGui.SameLine();
        ImGui.TextColored(arConfigExists ? green : red, arConfigExists ? "[AutoRetainer Config]" : "[AutoRetainer Config ×]");
        ImGui.SameLine();
        ImGui.Text("Optional:");
        ImGui.SameLine();
        ImGui.TextColored(lifestreamExists ? green : yellow, lifestreamExists ? "[Lifestream]" : "[Lifestream -]");
        ImGui.SameLine();
        ImGui.TextColored(xaDatabaseAvailable ? green : yellow, xaDatabaseAvailable ? "[XA Database]" : "[XA Database -]");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var cfg = plugin.Configuration;

        var alwaysOn = cfg.ExportDataAlwaysOn;
        if (ImGui.Checkbox("Always On", ref alwaysOn))
        {
            cfg.ExportDataAlwaysOn = alwaysOn;
            cfg.Save();
            ExportResetSchedulingState();
        }

        var intervalIndex = ExportGetIntervalIndex(cfg.ExportDataRunEveryHours);
        ImGui.SetNextItemWidth(220f);
        if (ImGui.Combo("Run Every", ref intervalIndex, ExportIntervalLabels, ExportIntervalLabels.Length))
        {
            cfg.ExportDataRunEveryHours = ExportIntervalOptions[intervalIndex];
            cfg.Save();
            ExportResetSchedulingState();
        }

        var outputPath = cfg.ExportDataOutputPath;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("Folder / File To Write", ref outputPath, 512))
            cfg.ExportDataOutputPath = outputPath;

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            cfg.ExportDataOutputPath = cfg.ExportDataOutputPath.Trim();
            cfg.Save();
            ExportResetSchedulingState();
        }

        ImGui.TextDisabled($"Folder path: creates ExportData_{ExportTimestampToken}.tsv.");
        ImGui.TextDisabled($"File path: use {ExportTimestampToken} inside the file name for pre/post tags.");
        ImGui.TextDisabled(@"Example: E:\gil\pre_{timestamp}_post.tsv");

        var canWriteNow = arConfigExists && !string.IsNullOrWhiteSpace(cfg.ExportDataOutputPath);
        if (!canWriteNow)
            ImGui.BeginDisabled();

        if (ImGui.Button("Write New File Now"))
            ExportTryWriteSnapshot("Manual");

        if (!canWriteNow)
            ImGui.EndDisabled();

        ImGui.SameLine();

        var folderToOpen = ExportResolveOutputFolder(cfg.ExportDataOutputPath);
        var folderExists = !string.IsNullOrWhiteSpace(folderToOpen) && Directory.Exists(folderToOpen);
        if (!folderExists)
            ImGui.BeginDisabled();

        if (ImGui.Button("Open Folder"))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folderToOpen!, UseShellExecute = true }); }
            catch { /* ignore if shell launch fails */ }
        }

        if (!folderExists)
            ImGui.EndDisabled();

        ImGui.Spacing();

        if (string.IsNullOrWhiteSpace(cfg.ExportDataOutputPath))
        {
            ImGui.TextColored(red, "Enter folder location to save data.");
            ImGui.TextDisabled($"You can also enter a full file path and place {ExportTimestampToken} in the file name.");
        }
        else
            ImGui.TextDisabled($"Output base: {cfg.ExportDataOutputPath}");

        if (!string.IsNullOrWhiteSpace(cfg.ExportDataLastSuccessfulRunUtc) && ExportParseUtc(cfg.ExportDataLastSuccessfulRunUtc).HasValue)
        {
            var lastRun = ExportParseUtc(cfg.ExportDataLastSuccessfulRunUtc)!.Value.ToLocalTime();
            ImGui.TextDisabled($"Last successful write: {lastRun:yyyy-MM-dd HH:mm:ss}");
        }
        else
        {
            ImGui.TextDisabled("Last successful write: never");
        }

        ImGui.TextDisabled(cfg.ExportDataRunEveryHours == 0
            ? "Interval 0 behaves as an always-on login/session export instead of a repeating per-frame write."
            : $"Automatic writes trigger when the last successful {ExportTaskName} export is older than {ExportFormatIntervalLabel(cfg.ExportDataRunEveryHours)}.");
        ImGui.TextDisabled("leveA prefers XA Database Journal data from currencies_json and falls back to persisted AutoRetainer fields.");

        if (!string.IsNullOrEmpty(exportStatusMessage) && DateTime.UtcNow < exportStatusExpiryUtc)
        {
            ImGui.Spacing();
            ImGui.TextColored(exportStatusColor, exportStatusMessage);
        }
    }

    // ───────────────────────────────────────────────
    //  Export Data — snapshot write
    // ───────────────────────────────────────────────
    private bool ExportTryWriteSnapshot(string trigger)
    {
        try
        {
            var cfg = plugin.Configuration;
            if (string.IsNullOrWhiteSpace(cfg.ExportDataOutputPath))
            {
                ExportSetStatus($"{ExportTaskName} could not start because the output path is blank.", new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
                return false;
            }

            var rows = ExportBuildRows();
            if (rows.Count == 0)
            {
                ExportSetStatus($"{ExportTaskName} found no characters to export from AutoRetainer DefaultConfig.json.", new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
                return false;
            }

            var outputFilePath = ExportBuildOutputFilePath(cfg.ExportDataOutputPath, DateTime.UtcNow);
            var delimiter = string.Equals(Path.GetExtension(outputFilePath), ".csv", StringComparison.OrdinalIgnoreCase) ? "," : "\t";
            var payload = ExportBuildDelimitedOutput(rows, delimiter);

            File.WriteAllText(outputFilePath, payload, new UTF8Encoding(false));

            cfg.ExportDataLastSuccessfulRunUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            cfg.Save();

            if (Plugin.PlayerState.IsLoaded && cfg.ExportDataRunEveryHours == 0)
                exportLastAlwaysOnRunContentId = Plugin.PlayerState.ContentId;

            ExportSetStatus($"{ExportTaskName} {trigger} write complete: {rows.Count} row(s) -> {outputFilePath}", new Vector4(0.4f, 1.0f, 0.4f, 1.0f));
            return true;
        }
        catch (Exception ex)
        {
            ExportSetStatus($"{ExportTaskName} export failed: {ex.Message}", new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
            return false;
        }
    }

    // ───────────────────────────────────────────────
    //  Export Data — row building
    // ───────────────────────────────────────────────
    private List<ExportRow> ExportBuildRows()
    {
        var autoRetainerCharacters = ExportLoadAutoRetainerCharacters();
        var housingMap = ExportLoadLifestreamHousing();
        var snapshotMap = ExportLoadXaSnapshotSupplements();

        return autoRetainerCharacters
            .OrderBy(character => WorldData.GetSortKey(character.World))
            .ThenBy(character => character.Name, StringComparer.OrdinalIgnoreCase)
            .Select(character =>
            {
                snapshotMap.TryGetValue(character.ContentId, out var snapshot);
                housingMap.TryGetValue(character.ContentId, out var mappedHousing);

                var fcHousing = ExportResolveHousing(snapshot?.FcEstate ?? string.Empty, mappedHousing?.Fc);
                var personalHousing = ExportResolveHousing(snapshot?.PersonalEstate ?? string.Empty, mappedHousing?.Private);
                var sharedHousing = snapshot?.SharedEstates ?? new List<ExportSharedHousingEntry>();
                var submarines = snapshot?.Submarines ?? Array.Empty<string>();

                return new ExportRow
                {
                    Name = character.Name,
                    World = character.World,
                    Region = WorldData.Worlds.FirstOrDefault(w => w.Name == character.World)?.Region ?? string.Empty,
                    Gil = character.Gil,
                    Rgil = snapshot?.RetainerGil > 0 ? snapshot.RetainerGil : character.RetainerGil,
                    Trs = snapshot?.TreasureValue ?? 0,
                    Fcp = snapshot?.FcPoints > 0 ? snapshot.FcPoints : character.FcPoints,
                    Mgp = snapshot?.Mgp ?? 0,
                    Vc = snapshot?.VentureCoffers > 0 ? snapshot.VentureCoffers : character.VentureCoffers,
                    Cf = character.Ceruleum,
                    Mrk = character.RepairKits,
                    Hjl = snapshot?.HighestJobLevel > 0 ? snapshot.HighestJobLevel : character.HighestJobLevel,
                    Gcr = character.GcRank,
                    Fcr = snapshot?.FcRank ?? 0,
                    Fcs = snapshot?.FcTag ?? string.Empty,
                    Fcl = !string.IsNullOrWhiteSpace(snapshot?.FcName) ? snapshot!.FcName : character.FcName,
                    LeveA = snapshot?.LeveAllowances ?? character.LeveAllowances,
                    S1 = submarines.Length > 0 ? submarines[0] : string.Empty,
                    S2 = submarines.Length > 1 ? submarines[1] : string.Empty,
                    S3 = submarines.Length > 2 ? submarines[2] : string.Empty,
                    S4 = submarines.Length > 3 ? submarines[3] : string.Empty,
                    Fcsize = fcHousing?.Size ?? string.Empty,
                    Fcdistrict = fcHousing?.District ?? string.Empty,
                    Fcward = fcHousing?.Ward,
                    Fcplot = fcHousing?.Plot,
                    Pfcsize = personalHousing?.Size ?? string.Empty,
                    Pfcdistrict = personalHousing?.District ?? string.Empty,
                    Pfcward = personalHousing?.Ward,
                    Pfcplot = personalHousing?.Plot,
                    SharedHousing = sharedHousing,
                };
            })
            .ToList();
    }

    // ───────────────────────────────────────────────
    //  Export Data — AutoRetainer loader
    // ───────────────────────────────────────────────
    private List<ExportAutoRetainerCharacter> ExportLoadAutoRetainerCharacters()
    {
        var path = GetExportAutoRetainerConfigPath();
        if (!File.Exists(path))
            return new List<ExportAutoRetainerCharacter>();

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        var fcLookup = new Dictionary<long, ExportFcLookup>();
        ExportExtractFcLookup(root, fcLookup);

        var characters = new List<ExportAutoRetainerCharacter>();
        if (!root.TryGetProperty("OfflineData", out var offlineData) || offlineData.ValueKind != JsonValueKind.Array)
            return characters;

        foreach (var entry in offlineData.EnumerateArray())
        {
            var contentId = ExportGetLong(entry, "CID");
            var name = ExportGetString(entry, "Name");
            var world = ExportGetString(entry, "World");
            if (contentId == 0 || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world))
                continue;

            fcLookup.TryGetValue(contentId, out var fcData);

            characters.Add(new ExportAutoRetainerCharacter
            {
                ContentId = contentId,
                Name = name,
                World = world,
                Gil = ExportGetInt(entry, "Gil"),
                FcId = ExportGetLong(entry, "FCID"),
                FcName = fcData?.Name ?? string.Empty,
                FcPoints = fcData?.Points ?? 0,
                HighestJobLevel = ExportGetHighestJobLevel(entry),
                GcRank = ExportGetInt(entry, "GCRank"),
                VentureCoffers = ExportGetInt(entry, "VentureCoffers"),
                Ceruleum = ExportGetInt(entry, "Ceruleum"),
                RepairKits = ExportGetInt(entry, "RepairKits"),
                LeveAllowances = ExportGetOptionalInt(entry, "LeveAllowances", "LeveAllowance", "LeveAllowancesRemaining", "Allowance", "Allowances"),
                RetainerGil = ExportGetRetainerGil(entry),
            });
        }

        return characters;
    }

    // ───────────────────────────────────────────────
    //  Export Data — Lifestream loader
    // ───────────────────────────────────────────────
    private Dictionary<long, ExportCharacterHousing> ExportLoadLifestreamHousing()
    {
        var path = GetExportLifestreamConfigPath();
        var result = new Dictionary<long, ExportCharacterHousing>();
        if (!File.Exists(path))
            return result;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!root.TryGetProperty("HousePathDatas", out var housePathDatas) || housePathDatas.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var entry in housePathDatas.EnumerateArray())
        {
            var contentId = ExportGetLong(entry, "CID");
            if (contentId == 0)
                continue;

            if (!result.TryGetValue(contentId, out var housing))
            {
                housing = new ExportCharacterHousing();
                result[contentId] = housing;
            }

            var ward = ExportGetOptionalInt(entry, "Ward");
            var plot = ExportGetOptionalInt(entry, "Plot");
            var districtId = ExportGetInt(entry, "ResidentialDistrict");
            var district = ExportResidentialDistrictNames.TryGetValue(districtId, out var districtName) ? districtName : string.Empty;
            var isPrivate = ExportGetBool(entry, "IsPrivate");

            var info = new ExportHousingInfo
            {
                Ward = ward.HasValue ? ward.Value + 1 : null,
                Plot = plot.HasValue ? plot.Value + 1 : null,
                District = district,
                Size = ExportGetPlotSize(plot.HasValue ? plot.Value + 1 : null),
            };

            if (isPrivate)
                housing.Private = info;
            else
                housing.Fc = info;
        }

        return result;
    }

    // ───────────────────────────────────────────────
    //  Export Data — XA Database snapshot loader
    // ───────────────────────────────────────────────
    private Dictionary<long, ExportSnapshotSupplement> ExportLoadXaSnapshotSupplements()
    {
        var result = new Dictionary<long, ExportSnapshotSupplement>();
        if (!plugin.IpcClient.IsXaDatabaseAvailable())
            return result;

        var dbPath = plugin.IpcClient.GetDbPath();
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return result;

        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT content_id,
                   highest_job_level,
                   retainer_gil,
                   fc_tag,
                   fc_name,
                   fc_points,
                   fc_estate,
                   personal_estate,
                   shared_estates,
                   currencies_json,
                   items_json,
                   listings_json,
                   retainer_items_json,
                   voyages_json,
                   free_company_json
            FROM xa_characters";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var contentId = reader.IsDBNull(0) ? 0L : reader.GetInt64(0);
            if (contentId == 0)
                continue;

            var highestJobLevel = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            var retainerGil = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
            var fcTag = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            var fcName = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            var fcPoints = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
            var fcEstate = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
            var personalEstate = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
            var sharedEstates = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
            var currenciesJson = reader.IsDBNull(9) ? "[]" : reader.GetString(9);
            var itemsJson = reader.IsDBNull(10) ? "[]" : reader.GetString(10);
            var listingsJson = reader.IsDBNull(11) ? "[]" : reader.GetString(11);
            var retainerItemsJson = reader.IsDBNull(12) ? "[]" : reader.GetString(12);
            var voyagesJson = reader.IsDBNull(13) ? "null" : reader.GetString(13);
            var freeCompanyJson = reader.IsDBNull(14) ? "null" : reader.GetString(14);

            var freeCompany = ExportDeserializeOrDefault<ExportFreeCompanySnapshot>(freeCompanyJson);

            result[contentId] = new ExportSnapshotSupplement
            {
                HighestJobLevel = highestJobLevel,
                RetainerGil = retainerGil,
                Mgp = ExportCalculateMgp(currenciesJson, itemsJson),
                LeveAllowances = ExportGetCurrencyAmount(currenciesJson, "Leve Allowances", "Leve Allowance"),
                TreasureValue = ExportCalculateTreasureValue(itemsJson, listingsJson, retainerItemsJson),
                VentureCoffers = ExportCalculateVentureCoffers(itemsJson, retainerItemsJson),
                FcRank = freeCompany?.Rank ?? 0,
                FcTag = !string.IsNullOrWhiteSpace(freeCompany?.Tag) ? freeCompany!.Tag : fcTag,
                FcName = !string.IsNullOrWhiteSpace(freeCompany?.Name) ? freeCompany!.Name : fcName,
                FcPoints = freeCompany?.FcPoints > 0 ? freeCompany.FcPoints : fcPoints,
                FcEstate = !string.IsNullOrWhiteSpace(freeCompany?.Estate) ? freeCompany!.Estate : fcEstate,
                PersonalEstate = personalEstate,
                SharedEstates = ExportParseSharedHousingEntries(sharedEstates),
                Submarines = ExportFormatSubmarineExports(voyagesJson),
            };
        }

        return result;
    }

    // ───────────────────────────────────────────────
    //  Export Data — output formatting
    // ───────────────────────────────────────────────
    private string ExportBuildDelimitedOutput(IEnumerable<ExportRow> rows, string delimiter)
    {
        var rowList = rows.ToList();
        var maxSharedHousingCount = Math.Max(1, rowList.Count == 0 ? 0 : rowList.Max(row => row.SharedHousing.Count));
        var headerColumns = ExportBuildHeaderColumns(maxSharedHousingCount);
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(delimiter, headerColumns.Select(column => ExportEscapeValue(column, delimiter))));

        foreach (var row in rowList)
        {
            builder.AppendLine(string.Join(delimiter, row.ToValues(maxSharedHousingCount).Select(value => ExportEscapeValue(value, delimiter))));
        }

        return builder.ToString();
    }

    private static string ExportEscapeValue(string value, string delimiter)
    {
        value ??= string.Empty;
        if (delimiter == "," && (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    // ───────────────────────────────────────────────
    //  Export Data — path helpers
    // ───────────────────────────────────────────────
    private string GetExportAutoRetainerConfigPath()
    {
        return plugin.ArConfigReader.GetAutoRetainerConfigPath();
    }

    private string GetExportLifestreamConfigPath()
    {
        return Path.Combine(exportPluginConfigsBasePath, "Lifestream", "DefaultConfig.json");
    }

    private static string? ExportResolveOutputFolder(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        var trimmed = configuredPath.Trim();

        if (trimmed.EndsWith(Path.DirectorySeparatorChar) || trimmed.EndsWith(Path.AltDirectorySeparatorChar) || !Path.HasExtension(trimmed))
            return trimmed;

        return Path.GetDirectoryName(trimmed);
    }

    private string ExportBuildOutputFilePath(string configuredPath, DateTime nowUtc)
    {
        var trimmed = configuredPath.Trim();
        var timestamp = nowUtc.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

        if (trimmed.EndsWith(Path.DirectorySeparatorChar) || trimmed.EndsWith(Path.AltDirectorySeparatorChar) || !Path.HasExtension(trimmed))
        {
            Directory.CreateDirectory(trimmed);
            return Path.Combine(trimmed, $"ExportData_{timestamp}.tsv");
        }

        if (ExportTimestampTokenRegex.IsMatch(trimmed))
        {
            var tokenResolvedPath = ExportTimestampTokenRegex.Replace(trimmed, timestamp);
            var tokenResolvedDirectory = Path.GetDirectoryName(tokenResolvedPath);
            if (string.IsNullOrWhiteSpace(tokenResolvedDirectory))
                tokenResolvedDirectory = Plugin.PluginInterface.GetPluginConfigDirectory();

            Directory.CreateDirectory(tokenResolvedDirectory);
            return Path.IsPathRooted(tokenResolvedPath)
                ? tokenResolvedPath
                : Path.Combine(tokenResolvedDirectory, Path.GetFileName(tokenResolvedPath));
        }

        var directory = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrWhiteSpace(directory))
            directory = Plugin.PluginInterface.GetPluginConfigDirectory();

        Directory.CreateDirectory(directory);

        var fileName = Path.GetFileNameWithoutExtension(trimmed);
        var extension = Path.GetExtension(trimmed);
        return Path.Combine(directory, $"{fileName}_{timestamp}{extension}");
    }

    private static List<string> ExportBuildHeaderColumns(int sharedHousingCount)
    {
        var columns = new List<string>(ExportBaseHeaderColumns);
        for (var index = 0; index < sharedHousingCount; index++)
        {
            var prefix = index == 0 ? "shd" : $"shd{index + 1}";
            columns.Add(prefix);
            columns.Add($"{prefix}size");
            columns.Add($"{prefix}district");
            columns.Add($"{prefix}ward");
            columns.Add($"{prefix}plot");
        }

        return columns;
    }

    // ───────────────────────────────────────────────
    //  Export Data — scheduling helpers
    // ───────────────────────────────────────────────
    private void ExportResetSchedulingState()
    {
        exportLastAlwaysOnRunContentId = 0;
        exportNextAutomaticAttemptUtc = DateTime.UtcNow.AddSeconds(5);
    }

    private void ExportSetStatus(string message, Vector4 color)
    {
        exportStatusMessage = message;
        exportStatusColor = color;
        exportStatusExpiryUtc = DateTime.UtcNow.AddSeconds(10);
    }

    private static int ExportNormalizeInterval(int hours)
    {
        return ExportIntervalOptions.Contains(hours) ? hours : 0;
    }

    private static int ExportGetIntervalIndex(int hours)
    {
        var normalized = ExportNormalizeInterval(hours);
        for (var i = 0; i < ExportIntervalOptions.Length; i++)
        {
            if (ExportIntervalOptions[i] == normalized)
                return i;
        }

        return 0;
    }

    private static string ExportFormatIntervalLabel(int hours)
    {
        return ExportNormalizeInterval(hours) == 0 ? "Always" : $"{ExportNormalizeInterval(hours)}hr";
    }

    private static DateTime? ExportParseUtc(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    // ───────────────────────────────────────────────
    //  Export Data — calculation helpers
    // ───────────────────────────────────────────────
    private int ExportCalculateMgp(string currenciesJson, string itemsJson)
    {
        var itemEntries = ExportDeserializeOrDefault<List<ExportItemSnapshot>>(itemsJson) ?? new List<ExportItemSnapshot>();

        var mgp = ExportGetCurrencyAmount(currenciesJson, "MGP") ?? 0;
        var platinumCards = itemEntries.Where(entry => entry.ItemId == 16784).Sum(entry => entry.Quantity);

        var total = (long)mgp + (long)platinumCards * 50000L;
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    private int? ExportGetCurrencyAmount(string currenciesJson, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(currenciesJson) || names == null || names.Length == 0)
            return null;

        var currencyEntries = ExportDeserializeOrDefault<List<ExportCurrencySnapshot>>(currenciesJson) ?? new List<ExportCurrencySnapshot>();
        foreach (var entry in currencyEntries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
                continue;

            foreach (var name in names)
            {
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                    return entry.Amount;
            }
        }

        return null;
    }

    private int ExportCalculateVentureCoffers(string itemsJson, string retainerItemsJson)
    {
        var itemEntries = ExportDeserializeOrDefault<List<ExportItemSnapshot>>(itemsJson) ?? new List<ExportItemSnapshot>();
        var retainerItemEntries = ExportDeserializeOrDefault<List<ExportItemSnapshot>>(retainerItemsJson) ?? new List<ExportItemSnapshot>();

        var total = (long)itemEntries.Where(entry => entry.ItemId == ExportVentureCofferItemId).Sum(entry => entry.Quantity)
            + (long)retainerItemEntries.Where(entry => entry.ItemId == ExportVentureCofferItemId).Sum(entry => entry.Quantity);

        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    private int ExportCalculateTreasureValue(string itemsJson, string listingsJson, string retainerItemsJson)
    {
        var total = ExportSumTreasureValues(ExportDeserializeOrDefault<List<ExportItemSnapshot>>(itemsJson))
            + ExportSumTreasureValues(ExportDeserializeOrDefault<List<ExportItemSnapshot>>(listingsJson))
            + ExportSumTreasureValues(ExportDeserializeOrDefault<List<ExportItemSnapshot>>(retainerItemsJson));

        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    private static long ExportSumTreasureValues(List<ExportItemSnapshot>? items)
    {
        if (items == null || items.Count == 0)
            return 0;

        long total = 0;
        foreach (var entry in items)
        {
            if (entry.Quantity <= 0)
                continue;

            if (ExportTreasureValues.TryGetValue(entry.ItemId, out var itemValue))
                total += (long)entry.Quantity * itemValue;
        }

        return total;
    }

    // ───────────────────────────────────────────────
    //  Export Data — housing helpers
    // ───────────────────────────────────────────────
    private List<ExportSharedHousingEntry> ExportParseSharedHousingEntries(string sharedEstates)
    {
        if (string.IsNullOrWhiteSpace(sharedEstates))
            return new List<ExportSharedHousingEntry>();

        return sharedEstates
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim())
            .Where(entry => entry.Length > 0)
            .GroupBy(entry => ExportStripHousingOwnerSuffix(entry), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var display = group.First();
                var housing = ExportParseEstateString(display);
                return new ExportSharedHousingEntry
                {
                    Display = display,
                    Size = housing?.Size ?? string.Empty,
                    District = housing?.District ?? string.Empty,
                    Ward = housing?.Ward,
                    Plot = housing?.Plot,
                };
            })
            .ToList();
    }

    private static ExportHousingInfo? ExportResolveHousing(string primaryText, ExportHousingInfo? fallback)
    {
        var primary = ExportParseEstateString(primaryText);
        if (primary == null)
            return fallback;

        if (fallback == null)
            return primary;

        if (ExportHasHousingConflict(primary, fallback))
            return primary;

        var plot = primary.Plot ?? fallback.Plot;
        return new ExportHousingInfo
        {
            Ward = primary.Ward ?? fallback.Ward,
            Plot = plot,
            District = !string.IsNullOrWhiteSpace(primary.District) ? primary.District : fallback.District,
            Size = ExportResolveHousingSize(primary.Size, fallback.Size, plot),
        };
    }

    private static ExportHousingInfo? ExportParseEstateString(string estate)
    {
        var normalizedEstate = ExportStripHousingOwnerSuffix(estate);
        if (string.IsNullOrWhiteSpace(normalizedEstate))
            return null;

        var plot = ExportExtractNumber(ExportPlotNumberRegex, normalizedEstate, "plot");
        var ward = ExportExtractWardNumber(normalizedEstate);
        var district = ExportExtractDistrictDisplayName(normalizedEstate);
        var size = ExportExtractHousingSize(normalizedEstate);
        if (plot <= 0 && ward <= 0 && string.IsNullOrWhiteSpace(district))
            return null;

        int? plotValue = plot > 0 ? plot : null;
        int? wardValue = ward > 0 ? ward : null;

        return new ExportHousingInfo
        {
            Ward = wardValue,
            Plot = plotValue,
            District = district,
            Size = ExportResolveHousingSize(size, string.Empty, plotValue),
        };
    }

    private static bool ExportHasHousingConflict(ExportHousingInfo primary, ExportHousingInfo fallback)
    {
        if (primary.Ward.HasValue && fallback.Ward.HasValue && primary.Ward.Value != fallback.Ward.Value)
            return true;

        if (primary.Plot.HasValue && fallback.Plot.HasValue && primary.Plot.Value != fallback.Plot.Value)
            return true;

        return !string.IsNullOrWhiteSpace(primary.District)
            && !string.IsNullOrWhiteSpace(fallback.District)
            && !string.Equals(
                ExportNormalizeHousingTextForComparison(primary.District),
                ExportNormalizeHousingTextForComparison(fallback.District),
                StringComparison.Ordinal);
    }

    private static string ExportResolveHousingSize(string primarySize, string fallbackSize, int? plot)
    {
        if (!string.IsNullOrWhiteSpace(primarySize))
            return primarySize;

        if (!string.IsNullOrWhiteSpace(fallbackSize))
            return fallbackSize;

        return ExportGetPlotSize(plot);
    }

    private static string ExportExtractHousingSize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var openParen = value.LastIndexOf('(');
        var closeParen = value.LastIndexOf(')');
        if (openParen < 0 || closeParen <= openParen)
            return string.Empty;

        return value[(openParen + 1)..closeParen].Trim();
    }

    private static int ExportExtractWardNumber(string value)
    {
        var match = ExportWardNumberRegex.Match(value);
        if (!match.Success)
            return 0;

        if (int.TryParse(match.Groups["wardAfter"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wardAfter))
            return wardAfter;

        if (int.TryParse(match.Groups["wardBefore"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wardBefore))
            return wardBefore;

        return 0;
    }

    private static int ExportExtractNumber(Regex regex, string value, string groupName)
    {
        var match = regex.Match(value);
        if (!match.Success)
            return 0;

        return int.TryParse(match.Groups[groupName].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue)
            ? parsedValue
            : 0;
    }

    private static string ExportExtractDistrictDisplayName(string value)
    {
        var segments = ExportStripHousingOwnerSuffix(value)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .ToArray();
        if (segments.Length == 0)
            return string.Empty;

        return ExportParentheticalTextRegex.Replace(segments[^1], string.Empty).Trim().Trim(',', ' ');
    }

    private static string ExportStripHousingOwnerSuffix(string value)
    {
        var normalizedValue = ExportNormalizeHousingDisplayValue(value);
        return normalizedValue.Length == 0 ? string.Empty : ExportOwnerSuffixRegex.Replace(normalizedValue, string.Empty).Trim();
    }

    private static string ExportNormalizeHousingDisplayValue(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string ExportNormalizeHousingTextForComparison(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var withoutParenthetical = ExportParentheticalTextRegex.Replace(ExportStripHousingOwnerSuffix(value), string.Empty);
        var collapsedWhitespace = ExportWhitespaceRegex.Replace(withoutParenthetical, " ").Trim();
        return collapsedWhitespace.Trim(',', ' ').ToLowerInvariant();
    }

    private static string ExportGetPlotSize(int? plotNumber)
    {
        if (!plotNumber.HasValue || plotNumber.Value <= 0)
            return string.Empty;

        var localPlot = (plotNumber.Value - 1) % 30;
        return localPlot switch
        {
            >= 0 and <= 7 => "Small",
            >= 8 and <= 11 => "Medium",
            >= 12 and <= 14 => "Large",
            >= 15 and <= 22 => "Small",
            >= 23 and <= 26 => "Medium",
            >= 27 and <= 29 => "Large",
            _ => string.Empty,
        };
    }

    // ───────────────────────────────────────────────
    //  Export Data — submarine helpers
    // ───────────────────────────────────────────────
    private string[] ExportFormatSubmarineExports(string voyagesJson)
    {
        var voyages = ExportDeserializeOrDefault<ExportVoyageSnapshot>(voyagesJson);
        if (voyages?.Submarines == null || voyages.Submarines.Count == 0)
            return Array.Empty<string>();

        return voyages.Submarines
            .OrderBy(entry => entry.Slot)
            .Take(4)
            .Select(ExportFormatSubmarineExport)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static string ExportFormatSubmarineExport(ExportVoyageEntrySnapshot entry)
    {
        var build = !string.IsNullOrWhiteSpace(entry.BuildString)
            ? entry.BuildString
            : ExportGetVoyageBuildString(entry.HullId, entry.SternId, entry.BowId, entry.BridgeId);

        if (entry.RankId <= 0 && string.IsNullOrWhiteSpace(build))
            return string.Empty;

        if (string.IsNullOrWhiteSpace(build))
            return entry.RankId.ToString(CultureInfo.InvariantCulture);

        return $"{entry.RankId.ToString(CultureInfo.InvariantCulture)}/{build}";
    }

    private static string ExportGetVoyageBuildString(ushort hullId, ushort sternId, ushort bowId, ushort bridgeId)
    {
        if (hullId == 0 && sternId == 0 && bowId == 0 && bridgeId == 0)
            return string.Empty;

        var hull = ExportShortenVoyagePart(hullId);
        var stern = ExportShortenVoyagePart(sternId);
        var bow = ExportShortenVoyagePart(bowId);
        var bridge = ExportShortenVoyagePart(bridgeId);
        return $"{hull}{stern}{bow}{bridge}";
    }

    private static string ExportShortenVoyagePart(int rowId)
    {
        if (rowId < 1)
            return "?";

        var classIndex = (rowId - 1) / 4;
        return classIndex >= 0 && classIndex < ExportVoyagePartShortcodes.Length
            ? ExportVoyagePartShortcodes[classIndex]
            : "?";
    }

    // ───────────────────────────────────────────────
    //  Export Data — JSON helpers
    // ───────────────────────────────────────────────
    private T? ExportDeserializeOrDefault<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.OrdinalIgnoreCase))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json, exportJsonOptions);
        }
        catch
        {
            return default;
        }
    }

    private static void ExportExtractFcLookup(JsonElement element, Dictionary<long, ExportFcLookup> lookup)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (ExportTryGetLong(element, "HolderChara", out var holderChara) && holderChara != 0)
                {
                    var fcName = ExportGetString(element, "Name");
                    var fcPoints = ExportGetInt(element, "FCPoints");
                    var fcId = ExportGetLong(element, "ID");
                    lookup[holderChara] = new ExportFcLookup(fcId, fcName, fcPoints);
                }

                foreach (var property in element.EnumerateObject())
                    ExportExtractFcLookup(property.Value, lookup);
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ExportExtractFcLookup(item, lookup);
                break;
        }
    }

    private static int ExportGetHighestJobLevel(JsonElement entry)
    {
        if (!entry.TryGetProperty("ClassJobLevelArray", out var levels) || levels.ValueKind != JsonValueKind.Array)
            return 0;

        var highest = 0;
        foreach (var level in levels.EnumerateArray())
        {
            if (level.ValueKind == JsonValueKind.Number && level.TryGetInt32(out var parsed) && parsed > highest)
                highest = parsed;
        }

        return highest;
    }

    private static string ExportGetString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.String)
                return property.GetString() ?? string.Empty;

            if (property.ValueKind == JsonValueKind.Number)
                return property.ToString();
        }

        return string.Empty;
    }

    private static int ExportGetInt(JsonElement element, params string[] propertyNames)
    {
        return ExportGetOptionalInt(element, propertyNames) ?? 0;
    }

    private static int? ExportGetOptionalInt(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var directInt))
                return directInt;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var directLong))
                return (int)directLong;

            if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return null;
    }

    private static long ExportGetLong(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var property))
                continue;

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var directLong))
                return directLong;

            if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return 0;
    }

    private static bool ExportTryGetLong(JsonElement element, string propertyName, out long value)
    {
        value = ExportGetLong(element, propertyName);
        return value != 0;
    }

    private static bool ExportGetBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return false;

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when property.TryGetInt32(out var numeric) => numeric != 0,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            _ => false,
        };
    }

    private static long ExportGetRetainerGil(JsonElement entry)
    {
        var retainerGil = 0L;

        if (entry.TryGetProperty("RetainerData", out var retainerData) && retainerData.ValueKind == JsonValueKind.Array)
        {
            foreach (var retainer in retainerData.EnumerateArray())
            {
                var gil = ExportGetLong(retainer, "Gil");
                retainerGil += gil;
            }
        }

        return retainerGil;
    }

    // ───────────────────────────────────────────────
    //  Export Data — model classes
    // ───────────────────────────────────────────────
    private sealed record ExportAutoRetainerCharacter
    {
        public long ContentId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string World { get; init; } = string.Empty;
        public int Gil { get; init; }
        public long FcId { get; init; }
        public string FcName { get; init; } = string.Empty;
        public int FcPoints { get; init; }
        public int HighestJobLevel { get; init; }
        public int GcRank { get; init; }
        public int VentureCoffers { get; init; }
        public int Ceruleum { get; init; }
        public int RepairKits { get; init; }
        public int? LeveAllowances { get; init; }
        public long RetainerGil { get; init; }
    }

    private sealed record ExportFcLookup(long FcId, string Name, int Points);

    private sealed class ExportCharacterHousing
    {
        public ExportHousingInfo? Private { get; set; }
        public ExportHousingInfo? Fc { get; set; }
    }

    private sealed class ExportHousingInfo
    {
        public int? Ward { get; init; }
        public int? Plot { get; init; }
        public string District { get; init; } = string.Empty;
        public string Size { get; init; } = string.Empty;
    }

    private sealed class ExportSharedHousingEntry
    {
        public string Display { get; init; } = string.Empty;
        public string Size { get; init; } = string.Empty;
        public string District { get; init; } = string.Empty;
        public int? Ward { get; init; }
        public int? Plot { get; init; }
    }

    private sealed class ExportSnapshotSupplement
    {
        public int HighestJobLevel { get; init; }
        public long RetainerGil { get; init; }
        public int Mgp { get; init; }
        public int? LeveAllowances { get; init; }
        public int TreasureValue { get; init; }
        public int VentureCoffers { get; init; }
        public int FcRank { get; init; }
        public string FcTag { get; init; } = string.Empty;
        public string FcName { get; init; } = string.Empty;
        public int FcPoints { get; init; }
        public string FcEstate { get; init; } = string.Empty;
        public string PersonalEstate { get; init; } = string.Empty;
        public List<ExportSharedHousingEntry> SharedEstates { get; init; } = new();
        public string[] Submarines { get; init; } = Array.Empty<string>();
    }

    private sealed class ExportCurrencySnapshot
    {
        public string Name { get; set; } = string.Empty;
        public int Amount { get; set; }
    }

    private sealed class ExportItemSnapshot
    {
        public uint ItemId { get; set; }
        public int Quantity { get; set; }
    }

    private sealed class ExportVoyageSnapshot
    {
        public List<ExportVoyageEntrySnapshot> Airships { get; set; } = new();
        public List<ExportVoyageEntrySnapshot> Submarines { get; set; } = new();
    }

    private sealed class ExportVoyageEntrySnapshot
    {
        public byte Slot { get; set; }
        public byte RankId { get; set; }
        public ushort HullId { get; set; }
        public ushort SternId { get; set; }
        public ushort BowId { get; set; }
        public ushort BridgeId { get; set; }
        public string BuildString { get; set; } = string.Empty;
    }

    private sealed class ExportFreeCompanySnapshot
    {
        public string Name { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public int FcPoints { get; set; }
        public byte Rank { get; set; }
        public string Estate { get; set; } = string.Empty;
    }

    private sealed class ExportRow
    {
        public string Name { get; init; } = string.Empty;
        public string World { get; init; } = string.Empty;
        public string Region { get; init; } = string.Empty;
        public int Gil { get; init; }
        public long Rgil { get; init; }
        public int Trs { get; init; }
        public int Fcp { get; init; }
        public int Mgp { get; init; }
        public int Vc { get; init; }
        public int Cf { get; init; }
        public int Mrk { get; init; }
        public int Hjl { get; init; }
        public int Gcr { get; init; }
        public int Fcr { get; init; }
        public string Fcs { get; init; } = string.Empty;
        public string Fcl { get; init; } = string.Empty;
        public int? LeveA { get; init; }
        public string S1 { get; init; } = string.Empty;
        public string S2 { get; init; } = string.Empty;
        public string S3 { get; init; } = string.Empty;
        public string S4 { get; init; } = string.Empty;
        public string Fcsize { get; init; } = string.Empty;
        public string Fcdistrict { get; init; } = string.Empty;
        public int? Fcward { get; init; }
        public int? Fcplot { get; init; }
        public string Pfcsize { get; init; } = string.Empty;
        public string Pfcdistrict { get; init; } = string.Empty;
        public int? Pfcward { get; init; }
        public int? Pfcplot { get; init; }
        public List<ExportSharedHousingEntry> SharedHousing { get; init; } = new();

        public IEnumerable<string> ToValues(int sharedHousingCount)
        {
            yield return Name;
            yield return World;
            yield return Region;
            yield return Gil.ToString(CultureInfo.InvariantCulture);
            yield return Rgil.ToString(CultureInfo.InvariantCulture);
            yield return Trs.ToString(CultureInfo.InvariantCulture);
            yield return Fcp.ToString(CultureInfo.InvariantCulture);
            yield return Mgp.ToString(CultureInfo.InvariantCulture);
            yield return Vc.ToString(CultureInfo.InvariantCulture);
            yield return Cf.ToString(CultureInfo.InvariantCulture);
            yield return Mrk.ToString(CultureInfo.InvariantCulture);
            yield return Hjl.ToString(CultureInfo.InvariantCulture);
            yield return LeveA?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            yield return Gcr.ToString(CultureInfo.InvariantCulture);
            yield return Fcr.ToString(CultureInfo.InvariantCulture);
            yield return Fcs;
            yield return Fcl;
            yield return S1;
            yield return S2;
            yield return S3;
            yield return S4;
            yield return Fcsize;
            yield return Fcdistrict;
            yield return Fcward?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            yield return Fcplot?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            yield return Pfcsize;
            yield return Pfcdistrict;
            yield return Pfcward?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            yield return Pfcplot?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

            for (var index = 0; index < sharedHousingCount; index++)
            {
                var entry = index < SharedHousing.Count ? SharedHousing[index] : null;
                yield return entry?.Display ?? string.Empty;
                yield return entry?.Size ?? string.Empty;
                yield return entry?.District ?? string.Empty;
                yield return entry?.Ward?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                yield return entry?.Plot?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }
    }
}
