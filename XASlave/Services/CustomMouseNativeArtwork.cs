using System;
using System.IO;

namespace XASlave.Services;

/// <summary>Copies the exact native software-quad texel extent and hotspot from a managed game atlas.</summary>
internal static class CustomMouseNativeArtwork
{
    // Installed 2026-09-17 client table at RVA 0x289BA30. Type 17 and unknown values retain native handling.
    private static ReadOnlySpan<byte> TypeCells => [1, 8, 10, 7, 9, 11, 2, 12, 13, 14, 15, 4, 0, 6, 3, 16, 5];
    internal const int CursorTypeCount = 17;

    internal static int GetCell(uint type) => type < CursorTypeCount ? TypeCells[(int)type] : -1;

    internal static Frame Extract(byte[] atlasBgra, int atlasWidth, int atlasHeight, int preset, uint type)
    {
        ArgumentNullException.ThrowIfNull(atlasBgra);
        var cell = GetCell(type);
        if (cell < 0) throw new ArgumentOutOfRangeException(nameof(type));
        if (preset is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(preset));
        if (atlasWidth is < 1 or > 2048 || atlasHeight is < 1 or > 2048
            || atlasBgra.Length != checked(atlasWidth * atlasHeight * 4))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S261914D636D704A9"), originalMessage => new InvalidDataException(originalMessage));
        var cellSize = 64 + 32 * preset;
        var extent = cellSize - 1; // Native quad width and UV extent both exclude the final padding texel.
        var left = cell % 4 * cellSize;
        var top = cell / 4 * cellSize;
        if (left + extent > atlasWidth || top + extent > atlasHeight)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S89F3F69425610DC1"), originalMessage => new InvalidDataException(originalMessage));
        var pixels = new byte[checked(extent * extent * 4)];
        for (var row = 0; row < extent; row++)
            atlasBgra.AsSpan(((top + row) * atlasWidth + left) * 4, extent * 4)
                .CopyTo(pixels.AsSpan(row * extent * 4, extent * 4));
        return new Frame(extent, extent, cellSize / 2, cellSize * 3 / 8, pixels);
    }

    internal readonly record struct Frame(int Width, int Height, int HotspotX, int HotspotY, byte[] Bgra);
}
