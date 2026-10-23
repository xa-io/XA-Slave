using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace XASlave.Services;

/// <summary>A bounded managed copy of CUR, ANI or static PNG artwork; decoding and timing never access native game state.</summary>
public sealed class CustomMouseArtwork
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    public const int MaximumFrames = 64;
    public const int MaximumSteps = 256;
    public const int MaximumDimension = 256;
    private static readonly uint[] PngCrcTable = CreatePngCrcTable();
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
    private readonly long totalDurationMilliseconds;

    public IReadOnlyList<CustomMouseArtworkFrame> Frames { get; }
    public IReadOnlyList<CustomMouseArtworkStep> Steps { get; }

    private CustomMouseArtwork(CustomMouseArtworkFrame[] frames, CustomMouseArtworkStep[] steps)
    {
        Frames = Array.AsReadOnly(frames);
        Steps = Array.AsReadOnly(steps);
        foreach (var step in steps) totalDurationMilliseconds += step.DurationMilliseconds;
    }

    /// <summary>Detects artwork by its file signature. Standalone PNGs are one static frame with a top-left click hotspot.</summary>
    public static CustomMouseArtwork Load(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 6 || bytes.Length > MaximumBytes)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("SA9C88BF7DB759550"));
        if (bytes.AsSpan().StartsWith(PngSignature)) return LoadPng(bytes);
        if (bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)) return LoadAnimation(bytes);
        return new CustomMouseArtwork([LoadCursor(bytes)], [new CustomMouseArtworkStep(0, 100)]);
    }

    /// <summary>Animation off freezes the first playback step; elapsed time wraps deterministically over the stored durations.</summary>
    public int GetFrameIndex(long elapsedMilliseconds, bool animate)
    {
        if (!animate || elapsedMilliseconds <= 0 || Steps.Count == 1) return Steps[0].FrameIndex;
        var position = elapsedMilliseconds % totalDurationMilliseconds;
        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            if (position < step.DurationMilliseconds) return step.FrameIndex;
            position -= step.DurationMilliseconds;
        }
        return Steps[0].FrameIndex;
    }

    private static CustomMouseArtwork LoadPng(ReadOnlySpan<byte> png)
    {
        if (png.Length < 33 || BinaryPrimitives.ReadUInt32BigEndian(png.Slice(8, 4)) != 13 || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw Invalid(global::XASlave.Localization.UiMessage.Key("SD15B013DE9402EA7"));
        var width = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        if (width is 0 or > MaximumDimension || height is 0 or > MaximumDimension)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("S48B1946452114E31"));
        ValidatePng(png, (int)width, (int)height);
        return new CustomMouseArtwork(
            [new CustomMouseArtworkFrame((int)width, (int)height, 0, 0, null, png.ToArray())],
            [new CustomMouseArtworkStep(0, 100)]);
    }

    private static CustomMouseArtwork LoadAnimation(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes.Slice(8, 4).SequenceEqual("ACON"u8) || U32(bytes, 4) != bytes.Length - 8)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("S5F50043289BE26B8"));
        AnimationHeader? header = null;
        uint[]? rates = null;
        uint[]? sequence = null;
        var frames = new List<CustomMouseArtworkFrame>();
        var hasFrameList = false;
        var position = 12;
        while (position < bytes.Length)
        {
            var chunk = ReadRiffChunk(bytes, ref position, out var tag);
            if (tag.SequenceEqual("anih"u8))
            {
                if (header != null || chunk.Length != 36 || U32(chunk, 0) != 36)
                    throw Invalid(global::XASlave.Localization.UiMessage.Key("SA0F378E72315AE49"));
                header = ReadAnimationHeader(chunk);
            }
            else if (tag.SequenceEqual("rate"u8))
            {
                if (rates != null) throw Invalid(global::XASlave.Localization.UiMessage.Key("S4B77B33BD898C10E"));
                rates = ReadStepValues(chunk);
            }
            else if (tag.SequenceEqual("seq "u8))
            {
                if (sequence != null) throw Invalid(global::XASlave.Localization.UiMessage.Key("S1590D4105B7D240B"));
                sequence = ReadStepValues(chunk);
            }
            else if (tag.SequenceEqual("LIST"u8))
            {
                if (chunk.Length < 4) throw Invalid(global::XASlave.Localization.UiMessage.Key("SA9EF40E18BB7F6C3"));
                if (!chunk[..4].SequenceEqual("fram"u8)) continue;
                if (hasFrameList) throw Invalid(global::XASlave.Localization.UiMessage.Key("SBBCFE21A200DB648"));
                hasFrameList = true;
                var framePosition = 4;
                while (framePosition < chunk.Length)
                {
                    var frameChunk = ReadRiffChunk(chunk, ref framePosition, out var frameTag);
                    if (!frameTag.SequenceEqual("icon"u8)) continue;
                    if (frames.Count == MaximumFrames) throw Invalid(global::XASlave.Localization.UiMessage.Key("SD239701EA10C1DF9"));
                    frames.Add(LoadCursor(frameChunk));
                }
            }
        }
        if (header == null || !hasFrameList || frames.Count != header.Value.FrameCount)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("S10A3D6E0F488D3C1"));
        var info = header.Value;
        if (rates != null && rates.Length != info.StepCount)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("SEAA1787EA9A643EB"));
        if (info.HasSequence != (sequence != null) || sequence != null && sequence.Length != info.StepCount)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("S5A8C6D0A34A2B2B8"));
        if (sequence == null && info.StepCount != frames.Count)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("S3726496FCA1CC915"));
        var steps = new CustomMouseArtworkStep[info.StepCount];
        for (var i = 0; i < steps.Length; i++)
        {
            var frameIndex = sequence == null ? (uint)i : sequence[i];
            if (frameIndex >= frames.Count) throw Invalid(global::XASlave.Localization.UiMessage.Key("SB9EA759F27502423"));
            var jiffies = rates == null ? info.DefaultRate : rates[i];
            var milliseconds = (jiffies * 1000L + 30) / 60;
            if (jiffies == 0 || milliseconds > int.MaxValue)
                throw Invalid(global::XASlave.Localization.UiMessage.Key("SD657499ED3C8DC1D"));
            steps[i] = new CustomMouseArtworkStep((int)frameIndex, (int)milliseconds);
        }
        return new CustomMouseArtwork(frames.ToArray(), steps);
    }

    private static AnimationHeader ReadAnimationHeader(ReadOnlySpan<byte> header)
    {
        var frames = U32(header, 4);
        var steps = U32(header, 8);
        var flags = U32(header, 32);
        if (frames is 0 or > MaximumFrames || steps is 0 or > MaximumSteps)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("SF92D3E60C557D5AB"));
        if (U32(header, 12) > MaximumDimension || U32(header, 16) > MaximumDimension)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("SE332EE673916EA05"));
        if ((flags & 1) == 0 || (flags & ~3u) != 0)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("S4A3B51BED707FAE1"));
        return new AnimationHeader((int)frames, (int)steps, U32(header, 28), (flags & 2) != 0);
    }

    private static uint[] ReadStepValues(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length == 0 || chunk.Length % 4 != 0 || chunk.Length / 4 > MaximumSteps)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("S22E8FF908C033ED9"));
        var values = new uint[chunk.Length / 4];
        for (var i = 0; i < values.Length; i++) values[i] = U32(chunk, i * 4);
        return values;
    }

    private static ReadOnlySpan<byte> ReadRiffChunk(ReadOnlySpan<byte> container, ref int position, out ReadOnlySpan<byte> tag)
    {
        if (container.Length - position < 8) throw Invalid(global::XASlave.Localization.UiMessage.Key("SAF1170940A392268"));
        tag = container.Slice(position, 4);
        var length = U32(container, position + 4);
        var bodyStart = position + 8;
        if (length > container.Length - bodyStart) throw Invalid(global::XASlave.Localization.UiMessage.Key("SADB47C001D8BC89C"));
        var bodyEnd = bodyStart + (int)length;
        if ((length & 1) != 0 && bodyEnd == container.Length) throw Invalid(global::XASlave.Localization.UiMessage.Key("S019C9EA39BDF7D6F"));
        position = bodyEnd + (int)(length & 1);
        return container.Slice(bodyStart, (int)length);
    }

    private static CustomMouseArtworkFrame LoadCursor(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6 || U16(bytes, 0) != 0 || U16(bytes, 2) != 2)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("SB0AEB50F65E2D5A5"));
        var count = U16(bytes, 4);
        if (count is 0 or > MaximumFrames || bytes.Length < 6 + count * 16)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("S68679967BC7E2F8A"));
        var selected = -1;
        var selectedArea = 0;
        var directoryEnd = 6 + count * 16;
        for (var i = 0; i < count; i++)
        {
            var entry = bytes.Slice(6 + i * 16, 16);
            var width = entry[0] == 0 ? 256 : entry[0];
            var height = entry[1] == 0 ? 256 : entry[1];
            var length = U32(entry, 8);
            var offset = U32(entry, 12);
            if (entry[3] != 0 || U16(entry, 4) >= width || U16(entry, 6) >= height)
                throw Invalid(global::XASlave.Localization.UiMessage.Key("SE9176B94E7BC08CA"));
            if (offset < directoryEnd || offset > bytes.Length || length == 0 || length > bytes.Length - offset)
                throw Invalid(global::XASlave.Localization.UiMessage.Key("SE29A2E6A1B6DE5F1"));
            // A CUR directory stores alternate resolutions of one cursor, not animation frames.
            if (width * height > selectedArea) { selected = i; selectedArea = width * height; }
        }
        var selectedEntry = bytes.Slice(6 + selected * 16, 16);
        var selectedWidth = selectedEntry[0] == 0 ? 256 : selectedEntry[0];
        var selectedHeight = selectedEntry[1] == 0 ? 256 : selectedEntry[1];
        var hotspotX = U16(selectedEntry, 4);
        var hotspotY = U16(selectedEntry, 6);
        var image = bytes.Slice((int)U32(selectedEntry, 12), (int)U32(selectedEntry, 8));
        if (image.StartsWith(PngSignature))
        {
            ValidatePng(image, selectedWidth, selectedHeight);
            return new CustomMouseArtworkFrame(selectedWidth, selectedHeight, hotspotX, hotspotY, null, image.ToArray());
        }
        return new CustomMouseArtworkFrame(selectedWidth, selectedHeight, hotspotX, hotspotY,
            DecodeDib(image, selectedWidth, selectedHeight), null);
    }

    private static byte[] DecodeDib(ReadOnlySpan<byte> image, int width, int height)
    {
        if (image.Length < 40) throw Invalid(global::XASlave.Localization.UiMessage.Key("SCD850F9F47444EEE"));
        var headerSize = U32(image, 0);
        if (headerSize is not (40 or 108 or 124) || headerSize > image.Length)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("SB39842542158DD1A"));
        var dibWidth = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(4, 4));
        var dibHeight = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(8, 4));
        if (dibWidth != width || dibHeight != height * 2 && dibHeight != -height * 2)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("S4788444DC5E3D4D4"));
        if (U16(image, 12) != 1 || U16(image, 14) != 32 || U32(image, 16) != 0 || U32(image, 32) != 0)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("S57CF9BCE353DD98B"));
        var pixelOffset = (int)headerSize;
        var pixelStride = width * 4;
        var pixelBytes = pixelStride * height;
        var maskStride = ((width + 31) / 32) * 4;
        var maskOffset = pixelOffset + pixelBytes;
        if (image.Length < maskOffset + maskStride * height)
            throw Invalid(global::XASlave.Localization.UiMessage.Key("S44F8DDBE5519F2BE"));
        var hasAlpha = false;
        for (var i = pixelOffset + 3; i < maskOffset; i += 4)
            if (image[i] != 0) { hasAlpha = true; break; }
        var pixels = new byte[pixelBytes];
        for (var y = 0; y < height; y++)
        {
            var sourceY = dibHeight < 0 ? y : height - 1 - y;
            image.Slice(pixelOffset + sourceY * pixelStride, pixelStride).CopyTo(pixels.AsSpan(y * pixelStride));
            // Modern 32-bit cursors own an alpha channel; the legacy AND mask is used only when that channel is empty.
            if (hasAlpha) continue;
            for (var x = 0; x < width; x++)
            {
                var transparent = (image[maskOffset + sourceY * maskStride + x / 8] & (0x80 >> (x & 7))) != 0;
                pixels[y * pixelStride + x * 4 + 3] = transparent ? (byte)0 : (byte)255;
            }
        }
        return pixels;
    }

    private static void ValidatePng(ReadOnlySpan<byte> png, int width, int height)
    {
        var position = 8;
        var hasHeader = false;
        var hasData = false;
        var dataEnded = false;
        while (position < png.Length)
        {
            if (png.Length - position < 12) throw Invalid(global::XASlave.Localization.UiMessage.Key("SD07AF6A91F2C85DA"));
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(position, 4));
            if (length > png.Length - position - 12) throw Invalid(global::XASlave.Localization.UiMessage.Key("SAA65008DA80870A1"));
            var tag = png.Slice(position + 4, 4);
            var body = png.Slice(position + 8, (int)length);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(position + 8 + (int)length, 4));
            if (PngCrc(png.Slice(position + 4, (int)length + 4)) != expectedCrc)
                throw Invalid(global::XASlave.Localization.UiMessage.Key("S1F4F412B8A3BEE87"));
            position += (int)length + 12;
            if (!hasHeader && !tag.SequenceEqual("IHDR"u8)) throw Invalid(global::XASlave.Localization.UiMessage.Key("S7E40C68F1579EE50"));
            if (tag.SequenceEqual("acTL"u8) || tag.SequenceEqual("fcTL"u8) || tag.SequenceEqual("fdAT"u8))
                throw Invalid(global::XASlave.Localization.UiMessage.Key("SE81473D109FF89B8"));
            if (tag.SequenceEqual("IHDR"u8))
            {
                if (hasHeader || length != 13) throw Invalid(global::XASlave.Localization.UiMessage.Key("S8E038EDF0DEC8EF3"));
                hasHeader = true;
                if (BinaryPrimitives.ReadUInt32BigEndian(body[..4]) != width || BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != height)
                    throw Invalid(global::XASlave.Localization.UiMessage.Key("S50EB0EDF93DE6433"));
                var depth = body[8];
                var validDepth = body[9] switch
                {
                    0 => depth is 1 or 2 or 4 or 8 or 16,
                    2 or 4 or 6 => depth is 8 or 16,
                    3 => depth is 1 or 2 or 4 or 8,
                    _ => false,
                };
                if (!validDepth || body[10] != 0 || body[11] != 0 || body[12] > 1)
                    throw Invalid(global::XASlave.Localization.UiMessage.Key("SAE106FF4AC200FF3"));
            }
            else if (tag.SequenceEqual("IDAT"u8))
            {
                if (dataEnded) throw Invalid(global::XASlave.Localization.UiMessage.Key("S04F42A5C622C18E2"));
                if (length > 0) hasData = true;
            }
            else if (tag.SequenceEqual("IEND"u8))
            {
                if (!hasData || length != 0 || position != png.Length) throw Invalid(global::XASlave.Localization.UiMessage.Key("SA1B13B08C84C328B"));
                return;
            }
            else if (hasData) dataEnded = true;
        }
        throw Invalid(global::XASlave.Localization.UiMessage.Key("SA08514EC662600DD"));
    }

    private static uint[] CreatePngCrcTable()
    {
        var table = new uint[256];
        for (var i = 0; i < table.Length; i++)
        {
            var crc = (uint)i;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    private static uint PngCrc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes) crc = PngCrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        return crc ^ uint.MaxValue;
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
    private static InvalidDataException Invalid(global::XASlave.Localization.UiMessage message) => global::XASlave.Localization.UiMessage.CreateException(message, originalMessage => new InvalidDataException(originalMessage));
    private readonly record struct AnimationHeader(int FrameCount, int StepCount, uint DefaultRate, bool HasSequence);
}

public sealed class CustomMouseArtworkFrame
{
    public int Width { get; }
    public int Height { get; }
    public int HotspotX { get; }
    public int HotspotY { get; }
    public byte[]? PixelsBgra { get; }
    public byte[]? EncodedPng { get; }

    internal CustomMouseArtworkFrame(int width, int height, int hotspotX, int hotspotY, byte[]? pixelsBgra, byte[]? encodedPng)
    {
        Width = width;
        Height = height;
        HotspotX = hotspotX;
        HotspotY = hotspotY;
        PixelsBgra = pixelsBgra;
        EncodedPng = encodedPng;
    }
}

public sealed record CustomMouseArtworkStep(int FrameIndex, int DurationMilliseconds);
