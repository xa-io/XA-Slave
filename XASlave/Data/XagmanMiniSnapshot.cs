namespace XASlave.Data;

internal sealed record XagmanMiniSnapshot(
    bool HasRun, bool Running, XagmanRole Role, bool OutsideNetworkHelper,
    global::XASlave.Localization.UiMessage State, string Character, int Processed, int Total, global::XASlave.Localization.UiMessage ProgressLabel,
    int Failed, int Skipped, int Unfinished, global::XASlave.Localization.UiMessage Estimate)
{
    internal static readonly XagmanMiniSnapshot Empty = new(
        false, false, XagmanRole.Tony, false, global::XASlave.Localization.UiMessage.Key("SAB0171CA0494D441"), string.Empty,
        0, 0, global::XASlave.Localization.UiMessage.Key("S58190FFABF981AA3"), 0, 0, 0, global::XASlave.Localization.UiMessage.Key("SCA184496974204A0"));
}
