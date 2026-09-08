using System;

namespace XASlave.Services;

internal static class DatabaseValueReader
{
    public static string ReadString(object? value)
        => value is null or DBNull ? string.Empty : value.ToString() ?? string.Empty;

    public static long ReadInt64(object? value, long fallback = 0)
    {
        if (value is null or DBNull)
            return fallback;
        try { return Convert.ToInt64(value); }
        catch (Exception) { return fallback; }
    }

    public static int ReadInt32(object? value, int fallback = 0)
    {
        if (value is null or DBNull)
            return fallback;
        try { return Convert.ToInt32(value); }
        catch (Exception) { return fallback; }
    }
}
