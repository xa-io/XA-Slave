using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace XASlave.Services;

/// <summary>
/// Patch-sensitive native offsets and array indices that ClientStructs does not currently expose
/// as named fields. Keep them centralized so patch-day review has one explicit compatibility list.
/// </summary>
internal static class NativeOffsets
{
    public const int FreeCompanyChestContextInventoryType = 0x1B2C;
    public const int FreeCompanyChestContextInventorySlot = 0x1B30;

    public const int WorldTravelQueueActive = 0x120;
    public const int WorldTravelQueueElapsedSeconds = 0x128;
    public const int WorldTravelQueuePosition = 0x12C;
    public const int WorldTravelQueueVariantNumberIndex = 5;

    public const int LetterTransferBusyNumberIndex = 136;

    private static readonly object ReportLock = new();
    private static readonly HashSet<string> ReportedLayouts = new(StringComparer.Ordinal);

    public static unsafe bool FitsIn<T>(int offset, int byteCount)
        where T : unmanaged
        => offset >= 0 && byteCount > 0 && offset <= sizeof(T) - byteCount;

    public static void ReportOnce(IPluginLog log, string key, bool compatible, string detail)
    {
        lock (ReportLock)
        {
            if (!ReportedLayouts.Add(key))
                return;
        }

        if (compatible)
            log.Information($"[XASlave] Native layout {key}: {detail}");
        else
            log.Warning($"[XASlave] Native layout {key} is incompatible; feature path disabled. {detail}");
    }
}
