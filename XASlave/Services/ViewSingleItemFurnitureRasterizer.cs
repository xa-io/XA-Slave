using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace XASlave.Services;

// Renders detached housing geometry only. This owns no native game object or GPU
// resource; callers run it off the framework thread and own the uploaded image.
internal static class ViewSingleItemFurnitureRasterizer
{
    private const int MaximumDimension = 1024;
    private const int MaximumTriangles = 200_000;
    private const int MaximumVertices = 1_000_000;
    private const long MaximumPixelTests = 64L * 1024 * 1024;
    private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(-0.3f, 0.5f, 1));

    private readonly record struct Vertex(Vector3 Position, Vector2 UV, Vector4 Color, float Light);
    private sealed record Texture(int Width, int Height, byte[] Bgra, bool Translucent);
    private readonly record struct Triangle(Vertex A, Vertex B, Vertex C, Texture? Texture, float Depth);

    private sealed class Budget(CancellationToken cancellation)
    {
        private readonly long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
        private long pixelTests;

        internal void Check()
        {
            cancellation.ThrowIfCancellationRequested();
            if (Stopwatch.GetTimestamp() > deadline)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S440F74235E30C1FB"), originalMessageText => new InvalidOperationException(originalMessageText));
        }

        internal void AddPixels(long count)
        {
            pixelTests += count;
            if (pixelTests > MaximumPixelTests)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S6873C685620E6A32"), originalMessageText => new InvalidOperationException(originalMessageText));
            Check();
        }
    }

    // Angles are radians: Y yaw, X pitch, then camera-facing Z roll. Positive
    // roll appears clockwise on the image. Pan is a fraction of the canvas,
    // positive right/down. A bounds sphere preserves the fit while rotating.
    internal static byte[] Render(ViewSingleItemFurnitureModel model, int width, int height,
        float yaw, float pitch, float roll, float zoom, Vector2 pan, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (width < 1 || height < 1 || width > MaximumDimension || height > MaximumDimension)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S0A2F07678512B217"), originalMessageText => new ArgumentOutOfRangeException(nameof(width), originalMessageText));
        if (!Finite(model.Minimum) || !Finite(model.Maximum) || !float.IsFinite(yaw)
            || !float.IsFinite(pitch) || !float.IsFinite(roll) || !float.IsFinite(zoom)
            || !float.IsFinite(pan.X) || !float.IsFinite(pan.Y))
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S48712DFDF3B17857"), originalMessageText => new ArgumentException(originalMessageText));
        var extent = model.Maximum - model.Minimum;
        var radius = extent.Length() * 0.5f;
        if (extent.X < 0 || extent.Y < 0 || extent.Z < 0 || !float.IsFinite(radius) || radius <= 0.000001f)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S1E2140C40D94FFE8"), originalMessageText => new InvalidOperationException(originalMessageText));

        var budget = new Budget(cancellation);
        budget.Check();
        var center = model.Minimum + extent * 0.5f;
        var scale = Math.Min(width, height) * 0.46f / radius * Math.Clamp(zoom, 0.05f, 32);
        var offset = new Vector2(width * (0.5f + Math.Clamp(pan.X, -32, 32)),
            height * (0.5f + Math.Clamp(pan.Y, -32, 32)));
        // Explicit row-vector matrix composition keeps roll about the viewer's
        // forward axis after yaw/pitch, rather than the furniture's local axis.
        var rotation = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(pitch)
            * Matrix4x4.CreateRotationZ(-roll);
        var opaque = new List<Triangle>();
        var transparent = new List<Triangle>();
        var textures = new Dictionary<ViewSingleItemFurnitureTexture, Texture>();
        var triangleCount = 0L;
        var vertexCount = 0L;
        foreach (var mesh in model.Meshes)
        {
            budget.Check();
            triangleCount += mesh.Indices.Length / 3;
            if (triangleCount > MaximumTriangles)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SBF8CE5567AF12E45"), originalMessageText => new InvalidOperationException(originalMessageText));
            vertexCount += mesh.Positions.Length;
            if (vertexCount > MaximumVertices)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SAEC8067E2F83AB67"), originalMessageText => new InvalidOperationException(originalMessageText));
            Texture? texture = null;
            if (mesh.Texture is { } sourceTexture)
            {
                if (!textures.TryGetValue(sourceTexture, out texture))
                {
                    texture = PrepareTexture(sourceTexture, budget);
                    textures.Add(sourceTexture, texture);
                }
            }

            var vertices = new Vertex[mesh.Positions.Length];
            for (var i = 0; i < vertices.Length; ++i)
            {
                if ((i & 1023) == 0) budget.Check();
                var position = Vector3.Transform(mesh.Positions[i] - center, rotation);
                var normal = i < mesh.Normals.Length ? Vector3.TransformNormal(mesh.Normals[i], rotation) : Vector3.Zero;
                var uv = i < mesh.Uvs.Length ? mesh.Uvs[i] : Vector2.Zero;
                var color = i < mesh.Colors.Length && Finite(mesh.Colors[i])
                    ? Vector4.Clamp(mesh.Colors[i], Vector4.Zero, Vector4.One) : Vector4.One;
                vertices[i] = new Vertex(new Vector3(offset.X + position.X * scale,
                    offset.Y - position.Y * scale, position.Z), uv, color, Lighting(normal));
            }
            for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
            {
                if ((i & 1023) == 0) budget.Check();
                var ia = mesh.Indices[i];
                var ib = mesh.Indices[i + 1];
                var ic = mesh.Indices[i + 2];
                if ((uint)ia >= (uint)vertices.Length || (uint)ib >= (uint)vertices.Length || (uint)ic >= (uint)vertices.Length)
                    continue;
                var a = vertices[ia];
                var b = vertices[ib];
                var c = vertices[ic];
                if (!Finite(a.Position) || !Finite(b.Position) || !Finite(c.Position)) continue;
                if (a.Light < 0 || b.Light < 0 || c.Light < 0)
                {
                    var ab = b.Position - a.Position;
                    var ac = c.Position - a.Position;
                    ab = new Vector3(ab.X / scale, -ab.Y / scale, ab.Z);
                    ac = new Vector3(ac.X / scale, -ac.Y / scale, ac.Z);
                    var light = Math.Max(0.48f, Lighting(Vector3.Cross(ab, ac)));
                    if (a.Light < 0) a = a with { Light = light };
                    if (b.Light < 0) b = b with { Light = light };
                    if (c.Light < 0) c = c with { Light = light };
                }
                var triangle = new Triangle(a, b, c, texture, (a.Position.Z + b.Position.Z + c.Position.Z) / 3);
                var translucent = texture?.Translucent == true || a.Color.W < 0.999f || b.Color.W < 0.999f || c.Color.W < 0.999f;
                (translucent ? transparent : opaque).Add(triangle);
            }
        }
        if (opaque.Count + transparent.Count == 0)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S83F72E5178C2BB65"), originalMessageText => new InvalidOperationException(originalMessageText));

        var pixels = new byte[checked(width * height * 4)];
        var depth = new float[width * height];
        Array.Fill(depth, float.NegativeInfinity);
        foreach (var triangle in opaque) Draw(triangle, pixels, depth, width, height, false, budget);
        // Opaque geometry writes depth first. Translucent geometry is blended
        // back-to-front without depth writes; intersecting transparent surfaces
        // can still differ from the game's material-specific rendering.
        transparent.Sort(static (a, b) => a.Depth.CompareTo(b.Depth));
        budget.Check();
        foreach (var triangle in transparent) Draw(triangle, pixels, depth, width, height, true, budget);
        budget.Check();
        return pixels;
    }

    private static Texture PrepareTexture(ViewSingleItemFurnitureTexture source, Budget budget)
    {
        var required = (long)source.Width * source.Height * 4;
        if (source.Width <= 0 || source.Height <= 0 || required > source.Bgra.LongLength || required > 128L * 1024 * 1024)
            throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S6B4470F0EF2DB2AD"), originalMessageText => new InvalidOperationException(originalMessageText));
        var translucent = false;
        for (var i = 3; i < required; i += 4)
        {
            if ((i & 65535) == 3) budget.Check();
            // Ignore the near-opaque quantization fringe. Binary masks render
            // in the opaque pass with an alpha cutoff instead of depth holes.
            if (source.Bgra[i] > 0 && source.Bgra[i] < 250)
            {
                translucent = true;
                break;
            }
        }
        return new Texture(source.Width, source.Height, source.Bgra, translucent);
    }

    private static void Draw(Triangle triangle, byte[] pixels, float[] depth, int width, int height,
        bool translucent, Budget budget)
    {
        budget.Check();
        var a = triangle.A;
        var b = triangle.B;
        var c = triangle.C;
        var area = Edge(a.Position, b.Position, c.Position.X, c.Position.Y);
        if (!float.IsFinite(area) || MathF.Abs(area) < 0.00001f) return;
        if (area < 0)
        {
            (b, c) = (c, b);
            area = -area;
        }
        var left = Math.Max(0, MathF.Floor(Math.Min(a.Position.X, Math.Min(b.Position.X, c.Position.X))));
        var top = Math.Max(0, MathF.Floor(Math.Min(a.Position.Y, Math.Min(b.Position.Y, c.Position.Y))));
        var right = Math.Min(width - 1, MathF.Ceiling(Math.Max(a.Position.X, Math.Max(b.Position.X, c.Position.X))));
        var bottom = Math.Min(height - 1, MathF.Ceiling(Math.Max(a.Position.Y, Math.Max(b.Position.Y, c.Position.Y))));
        if (left > right || top > bottom) return;
        var minX = (int)left;
        var minY = (int)top;
        var maxX = (int)right;
        var maxY = (int)bottom;
        budget.AddPixels((long)(maxX - minX + 1) * (maxY - minY + 1));
        var inverseArea = 1 / area;
        var edgeA = TopLeft(b.Position, c.Position);
        var edgeB = TopLeft(c.Position, a.Position);
        var edgeC = TopLeft(a.Position, b.Position);
        for (var y = minY; y <= maxY; ++y)
        {
            if ((y & 31) == 0) budget.Check();
            for (var x = minX; x <= maxX; ++x)
            {
                var wa = Edge(b.Position, c.Position, x + 0.5f, y + 0.5f);
                var wb = Edge(c.Position, a.Position, x + 0.5f, y + 0.5f);
                var wc = Edge(a.Position, b.Position, x + 0.5f, y + 0.5f);
                if (!Inside(wa, edgeA) || !Inside(wb, edgeB) || !Inside(wc, edgeC)) continue;
                wa *= inverseArea;
                wb *= inverseArea;
                wc *= inverseArea;
                var z = a.Position.Z * wa + b.Position.Z * wb + c.Position.Z * wc;
                var target = y * width + x;
                if (!float.IsFinite(z) || z < depth[target]) continue;
                var uv = a.UV * wa + b.UV * wb + c.UV * wc;
                var color = a.Color * wa + b.Color * wb + c.Color * wc;
                if (triangle.Texture is { } texture) color *= Sample(texture, uv);
                if (color.W <= (translucent ? 0.01f : 0.5f)) continue;
                var light = a.Light * wa + b.Light * wb + c.Light * wc;
                color.X *= light;
                color.Y *= light;
                color.Z *= light;
                if (translucent) Blend(pixels, target * 4, color);
                else
                {
                    depth[target] = z;
                    pixels[target * 4] = Channel(color.Z);
                    pixels[target * 4 + 1] = Channel(color.Y);
                    pixels[target * 4 + 2] = Channel(color.X);
                    pixels[target * 4 + 3] = 255;
                }
            }
        }
    }

    private static Vector4 Sample(Texture texture, Vector2 uv)
    {
        if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y)) return Vector4.One;
        var u = (uv.X - MathF.Floor(uv.X)) * texture.Width - 0.5f;
        var v = (uv.Y - MathF.Floor(uv.Y)) * texture.Height - 0.5f;
        var x = (int)MathF.Floor(u);
        var y = (int)MathF.Floor(v);
        var fx = u - x;
        var fy = v - y;
        var x0 = (x + texture.Width) % texture.Width;
        var y0 = (y + texture.Height) % texture.Height;
        var x1 = (x0 + 1) % texture.Width;
        var y1 = (y0 + 1) % texture.Height;
        return Vector4.Lerp(Vector4.Lerp(Texel(texture, x0, y0), Texel(texture, x1, y0), fx),
            Vector4.Lerp(Texel(texture, x0, y1), Texel(texture, x1, y1), fx), fy);
    }

    private static Vector4 Texel(Texture texture, int x, int y)
    {
        var i = (y * texture.Width + x) * 4;
        return new Vector4(texture.Bgra[i + 2], texture.Bgra[i + 1], texture.Bgra[i], texture.Bgra[i + 3]) / 255;
    }

    // The upload and ImGui use straight alpha. Divide the accumulated color by
    // output alpha so transparent edge pixels are not multiplied twice later.
    private static void Blend(byte[] pixels, int index, Vector4 source)
    {
        var sourceAlpha = Math.Clamp(source.W, 0, 1);
        var remainingAlpha = pixels[index + 3] / 255f * (1 - sourceAlpha);
        var alpha = sourceAlpha + remainingAlpha;
        if (alpha <= 0) return;
        pixels[index] = Channel((source.Z * sourceAlpha + pixels[index] / 255f * remainingAlpha) / alpha);
        pixels[index + 1] = Channel((source.Y * sourceAlpha + pixels[index + 1] / 255f * remainingAlpha) / alpha);
        pixels[index + 2] = Channel((source.X * sourceAlpha + pixels[index + 2] / 255f * remainingAlpha) / alpha);
        pixels[index + 3] = Channel(alpha);
    }

    private static float Lighting(Vector3 normal)
    {
        var length = normal.LengthSquared();
        if (!float.IsFinite(length) || length < 0.00000001f) return -1;
        // Housing contains intentionally double-sided planes. Absolute diffuse
        // lighting keeps their reverse faces visible when the user rotates them.
        return 0.48f + 0.52f * Math.Clamp(MathF.Abs(Vector3.Dot(normal / MathF.Sqrt(length), LightDirection)), 0, 1);
    }

    private static byte Channel(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
    private static float Edge(Vector3 a, Vector3 b, float x, float y) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
    private static bool TopLeft(Vector3 a, Vector3 b) => b.Y < a.Y || (b.Y == a.Y && b.X > a.X);
    private static bool Inside(float edge, bool inclusive) => edge > 0 || (edge == 0 && inclusive);
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
