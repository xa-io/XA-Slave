using System;

namespace XASlave.Services;

public readonly record struct DropboxQueueQuantityPlan(
    int NormalQualityQuantity,
    int HighQualityQuantity)
{
    public int TotalQuantity => NormalQualityQuantity + HighQualityQuantity;
}

public static class DropboxQueueQuantityPlanner
{
    public static DropboxQueueQuantityPlan Create(
        int normalQualityAvailable,
        int highQualityAvailable,
        int requestedQuantity)
    {
        var nqAvailable = Math.Max(0, normalQualityAvailable);
        var hqAvailable = Math.Max(0, highQualityAvailable);
        var totalAvailable = Math.Min(int.MaxValue, (long)nqAvailable + hqAvailable);
        var quantityToQueue = requestedQuantity < 0
            ? Math.Max(0L, totalAvailable + requestedQuantity)
            : Math.Min(totalAvailable, requestedQuantity);

        var nqQuantity = (int)Math.Min(nqAvailable, quantityToQueue);
        var hqQuantity = (int)Math.Min(hqAvailable, quantityToQueue - nqQuantity);
        return new DropboxQueueQuantityPlan(nqQuantity, hqQuantity);
    }
}
