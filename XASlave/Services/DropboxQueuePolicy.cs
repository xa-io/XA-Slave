using System;

namespace XASlave.Services;

internal static class DropboxQueuePolicy
{
    public static bool TryCalculateTargetQuantity(int existingQuantity, int requestedQuantity, out int targetQuantity)
    {
        targetQuantity = 0;
        if (existingQuantity < 0 || requestedQuantity <= 0)
            return false;

        targetQuantity = (int)Math.Min(int.MaxValue, (long)existingQuantity + requestedQuantity);
        return true;
    }

    public static bool ShouldStartTrading(int queuedEntries)
        => queuedEntries > 0;
}
