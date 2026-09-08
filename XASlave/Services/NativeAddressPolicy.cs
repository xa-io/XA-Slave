using System;

namespace XASlave.Services;

internal static class NativeAddressPolicy
{
    public static bool IsRangeValid(nint address, nint rangeStart, int rangeSize, int requiredBytes)
    {
        if (address == nint.Zero
            || rangeStart == nint.Zero
            || rangeSize <= 0
            || requiredBytes <= 0
            || requiredBytes > rangeSize)
        {
            return false;
        }

        var addressValue = (ulong)(nuint)address;
        var rangeStartValue = (ulong)(nuint)rangeStart;
        if (addressValue < rangeStartValue)
            return false;

        var offset = addressValue - rangeStartValue;
        return offset <= (ulong)(rangeSize - requiredBytes);
    }

    public static bool TryGetBoundedLength(
        nint address,
        nint rangeStart,
        int rangeSize,
        int requestedLength,
        out int boundedLength)
    {
        boundedLength = 0;
        if (requestedLength <= 0 || !IsRangeValid(address, rangeStart, rangeSize, 1))
            return false;

        var offset = (ulong)(nuint)address - (ulong)(nuint)rangeStart;
        boundedLength = (int)Math.Min((ulong)requestedLength, (ulong)rangeSize - offset);
        return boundedLength > 0;
    }

    public static bool CanAdvanceCursor(nint cursor, nint endExclusive, int strideBytes)
    {
        if (cursor == nint.Zero || endExclusive == nint.Zero || strideBytes <= 0)
            return false;

        var cursorValue = (ulong)(nuint)cursor;
        var endValue = (ulong)(nuint)endExclusive;
        return cursorValue < endValue && (ulong)strideBytes <= endValue - cursorValue;
    }
}

internal static class NativeBufferGrowthPolicy
{
    internal const uint MaximumBufferSize = 64 * 1024 * 1024;

    public static bool TryGetNextSize(uint currentSize, uint returnedSize, out uint nextSize)
    {
        nextSize = 0;
        if (returnedSize == 0 || returnedSize <= currentSize || returnedSize > MaximumBufferSize)
            return false;

        nextSize = returnedSize;
        return true;
    }
}

internal static class PointMenuSelectionPolicy
{
    public static int FindFirstUncompleted(int entryCount, int completedBitfield)
    {
        if (entryCount <= 0 || entryCount > 32)
            return -1;

        for (var index = 0; index < entryCount; index++)
        {
            if ((completedBitfield & (1 << index)) == 0)
                return index;
        }

        return -1;
    }
}
