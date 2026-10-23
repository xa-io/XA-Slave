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

    internal global::XASlave.Localization.UiMessage BranchMessage => AutoRetainerUiMessagePresentation.Get(Branch);
    internal global::XASlave.Localization.UiMessage DalamudVersionMessage => AutoRetainerUiMessagePresentation.Get(DalamudVersion);
    internal global::XASlave.Localization.UiMessage ClientStructsVersionMessage => AutoRetainerUiMessagePresentation.Get(ClientStructsVersion);
    internal global::XASlave.Localization.UiMessage ClientStructsGitHashMessage => AutoRetainerUiMessagePresentation.Get(ClientStructsGitHash);
    internal global::XASlave.Localization.UiMessage DetailMessage => AutoRetainerUiMessagePresentation.Get(Detail);
    internal global::XASlave.Localization.UiMessage KindMessage => Kind switch
    {
        DalamudBranchKind.Unknown => global::XASlave.Localization.UiMessage.Key("SB764CDC0EAB71374"),
        DalamudBranchKind.Release => global::XASlave.Localization.UiMessage.Key("SE020E3C67BD0C312"),
        DalamudBranchKind.Staging => global::XASlave.Localization.UiMessage.Key("SA8E7AC92A1164AD8"),
        DalamudBranchKind.ApiPreview => global::XASlave.Localization.UiMessage.Key("S69B080253A5A4067"),
        DalamudBranchKind.Development => global::XASlave.Localization.UiMessage.Key("S21B6A798321A49F7"),
        DalamudBranchKind.Other => global::XASlave.Localization.UiMessage.Key("SF97E9DA0E3B879F0"),
        _ => global::XASlave.Localization.UiMessage.Value(Kind),
    };

    internal bool IsRelease => Kind == DalamudBranchKind.Release;

    internal string PolicyMessage => PolicyMessagePresentation.Original;
    internal global::XASlave.Localization.UiMessage PolicyMessagePresentation => IsRelease
        ? global::XASlave.Localization.UiMessage.Key("S15C9573729DB7C13")
        : Kind == DalamudBranchKind.Unknown
            ? global::XASlave.Localization.UiMessage.Key("SF413BBBED8F3E49F")
            : global::XASlave.Localization.UiMessage.Key("S5DE23484A989E5D3");

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
        var version = AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SCA184496974204A0"));
        var clientStructsGitHash = AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SCA184496974204A0"));
        var kind = DalamudBranchKind.Unknown;
        var detail = AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("SCA1E952AADAD33FC"));

        try
        {
            var info = pluginInterface.GetDalamudVersion();
            rawBranch = info.BetaTrack;
            kind = Classify(rawBranch);
            version = AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Raw(info.Version.ToString()));
            clientStructsGitHash = AutoRetainerUiMessagePresentation.Store((string.IsNullOrWhiteSpace(info.GitHashClientStructs) ? global::XASlave.Localization.UiMessage.Key("SCA184496974204A0") : global::XASlave.Localization.UiMessage.Raw(info.GitHashClientStructs)));
            if (kind == DalamudBranchKind.Unknown)
                detail = AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Key("S97EE7DAED18034CC"));
        }
        catch (Exception error)
        {
            // A failed capture must never keep an earlier successful release verdict.
            kind = DalamudBranchKind.Unknown;
            detail = AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Format("S58602E3150030A09", error.GetType().Name, global::XASlave.Localization.UiMessage.FromException(error)));
        }

        var clientStructsVersion = ReadLoadedClientStructs();
        return new DalamudBranchSnapshot(capturedUtc, rawBranch,
            AutoRetainerUiMessagePresentation.Store((string.IsNullOrWhiteSpace(rawBranch) ? global::XASlave.Localization.UiMessage.Key("SB764CDC0EAB71374") : global::XASlave.Localization.UiMessage.Raw(rawBranch.Trim().ToLowerInvariant()))),
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
                .Select(assembly => global::XASlave.Localization.UiMessage.Format("S7462756AF770C086", assembly.GetName().Version, (AssemblyLoadContext.GetLoadContext(assembly)?.Name is { } contextName ? global::XASlave.Localization.UiMessage.Raw(contextName) : global::XASlave.Localization.UiMessage.Key("SB42151CA1488F5E8"))))
                .OrderBy(identity => identity.Original, StringComparer.Ordinal)
                .ToArray();
            return AutoRetainerUiMessagePresentation.Store(identities.Length == 0 ? global::XASlave.Localization.UiMessage.Key("S9B41BA20B8A3C870") : global::XASlave.Localization.UiMessage.Join("; ", identities));
        }
        catch (Exception error)
        {
            // Optional dependency reporting does not change the independently captured branch.
            return AutoRetainerUiMessagePresentation.Store(global::XASlave.Localization.UiMessage.Format("S07E75F84B9846061", error.GetType().Name, global::XASlave.Localization.UiMessage.FromException(error)));
        }
    }
}
