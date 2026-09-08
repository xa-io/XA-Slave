using System;

namespace XASlave.Services;

internal enum AutoRetainerUiSection
{
    Retainers,
    Deployables,
}

internal enum AutoRetainerCharacterFilterMode
{
    None,
    Attention,
    MultiEnabled,
    MultiDisabled,
    MissingLifestreamFcAddress,
}

internal static class AutoRetainerCharacterFilterPolicy
{
    public static bool IsMultiModeFilter(AutoRetainerCharacterFilterMode mode)
    {
        return mode is AutoRetainerCharacterFilterMode.MultiEnabled
            or AutoRetainerCharacterFilterMode.MultiDisabled;
    }

    public static bool MatchesMultiModeFilter(
        AutoRetainerUiSection section,
        AutoRetainerCharacterFilterMode mode,
        bool retainersEnabled,
        bool deployablesEnabled,
        bool hasUncheckedRetainer = false,
        bool hasUncheckedSubmarine = false)
    {
        if (!IsMultiModeFilter(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The mode is not an Enabled/Disabled filter.");

        var sectionEnabled = section == AutoRetainerUiSection.Retainers
            ? retainersEnabled
            : deployablesEnabled;
        var sectionHasUncheckedChild = section == AutoRetainerUiSection.Retainers
            ? hasUncheckedRetainer
            : hasUncheckedSubmarine;
        return mode == AutoRetainerCharacterFilterMode.MultiEnabled
            ? sectionEnabled
            : !sectionEnabled || (sectionEnabled && sectionHasUncheckedChild);
    }
}
