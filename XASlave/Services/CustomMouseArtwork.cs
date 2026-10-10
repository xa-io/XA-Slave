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
            throw Invalid("Cursor artwork must contain between 6 bytes and 16 MiB.");
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
            throw Invalid("PNG requires a complete image header.");
        var width = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(20, 4));
        if (width is 0 or > MaximumDimension || height is 0 or > MaximumDimension)
            throw Invalid("PNG cursor dimensions must be between 1 and 256 pixels.");
        ValidatePng(png, (int)width, (int)height);
        return new CustomMouseArtwork(
            [new CustomMouseArtworkFrame((int)width, (int)height, 0, 0, null, png.ToArray())],
            [new CustomMouseArtworkStep(0, 100)]);
    }

    private static CustomMouseArtwork LoadAnimation(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || !bytes.Slice(8, 4).SequenceEqual("ACON"u8) || U32(bytes, 4) != bytes.Length - 8)
            throw Invalid("ANI requires a complete RIFF ACON container.");
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
                    throw Invalid("ANI requires one 36-byte animation header.");
                header = ReadAnimationHeader(chunk);
            }
            else if (tag.SequenceEqual("rate"u8))
            {
                if (rates != null) throw Invalid("ANI contains duplicate frame rates.");
                rates = ReadStepValues(chunk);
            }
            else if (tag.SequenceEqual("seq "u8))
            {
                if (sequence != null) throw Invalid("ANI contains duplicate frame sequences.");
                sequence = ReadStepValues(chunk);
            }
            else if (tag.SequenceEqual("LIST"u8))
            {
                if (chunk.Length < 4) throw Invalid("ANI contains a truncated list.");
                if (!chunk[..4].SequenceEqual("fram"u8)) continue;
                if (hasFrameList) throw Invalid("ANI contains duplicate frame lists.");
                hasFrameList = true;
                var framePosition = 4;
                while (framePosition < chunk.Length)
                {
                    var frameChunk = ReadRiffChunk(chunk, ref framePosition, out var frameTag);
                    if (!frameTag.SequenceEqual("icon"u8)) continue;
                    if (frames.Count == MaximumFrames) throw Invalid("ANI exceeds 64 frames.");
                    frames.Add(LoadCursor(frameChunk));
                }
            }
        }
        if (header == null || !hasFrameList || frames.Count != header.Value.FrameCount)
            throw Invalid("ANI frame count does not match its header.");
        var info = header.Value;
        if (rates != null && rates.Length != info.StepCount)
            throw Invalid("ANI frame-rate count does not match its playback steps.");
        if (info.HasSequence != (sequence != null) || sequence != null && sequence.Length != info.StepCount)
            throw Invalid("ANI sequence does not match its header.");
        if (sequence == null && info.StepCount != frames.Count)
            throw Invalid("ANI without a sequence must have one playback step per frame.");
        var steps = new CustomMouseArtworkStep[info.StepCount];
        for (var i = 0; i < steps.Length; i++)
        {
            var frameIndex = sequence == null ? (uint)i : sequence[i];
            if (frameIndex >= frames.Count) throw Invalid("ANI sequence references an unavailable frame.");
            var jiffies = rates == null ? info.DefaultRate : rates[i];
            var milliseconds = (jiffies * 1000L + 30) / 60;
            if (jiffies == 0 || milliseconds > int.MaxValue)
                throw Invalid("ANI frame delays must be positive and fit within the supported timer.");
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
            throw Invalid("ANI supports 1-64 frames and 1-256 playback steps.");
        if (U32(header, 12) > MaximumDimension || U32(header, 16) > MaximumDimension)
            throw Invalid("ANI dimensions exceed 256 pixels.");
        if ((flags & 1) == 0 || (flags & ~3u) != 0)
            throw Invalid("ANI must contain CUR frame images with supported animation flags.");
        return new AnimationHeader((int)frames, (int)steps, U32(header, 28), (flags & 2) != 0);
    }

    private static uint[] ReadStepValues(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length == 0 || chunk.Length % 4 != 0 || chunk.Length / 4 > MaximumSteps)
            throw Invalid("ANI playback arrays must contain 1-256 complete values.");
        var values = new uint[chunk.Length / 4];
        for (var i = 0; i < values.Length; i++) values[i] = U32(chunk, i * 4);
        return values;
    }

    private static ReadOnlySpan<byte> ReadRiffChunk(ReadOnlySpan<byte> container, ref int position, out ReadOnlySpan<byte> tag)
    {
        if (container.Length - position < 8) throw Invalid("ANI contains a truncated chunk header.");
        tag = container.Slice(position, 4);
        var length = U32(container, position + 4);
        var bodyStart = position + 8;
        if (length > container.Length - bodyStart) throw Invalid("ANI chunk extends outside its container.");
        var bodyEnd = bodyStart + (int)length;
        if ((length & 1) != 0 && bodyEnd == container.Length) throw Invalid("ANI chunk is missing its padding byte.");
        position = bodyEnd + (int)(length & 1);
        return container.Slice(bodyStart, (int)length);
    }

    private static CustomMouseArtworkFrame LoadCursor(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 6 || U16(bytes, 0) != 0 || U16(bytes, 2) != 2)
            throw Invalid("Artwork must be a Windows CUR file or an ANI containing CUR frames.");
        var count = U16(bytes, 4);
        if (count is 0 or > MaximumFrames || bytes.Length < 6 + count * 16)
            throw Invalid("CUR requires a complete directory of 1-64 images.");
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
                throw Invalid("CUR contains an invalid hotspot or directory entry.");
            if (offset < directoryEnd || offset > bytes.Length || length == 0 || length > bytes.Length - offset)
                throw Invalid("CUR image extends outside its file.");
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
        if (image.Length < 40) throw Invalid("CUR bitmap header is truncated.");
        var headerSize = U32(image, 0);
        if (headerSize is not (40 or 108 or 124) || headerSize > image.Length)
            throw Invalid("CUR requires a supported BITMAPINFOHEADER, V4 or V5 bitmap.");
        var dibWidth = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(4, 4));
        var dibHeight = BinaryPrimitives.ReadInt32LittleEndian(image.Slice(8, 4));
        if (dibWidth != width || dibHeight != height * 2 && dibHeight != -height * 2)
            throw Invalid("CUR bitmap dimensions do not match the image directory.");
        if (U16(image, 12) != 1 || U16(image, 14) != 32 || U32(image, 16) != 0 || U32(image, 32) != 0)
            throw Invalid("CUR bitmaps must use uncompressed 32-bit BGRA pixels.");
        var pixelOffset = (int)headerSize;
        var pixelStride = width * 4;
        var pixelBytes = pixelStride * height;
        var maskStride = ((width + 31) / 32) * 4;
        var maskOffset = pixelOffset + pixelBytes;
        if (image.Length < maskOffset + maskStride * height)
            throw Invalid("CUR bitmap pixels or transparency mask are truncated.");
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
            if (png.Length - position < 12) throw Invalid("PNG contains a truncated chunk.");
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(position, 4));
            if (length > png.Length - position - 12) throw Invalid("PNG chunk extends outside its image.");
            var tag = png.Slice(position + 4, 4);
            var body = png.Slice(position + 8, (int)length);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(position + 8 + (int)length, 4));
            if (PngCrc(png.Slice(position + 4, (int)length + 4)) != expectedCrc)
                throw Invalid("PNG chunk checksum is invalid.");
            position += (int)length + 12;
            if (!hasHeader && !tag.SequenceEqual("IHDR"u8)) throw Invalid("PNG must begin with IHDR.");
            if (tag.SequenceEqual("acTL"u8) || tag.SequenceEqual("fcTL"u8) || tag.SequenceEqual("fdAT"u8))
                throw Invalid("Animated PNG is not supported. Use static PNG artwork or an ANI animation.");
            if (tag.SequenceEqual("IHDR"u8))
            {
                if (hasHeader || length != 13) throw Invalid("PNG requires one complete image header.");
                hasHeader = true;
                if (BinaryPrimitives.ReadUInt32BigEndian(body[..4]) != width || BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4)) != height)
                    throw Invalid("PNG dimensions do not match the expected image size.");
                var depth = body[8];
                var validDepth = body[9] switch
                {
                    0 => depth is 1 or 2 or 4 or 8 or 16,
                    2 or 4 or 6 => depth is 8 or 16,
                    3 => depth is 1 or 2 or 4 or 8,
                    _ => false,
                };
                if (!validDepth || body[10] != 0 || body[11] != 0 || body[12] > 1)
                    throw Invalid("PNG image format is unsupported.");
            }
            else if (tag.SequenceEqual("IDAT"u8))
            {
                if (dataEnded) throw Invalid("PNG image data chunks must be consecutive.");
                if (length > 0) hasData = true;
            }
            else if (tag.SequenceEqual("IEND"u8))
            {
                if (!hasData || length != 0 || position != png.Length) throw Invalid("PNG image data is incomplete.");
                return;
            }
            else if (hasData) dataEnded = true;
        }
        throw Invalid("PNG is missing its end marker.");
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
    private static InvalidDataException Invalid(string message) => new(message);
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
