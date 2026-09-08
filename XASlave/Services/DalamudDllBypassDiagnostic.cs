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
    string Detail);

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
                inspectionError = $"Launcher DLL inspection failed: {ex.GetType().Name}: {ex.Message}";
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
                "known-default",
                "Original official/default launcher DLL",
                "none",
                "NOT PRESENT - exact SHA-256 matches a reviewed default artifact."),
            CurrentDefaultSha256 => new(
                ReviewedLauncherArtifactState.KnownDefault,
                "known-default",
                "Current official/default launcher DLL",
                "none",
                "NOT PRESENT - exact SHA-256 matches a reviewed default artifact."),
            LegacyPatchedSha256 => new(
                ReviewedLauncherArtifactState.KnownPatched,
                "known-patched",
                "Reviewed legacy five-method bypass",
                ReviewedPatchFamily,
                "PASS - exact SHA-256 matches a reviewed five-method-v1 patched artifact."),
            SupersededPatchedSha256 => new(
                ReviewedLauncherArtifactState.KnownPatched,
                "known-patched",
                "Superseded current-derived bypass with builder self-reference",
                ReviewedPatchFamily,
                "PASS - exact SHA-256 matches a reviewed five-method-v1 patched artifact."),
            CurrentPatchedSha256 => new(
                ReviewedLauncherArtifactState.KnownPatched,
                "known-patched",
                "Current-derived five-method bypass with preserved assembly references",
                ReviewedPatchFamily,
                "PASS - exact SHA-256 matches a reviewed five-method-v1 patched artifact."),
            _ => new(
                ReviewedLauncherArtifactState.Unknown,
                "unknown",
                "Unknown or newer launcher DLL",
                "unknown",
                "UNRESOLVED - SHA-256 is not in the reviewed catalog; static comparison is required."),
        };
    }

    internal static string BuildOperatorVerdict(
        bool launcherDllFound,
        ReviewedLauncherArtifact classification,
        bool? presentBeforeGameStart,
        bool? versionMismatch)
    {
        if (!launcherDllFound)
            return "NO - XIVLauncher.Common.dll was not found.";
        if (classification.State == ReviewedLauncherArtifactState.Unknown)
            return "UNRESOLVED - the installed DLL hash requires static comparison.";
        if (classification.State == ReviewedLauncherArtifactState.KnownDefault)
        {
            return versionMismatch switch
            {
                true => "NO - versions differ and a reviewed bypass is not installed.",
                false => "NOT NEEDED TODAY - versions match and the reviewed default DLL is installed.",
                null => "UNKNOWN NEED - the default DLL is installed but the version comparison is unavailable.",
            };
        }
        if (presentBeforeGameStart == false)
            return "RESTART REQUIRED - the reviewed bypass file is newer than the running game process.";
        if (presentBeforeGameStart is null)
            return "PASS (static only) - the reviewed bypass is installed; pre-launch timing is unavailable.";

        return versionMismatch switch
        {
            true => "PASS (runtime mismatch observed) - XA Slave is loaded while game and supported versions differ.",
            false => "PASS (static) - the reviewed bypass is installed; the version gate is not needed today.",
            null => "PASS (static) - the reviewed bypass is installed; version comparison is unavailable.",
        };
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
            return "The exact file hash matches an artifact whose five reviewed patch seams passed static comparison. This is not launcher or game acceptance proof.";
        if (classification.State == ReviewedLauncherArtifactState.KnownDefault)
            return "The exact file hash matches a reviewed official/default artifact and does not contain the reviewed bypass.";
        return "Unknown hashes remain unresolved until the DLL research workflow performs a fresh static comparison.";
    }

    private static unsafe string ReadRunningGameVersion()
    {
        try
        {
            var framework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
            return framework == null || string.IsNullOrWhiteSpace(framework->GameVersionString)
                ? "Unavailable"
                : framework->GameVersionString;
        }
        catch
        {
            return "Unavailable";
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
                "Unavailable",
                metadataPath,
                "Loaded Dalamud version metadata was not found beside the loaded Dalamud assembly, so version-mismatch need cannot be compared.");
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
                "Unavailable",
                metadataPath,
                "Loaded Dalamud version metadata does not contain a SupportedGameVer value.");
        }
        catch (Exception ex)
        {
            return (
                "Unavailable",
                metadataPath,
                $"Loaded Dalamud version metadata could not be read: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
