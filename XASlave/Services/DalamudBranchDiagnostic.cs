using System;
using System.Linq;
using System.Runtime.Loader;
using Dalamud.Plugin;

namespace XASlave.Services;

internal enum DalamudBranchKind
{
    Unknown,
    Release,
    Staging,
    ApiPreview,
    Development,
    Other,
}

internal sealed record DalamudBranchSnapshot(
    DateTime CapturedUtc,
    string? RawBranch,
    string Branch,
    DalamudBranchKind Kind,
    string DalamudVersion,
    string ClientStructsVersion,
    string ClientStructsGitHash,
    string Detail)
{
    internal bool IsRelease => Kind == DalamudBranchKind.Release;

    internal string PolicyMessage => IsRelease
        ? "Dalamud Release confirmed"
        : Kind == DalamudBranchKind.Unknown
            ? "Cannot confirm Dalamud Release"
            : "You are not on Dalamud Release";

    internal string ToReport() =>
        $"Dalamud Branch: {Branch}\n" +
        $"Raw track: {(RawBranch is null ? "<null>" : $"\"{RawBranch}\"")}\n" +
        $"Classification: {Kind}\n" +
        $"Release-only policy preview: {(IsRelease ? "Allowed" : "Not allowed")} - {PolicyMessage}\n" +
        $"Dalamud version: {DalamudVersion}\n" +
        $"Loaded ClientStructs: {ClientStructsVersion}\n" +
        $"ClientStructs Git hash: {ClientStructsGitHash}\n" +
        $"Details: {Detail}\n" +
        $"Captured: {CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n" +
        "Diagnostic only; no plugin is blocked or unloaded.";
}

/// <summary>
/// Reads the running session's launcher-reported track through Dalamud's public API.
/// Does not read launcher preferences, load dependencies, or change plugin state.
/// </summary>
internal static class DalamudBranchDiagnostic
{
    internal static DalamudBranchSnapshot Capture(IDalamudPluginInterface pluginInterface)
    {
        var capturedUtc = DateTime.UtcNow;
        string? rawBranch = null;
        var version = "Unavailable";
        var clientStructsGitHash = "Unavailable";
        var kind = DalamudBranchKind.Unknown;
        var detail = "Branch reported by the running Dalamud session. Release status does not verify private DLL dependency compatibility.";

        try
        {
            var info = pluginInterface.GetDalamudVersion();
            rawBranch = info.BetaTrack;
            kind = Classify(rawBranch);
            version = info.Version.ToString();
            clientStructsGitHash = string.IsNullOrWhiteSpace(info.GitHashClientStructs)
                ? "Unavailable" : info.GitHashClientStructs;
            if (kind == DalamudBranchKind.Unknown)
                detail = "The running session did not report a branch. A custom launch or unavailable metadata cannot be treated as release.";
        }
        catch (Exception error)
        {
            // A failed capture must never keep an earlier successful release verdict.
            kind = DalamudBranchKind.Unknown;
            detail = $"Branch detection failed: {error.GetType().Name}: {error.Message}";
        }

        var clientStructsVersion = ReadLoadedClientStructs();
        return new DalamudBranchSnapshot(capturedUtc, rawBranch,
            string.IsNullOrWhiteSpace(rawBranch) ? "Unknown" : rawBranch.Trim().ToLowerInvariant(),
            kind, version, clientStructsVersion, clientStructsGitHash, detail);
    }

    internal static DalamudBranchKind Classify(string? rawBranch)
    {
        var branch = rawBranch?.Trim();
        if (string.IsNullOrEmpty(branch)) return DalamudBranchKind.Unknown;
        if (string.Equals(branch, "release", StringComparison.OrdinalIgnoreCase)) return DalamudBranchKind.Release;
        if (string.Equals(branch, "stg", StringComparison.OrdinalIgnoreCase)) return DalamudBranchKind.Staging;
        if (string.Equals(branch, "dev", StringComparison.OrdinalIgnoreCase)) return DalamudBranchKind.Development;
        if (branch.Length > 3 && branch.StartsWith("api", StringComparison.OrdinalIgnoreCase)
            && branch.AsSpan(3).IndexOfAnyExceptInRange('0', '9') < 0)
            return DalamudBranchKind.ApiPreview;
        return DalamudBranchKind.Other;
    }

    private static string ReadLoadedClientStructs()
    {
        try
        {
            // Report every already-loaded copy with its context; never load a DLL for this probe.
            var identities = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic
                    && string.Equals(assembly.GetName().Name, "FFXIVClientStructs", StringComparison.Ordinal))
                .Select(assembly => $"{assembly.GetName().Version} (context: {AssemblyLoadContext.GetLoadContext(assembly)?.Name ?? "<unnamed>"})")
                .OrderBy(identity => identity, StringComparer.Ordinal)
                .ToArray();
            return identities.Length == 0 ? "Not loaded" : string.Join("; ", identities);
        }
        catch (Exception error)
        {
            // Optional dependency reporting does not change the independently captured branch.
            return $"Unavailable ({error.GetType().Name}: {error.Message})";
        }
    }
}
