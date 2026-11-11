using System;

namespace XASlave.Services;

/// <summary>The active Tony supplies the required loaded versions for a trade.</summary>
internal static class XagmanTradeVersionPolicy
{
    internal const int Revision = 1;

    internal static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Length > 32 || value != value.Trim())
            return false;
        foreach (var character in value)
            if (character != '.' && (character < '0' || character > '9'))
                return false;
        if (!Version.TryParse(value, out var version) || version.Build < 0 || version.Revision < 0)
            return false;
        normalized = version.ToString(4);
        return true;
    }

    internal static bool TryMatch(string? tonySlave, string? tonyDropbox,
        string? otherSlave, string? otherDropbox, out string failure)
    {
        failure = string.Empty;
        if (!TryNormalize(tonySlave, out var expectedSlave) || !TryNormalize(tonyDropbox, out var expectedDropbox))
        {
            failure = "The active Tony's loaded XA Slave or Dropbox version is unavailable.";
            return false;
        }
        if (!TryNormalize(otherSlave, out var actualSlave) || !TryNormalize(otherDropbox, out var actualDropbox))
        {
            failure = $"Tony requires XA Slave {expectedSlave} and Dropbox {expectedDropbox}; the trading partner's loaded versions are unavailable.";
            return false;
        }
        if (expectedSlave != actualSlave || expectedDropbox != actualDropbox)
        {
            failure = $"Tony requires XA Slave {expectedSlave} and Dropbox {expectedDropbox}; the trading partner reports XA Slave {actualSlave} and Dropbox {actualDropbox}.";
            return false;
        }
        return true;
    }
}
