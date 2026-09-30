namespace XASlave.Data;

internal sealed record XagmanMiniSnapshot(
    bool HasRun, bool Running, XagmanRole Role, bool OutsideNetworkHelper,
    string State, string Character, int Processed, int Total, string ProgressLabel,
    int Failed, int Skipped, int Unfinished, string Estimate)
{
    internal static readonly XagmanMiniSnapshot Empty = new(
        false, false, XagmanRole.Tony, false, "Idle", string.Empty,
        0, 0, "processed", 0, 0, 0, "Unavailable");
}
