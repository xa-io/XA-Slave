using System;

namespace XASlave.Services;

/// <summary>Reporting only. Never use this policy to change trade quantities or completion ledgers.</summary>
internal static class XagmanShortfallPolicy
{
    internal static bool IsIgnoredGilShortfall(bool ignoreGilShortfalls, uint itemId)
        => ignoreGilShortfalls && itemId == 1;

    internal static long GetReportedShortfall(bool ignoreGilShortfalls, uint itemId, long quantity)
        => IsIgnoredGilShortfall(ignoreGilShortfalls, itemId) ? 0L : Math.Max(0L, quantity);
}
