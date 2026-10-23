using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Dalamud.Plugin;

namespace XASlave.Services;

internal enum ReviewedLauncherArtifactState
{
    Unknown,
    KnownDefault,
    KnownPatched,
}

internal readonly record struct ReviewedLauncherArtifact(
    ReviewedLauncherArtifactState State,
    string StateLabel,
    string DisplayLabel,
    string PatchFamily,
    string StaticPatchResult)
{
    internal global::XASlave.Localization.UiMessage StateLabelMessage => AutoRetainerUiMessagePresentation.Get(StateLabel);
    internal global::XASlave.Localization.UiMessage DisplayLabelMessage => AutoRetainerUiMessagePresentation.Get(DisplayLabel);
    internal global::XASlave.Localization.UiMessage PatchFamilyMessage => AutoRetainerUiMessagePresentation.Get(PatchFamily);
    internal global::XASlave.Localization.UiMessage StaticPatchResultMessage => AutoRetainerUiMessagePresentation.Get(StaticPatchResult);

    internal bool BypassInstalled => State == ReviewedLauncherArtifactState.KnownPatched;
    internal bool StaticPatchValid => BypassInstalled
        && string.Equals(PatchFamily, DalamudDllBypassDiagnostic.ReviewedPatchFamily, StringComparison.Ordinal);
}

internal readonly record struct DalamudDllBypassDiagnosticSnapshot(
    DateTime CapturedUtc,
    string GameVersion,
    string SupportedGameVersion,
    string DalamudVersionMetadataPath,
    bool? VersionMismatch,
    string LauncherDllPath,
    bool LauncherDllFound,
    string LauncherDllSha256,
    ReviewedLauncherArtifact Classification,
    bool? PresentBeforeGameStart,
    string OperatorVerdict,
    string Detail)
{
    internal global::XASlave.Localization.UiMessage GameVersionMessage => AutoRetainerUiMessagePresentation.Get(GameVersion);
    internal global::XASlave.Localization.UiMessage SupportedGameVersionMessage => AutoRetainerUiMessagePresentation.Get(SupportedGameVersion);
    internal global::XASlave.Localization.UiMessage OperatorVerdictMessage => AutoRetainerUiMessagePresentation.Get(OperatorVerdict);
    internal global::XASlave.Localization.UiMessage DetailMessage => AutoRetainerUiMessagePresentation.Get(Detail);
}

/// <summary>
/// Performs inert local inspection only: path lookup, file metadata, SHA-256, and
/// loaded-Dalamud version.json parsing. Researched launcher assemblies are never loaded
/// or executed, and no network operation is used.
/// </summary>
internal static class DalamudDllBypassDiagnostic
{
    internal const string ReviewedPatchFamily = "five-method-v1";
    internal const string OriginalDefaultSha256 = "7BC7632D162222043D37B3717D570715C298E0B5946E946FA9173E320565ACDB";
    internal const string CurrentDefaultSha256 = "F141AA83639E93691C7F13C52E5BA4F0A7BB0A4549B4EDD9A134650BB990F948";
    internal const string LegacyPatchedSha256 = "52E8680328BD5BF5ED269571BF77D5AE032F0DE4D682A3EBB5D4B6AE2AC25352";
    internal const string SupersededPatchedSha256 = "9963771AF644D49F8319DEC630BE7679FD15514610A33D965F00F7B927D7B8D3";
    internal const string CurrentPatchedSha256 = "CF251AA022B91D3753DAEE4124CE52F71B457649BE17C8258F31E420B11C1221";

    internal static readonly string[] ReviewedPatchSeams =
    [
        "DalamudBranchMeta.Branch.get_Hidden",
        "DalamudBranchMeta.Branch.get_IsApplicableForCurrentGameVer",
        "DalamudUpdater.ReCheckVersion(DirectoryInfo)",
        "DalamudUpdater.<GetVersionInfo>d__*::MoveNext",
        "DalamudUpdater.<UpdateDalamud>d__*::MoveNext",
    ];

    internal static DalamudDllBypassDiagnosticSnapshot Capture()
    {
        var capturedUtc = DateTime.UtcNow;
        var gameVersion = ReadRunningGameVersion();
        var (supportedGameVersion, metadataPath, metadataError) = ReadDalamudSupportedGameVersion();
        var launcherDllPath = FindLauncherCommonDll();
        var launcherDllFound = !string.IsNullOrWhiteSpace(launcherDllPath) && File.Exists(launcherDllPath);
        var launcherDllSha256 = string.Empty;
        var classification = ClassifySha256(string.Empty);
        bool? presentBeforeGameStart = null;
        var inspectionError = string.Empty;

        if (launcherDllFound)
        {
            try
            {
                launcherDllSha256 = ComputeSha256(launcherDllPath);
                classification = ClassifySha256(launcherDllSha256);
                if (classification.BypassInstalled)
                    presentBeforeGameStart = WasPresentBeforeGameStart(launcherDllPath);
            }
            catch (Exception ex)
            {
                inspectionError = AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Format("S0A009681A88CBD01", ex.GetType().Name, global::XASlave.Localization.UiMessage.FromException(ex)));
                classification = ClassifySha256(string.Empty);
                presentBeforeGameStart = null;
            }
        }

        bool? versionMismatch = null;
        if (!string.Equals(gameVersion, "Unavailable", StringComparison.Ordinal)
            && !string.Equals(supportedGameVersion, "Unavailable", StringComparison.Ordinal))
        {
            versionMismatch = !string.Equals(gameVersion, supportedGameVersion, StringComparison.Ordinal);
        }

        var operatorVerdict = BuildOperatorVerdict(
            launcherDllFound,
            classification,
            presentBeforeGameStart,
            versionMismatch);
        var detail = BuildDetail(classification, metadataError, inspectionError);

        return new DalamudDllBypassDiagnosticSnapshot(
            capturedUtc,
            gameVersion,
            supportedGameVersion,
            metadataPath,
            versionMismatch,
            launcherDllPath,
            launcherDllFound,
            launcherDllSha256,
            classification,
            presentBeforeGameStart,
            operatorVerdict,
            detail);
    }

    internal static ReviewedLauncherArtifact ClassifySha256(string? sha256)
    {
        return sha256?.Trim().ToUpperInvariant() switch
        {
            OriginalDefaultSha256 => new(
                ReviewedLauncherArtifactState.KnownDefault,
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S10952D1479CE14AE")),
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SB81096BD59B5381D")),
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S140BEDBF9C3F6D56")),
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SDFD33BF55B25FB15"))),
            CurrentDefaultSha256 => new(
                ReviewedLauncherArtifactState.KnownDefault,
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S10952D1479CE14AE")),
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S4F73B1D23D3B6AB5")),
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S140BEDBF9C3F6D56")),
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SDFD33BF55B25FB15"))),
            LegacyPatchedSha256 => new(
                ReviewedLauncherArtifactState.KnownPatched,
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S207E6240C5390362")),
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S3F266A095BE33CA5")),
                ReviewedPatchFamily,
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S2356DB58DEE6E995"))),
            SupersededPatchedSha256 => new(
                ReviewedLauncherArtifactState.KnownPatched,
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S207E6240C5390362")),
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S84A80DE31B811B8A")),
                ReviewedPatchFamily,
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S2356DB58DEE6E995"))),
            CurrentPatchedSha256 => new(
                ReviewedLauncherArtifactState.KnownPatched,
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S207E6240C5390362")),
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SDD64171F7D427666")),
                ReviewedPatchFamily,
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S2356DB58DEE6E995"))),
            _ => new(
                ReviewedLauncherArtifactState.Unknown,
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SB23A6A8439C0DDE5")),
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S91B32D76482E102F")),
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SB23A6A8439C0DDE5")),
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S8E572576DCDAC73F"))),
        };
    }

    internal static string BuildOperatorVerdict(
        bool launcherDllFound,
        ReviewedLauncherArtifact classification,
        bool? presentBeforeGameStart,
        bool? versionMismatch)
    {
        if (!launcherDllFound)
            return AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SE09B548D1B8BBEC5"));
        if (classification.State == ReviewedLauncherArtifactState.Unknown)
            return AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SD708950AFC669960"));
        if (classification.State == ReviewedLauncherArtifactState.KnownDefault)
        {
            return AutoRetainerUiMessagePresentation.Store(versionMismatch switch
            {
                true => global::XASlave.Localization.UiMessage.Key("S12359A92493012C7"),
                false => global::XASlave.Localization.UiMessage.Key("SF4F57B3115FB6DCF"),
                null => global::XASlave.Localization.UiMessage.Key("SC19EBB37901E233B"),
            });
        }
        if (presentBeforeGameStart == false)
            return AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S5BB445C3888DAB41"));
        if (presentBeforeGameStart is null)
            return AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SD11ED7C3A251DED8"));

        return AutoRetainerUiMessagePresentation.Store(versionMismatch switch
        {
            true => global::XASlave.Localization.UiMessage.Key("S195F326868BDB2DF"),
            false => global::XASlave.Localization.UiMessage.Key("SBFE7105D5B9DCCC0"),
            null => global::XASlave.Localization.UiMessage.Key("SB05417EAC2924C93"),
        });
    }

    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string BuildDetail(
        ReviewedLauncherArtifact classification,
        string metadataError,
        string inspectionError)
    {
        if (!string.IsNullOrWhiteSpace(inspectionError))
            return inspectionError;
        if (!string.IsNullOrWhiteSpace(metadataError))
            return metadataError;
        if (classification.StaticPatchValid)
            return AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SE18C663820B7DE38"));
        if (classification.State == ReviewedLauncherArtifactState.KnownDefault)
            return AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S2C260A52995C307F"));
        return AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SCC43BB4A90FDA969"));
    }

    private static unsafe string ReadRunningGameVersion()
    {
        try
        {
            var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            return AutoRetainerUiMessagePresentation.Store((framework == null || string.IsNullOrWhiteSpace(framework->GameVersionString) ? global::XASlave.Localization.UiMessage.Key("SCA184496974204A0") : global::XASlave.Localization.UiMessage.Raw(framework->GameVersionString)));
        }
        catch
        {
            return AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SCA184496974204A0"));
        }
    }

    private static bool? WasPresentBeforeGameStart(string launcherDllPath)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return File.GetLastWriteTimeUtc(launcherDllPath) <= process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static string FindLauncherCommonDll()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            return string.Empty;

        var launcherRoot = Path.Combine(localAppData, "XIVLauncher");
        var currentPath = Path.Combine(launcherRoot, "current", "XIVLauncher.Common.dll");
        if (File.Exists(currentPath))
            return currentPath;
        if (!Directory.Exists(launcherRoot))
            return currentPath;

        try
        {
            return Directory.EnumerateDirectories(launcherRoot, "app-*", SearchOption.TopDirectoryOnly)
                .Select(directory => Path.Combine(directory, "XIVLauncher.Common.dll"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault() ?? currentPath;
        }
        catch
        {
            return currentPath;
        }
    }

    private static (string SupportedGameVersion, string MetadataPath, string Error) ReadDalamudSupportedGameVersion()
    {
        var dalamudAssemblyPath = typeof(IDalamudPluginInterface).Assembly.Location;
        var dalamudDirectory = Path.GetDirectoryName(dalamudAssemblyPath);
        var metadataPath = string.IsNullOrWhiteSpace(dalamudDirectory)
            ? string.Empty
            : Path.Combine(dalamudDirectory, "version.json");
        if (string.IsNullOrWhiteSpace(metadataPath) || !File.Exists(metadataPath))
        {
            return (
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SCA184496974204A0")),
                metadataPath,
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S894733AA9DFCFB8D")));
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            if (document.RootElement.TryGetProperty("SupportedGameVer", out var value))
            {
                var supportedVersion = value.GetString();
                if (!string.IsNullOrWhiteSpace(supportedVersion))
                    return (supportedVersion, metadataPath, string.Empty);
            }

            return (
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SCA184496974204A0")),
                metadataPath,
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S9FA68850CBC6C627")));
        }
        catch (Exception ex)
        {
            return (
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SCA184496974204A0")),
                metadataPath,
                AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Format("S4559A93AD87CFBC2", ex.GetType().Name, global::XASlave.Localization.UiMessage.FromException(ex))));
        }
    }
}
