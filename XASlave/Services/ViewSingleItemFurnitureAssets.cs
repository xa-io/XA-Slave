using System;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Data.Parsing;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

// This path reads game assets into owned managed data. It never creates a native
// housing object, modifies a territory, or registers an object with Scene.World.
internal static class ViewSingleItemFurnitureAssets
{
    private static readonly object CatalogLock = new();
    private static FrozenDictionary<uint, ViewSingleItemFurnitureAsset>? catalog;

    internal static bool TryDescribe(uint itemId, [NotNullWhen(true)] out ViewSingleItemFurnitureAsset? asset)
    {
        lock (CatalogLock)
        {
            // Publish only a complete index. A sheet-load exception can be retried.
            catalog ??= BuildCatalog();
            return catalog.TryGetValue(itemId, out asset);
        }
    }

    private static FrozenDictionary<uint, ViewSingleItemFurnitureAsset> BuildCatalog()
    {
        var entries = new Dictionary<uint, ViewSingleItemFurnitureAsset>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<HousingFurniture>())
            if (row.Item.RowId != 0 && row.ModelKey != 0)
                entries.TryAdd(row.Item.RowId, new(row.Item.RowId, row.ModelKey, false));
        foreach (var row in Plugin.DataManager.GetExcelSheet<HousingYardObject>())
            if (row.Item.RowId != 0 && row.ModelKey != 0)
                entries.TryAdd(row.Item.RowId, new(row.Item.RowId, row.ModelKey, true));
        return entries.ToFrozenDictionary();
    }

    internal static ViewSingleItemFurnitureModel Load(ViewSingleItemFurnitureAsset asset, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var number = asset.ModelKey.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
        var folder = asset.Outdoor ? "outdoor" : "indoor";
        var prefix = asset.Outdoor ? "gar" : "fun";
        var path = $"bgcommon/hou/{folder}/general/{number}/asset/{prefix}_b0_m{number}.sgb";
        return new Loader(cancellation).Load(path);
    }

    private sealed class Loader(CancellationToken cancellation)
    {
        private const int MaximumDepth = 16;
        private const int MaximumObjects = 512;
        private const int MaximumMeshes = 2048;
        private const int MaximumTriangles = 200_000;
        private const int MaximumVertices = 900_000;
        private const int MaximumFileBytes = 64 * 1024 * 1024;
        private const long MaximumAssetBytes = 256L * 1024 * 1024;
        private const long MaximumTextureBytes = 64L * 1024 * 1024;
        private const int MaximumTextureDimension = 8192;
        private const int DecodedTextureDimension = 2048;
        private readonly List<ViewSingleItemFurnitureMesh> meshes = [];
        private readonly HashSet<string> sceneStack = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ViewSingleItemFurnitureTexture?> textures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ViewSingleItemFurnitureTexture?> materials = new(StringComparer.Ordinal);
        private readonly HashSet<string> notes = new(StringComparer.Ordinal);
        private Vector3 minimum = new(float.MaxValue);
        private Vector3 maximum = new(float.MinValue);
        private int objectCount;
        private int triangleCount;
        private int vertexCount;
        private long assetBytes;
        private long textureBytes;

        internal ViewSingleItemFurnitureModel Load(string path)
        {
            VisitScene(path, Matrix4x4.Identity, 0);
            cancellation.ThrowIfCancellationRequested();
            if (meshes.Count == 0)
                throw new InvalidDataException("This furnishing has no supported visible geometry in its default static state.");
            notes.Add("Static furniture preview; lighting and materials are approximate. Dyes, particles and interactive animations are not shown.");
            return new(meshes.ToArray(), minimum, maximum, string.Join(" ", notes));
        }

        private T? Read<T>(string path) where T : FileResource
        {
            cancellation.ThrowIfCancellationRequested();
            var file = Plugin.DataManager.GetFile<T>(path);
            cancellation.ThrowIfCancellationRequested();
            if (file == null) return null;
            if (file.Data.Length > MaximumFileBytes || (assetBytes += file.Data.Length) > MaximumAssetBytes)
                throw new InvalidDataException("This furnishing exceeds the preview asset memory limit.");
            return file;
        }

        private void VisitScene(string sourcePath, Matrix4x4 parent, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth > MaximumDepth || ++objectCount > MaximumObjects)
                throw new InvalidDataException("This furnishing exceeds the preview scene complexity limit.");
            var path = GamePath(sourcePath, ".sgb");
            if (!sceneStack.Add(path))
                throw new InvalidDataException("This furnishing contains a recursive scene reference.");
            try
            {
                // The installed Lumina BG reader predates the high collision-mask
                // fields: its IsVisible reads 0x48 instead of the current 0x50.
                // Read the bounded serialized layout, never a native pointer.
                var file = Read<FileResource>(path) ?? throw new InvalidDataException("The furnishing scene could not be loaded: " + path);
                var data = file.Data;
                if (UInt32(data, 0) != 0x31424753 || UInt32(data, 4) > data.Length)
                    throw new InvalidDataException("The furnishing scene has an unsupported file header.");
                var sectionCount = Count(data, 8, 16);
                var section = 12;
                var foundScene = false;
                for (var s = 0; s < sectionCount; s++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var sectionSize = Count(data, section + 4, MaximumFileBytes);
                    if (sectionSize < 8) throw new InvalidDataException("A furnishing section has an invalid size.");
                    Slice(data, section, sectionSize);
                    if (UInt32(data, section) == 0x314E4353)
                    {
                        foundScene = true;
                        VisitSceneHeader(data, section + 8, parent, depth);
                    }
                    section = checked(section + sectionSize);
                }
                if (!foundScene) throw new InvalidDataException("The furnishing has no supported scene section.");
            }
            finally { sceneStack.Remove(path); }
        }

        private void VisitSceneHeader(byte[] data, int scene, Matrix4x4 parent, int depth)
        {
            Slice(data, scene, 0x40);
            var groups = Relative(data, scene, 0);
            var groupCount = Count(data, scene + 4, MaximumObjects);
            if (Count(data, scene + 0x18, MaximumObjects) != 0)
                notes.Add("External scene layer resources are omitted.");
            // FileSceneHeader contains a contiguous array of 0x10-byte group
            // headers, not a pointer table. Each group's layers are offset tables.
            for (var g = 0; g < groupCount; g++)
            {
                var group = checked(groups + g * 0x10);
                var layers = Relative(data, group, 8);
                var layerCount = Count(data, group + 12, MaximumObjects);
                for (var l = 0; l < layerCount; l++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var layer = Relative(data, layers, l * 4);
                    Slice(data, layer, 0x34);
                    var filterOffset = Int32(data, layer + 0x14);
                    // Match/NoMatch layers require a territory layer-set key. Never
                    // combine these mutually exclusive variants without that key.
                    if (UInt32(data, layer + 0x18) != 0 || data[layer + 0x1C] != 0 ||
                        (filterOffset != 0 && UInt32(data, Relative(data, layer, 0x14)) != 0))
                    {
                        notes.Add("Conditional furniture variants are omitted; only unconditional default layers are shown.");
                        continue;
                    }
                    if (Int32(data, layer + 0x28) != 0 || Int32(data, layer + 0x30) != 0)
                        notes.Add("Animated object-set overrides are not applied.");
                    var instances = Relative(data, layer, 8);
                    var instanceCount = Count(data, layer + 12, MaximumObjects);
                    for (var i = 0; i < instanceCount; i++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (++objectCount > MaximumObjects)
                            throw new InvalidDataException("This furnishing exceeds the preview scene complexity limit.");
                        var instance = Relative(data, instances, i * 4);
                        var kind = UInt32(data, instance);
                        if (kind is not (1 or 6))
                        {
                            if (kind == 4) notes.Add("Furniture particle effects are omitted.");
                            continue;
                        }
                        var transform = Transform(Slice(data, instance + 12, 36)) * parent;
                        var assetPath = ReadString(data, checked((uint)Relative(data, instance, 0x30)));
                        if (kind == 1)
                        {
                            Slice(data, instance, 0x5C);
                            var visible = data[instance + 0x50];
                            if (visible > 1) throw new InvalidDataException("A furnishing uses an unknown initial visibility state.");
                            if (visible == 1 && assetPath.Length != 0) LoadModel(assetPath, transform);
                        }
                        else
                        {
                            if (Count(data, instance + 0x3C, MaximumObjects) != 0)
                            {
                                notes.Add("Sub-scenes with undecoded visibility overrides are omitted.");
                                continue;
                            }
                            if (assetPath.Length != 0) VisitScene(assetPath, transform, depth + 1);
                        }
                    }
                }
            }
        }

        private void LoadModel(string sourcePath, Matrix4x4 transform)
        {
            var path = GamePath(sourcePath, ".mdl");
            var model = Read<MdlFile>(path) ?? throw new InvalidDataException("A furnishing model could not be loaded: " + path);
            if (model.FileHeader.LodCount == 0 || model.Lods.Length == 0)
                throw new InvalidDataException("A furnishing model has no geometry level.");
            if (!Matrix4x4.Invert(transform, out var inverse))
                throw new InvalidDataException("A furnishing model has an invalid transform.");
            var normalTransform = Matrix4x4.Transpose(inverse);
            var lod = model.Lods[0];
            var meshIndices = new HashSet<int>();
            AddRange(lod.MeshIndex, lod.MeshCount);
            // Glass is visible geometry; shadow, fog and material-change copies are
            // separate passes/alternatives and must not duplicate the base model.
            if (model.ExtraLods is { Length: > 0 })
                AddRange(model.ExtraLods[0].GlassMeshIndex, model.ExtraLods[0].GlassMeshCount);
            if (lod.WaterMeshCount != 0 || model.ModelHeader.ShapeCount != 0)
                notes.Add("Water surfaces and shape animations are not applied.");
            foreach (var meshIndex in meshIndices)
            {
                cancellation.ThrowIfCancellationRequested();
                LoadMesh(model, path, meshIndex, transform, normalTransform);
            }

            void AddRange(int start, int count)
            {
                if (start < 0 || count < 0 || start > model.Meshes.Length - count)
                    throw new InvalidDataException("A furnishing model contains an invalid mesh range.");
                for (var i = start; i < start + count; i++) meshIndices.Add(i);
            }
        }

        private void LoadMesh(MdlFile model, string path, int meshIndex, Matrix4x4 transform, Matrix4x4 normalTransform)
        {
            var mesh = model.Meshes[meshIndex];
            if (mesh.IndexCount == 0 || mesh.VertexCount == 0) return;
            if (mesh.IndexCount % 3 != 0 || mesh.IndexCount > MaximumTriangles * 3 ||
                meshes.Count >= MaximumMeshes || triangleCount + mesh.IndexCount / 3 > MaximumTriangles ||
                vertexCount + mesh.VertexCount > MaximumVertices || meshIndex >= model.VertexDeclarations.Length)
                throw new InvalidDataException("This furnishing exceeds the preview geometry limit or contains invalid geometry.");
            var positions = new Vector3[mesh.VertexCount];
            var normals = new Vector3[mesh.VertexCount];
            var uvs = new Vector2[mesh.VertexCount];
            var colors = new Vector4[mesh.VertexCount];
            var indices = new int[(int)mesh.IndexCount];
            var elements = model.VertexDeclarations[meshIndex].VertexElements;
            var hasNormal = false;
            for (var vertex = 0; vertex < positions.Length; vertex++)
            {
                if ((vertex & 255) == 0) cancellation.ThrowIfCancellationRequested();
                var hasPosition = false;
                colors[vertex] = Vector4.One;
                foreach (var element in elements)
                {
                    if (element.Stream == byte.MaxValue) break;
                    if (element.UsageIndex != 0 || element.Usage is not (0 or 3 or 4 or 7)) continue;
                    if (element.Stream >= mesh.VertexStreamCount || element.Stream >= mesh.VertexBufferOffset.Length ||
                        element.Stream >= mesh.VertexBufferStride.Length)
                        throw new InvalidDataException("A furnishing vertex references an invalid data stream.");
                    var elementSize = ElementSize(element.Type);
                    var stride = mesh.VertexBufferStride[element.Stream];
                    if (element.Offset + elementSize > stride)
                        throw new InvalidDataException("A furnishing vertex exceeds its data stride.");
                    var streamOffset = (long)mesh.VertexBufferOffset[element.Stream] + (long)vertex * stride + element.Offset;
                    if (streamOffset < 0 || streamOffset > model.FileHeader.VertexBufferSize[0] - (long)elementSize)
                        throw new InvalidDataException("A furnishing vertex exceeds its model buffer.");
                    var offset = checked((long)model.FileHeader.VertexOffset[0] + streamOffset);
                    var value = ReadElement(Slice(model.Data, offset, elementSize), element.Type);
                    if (!Finite(value)) throw new InvalidDataException("A furnishing vertex contains non-finite values.");
                    switch (element.Usage)
                    {
                        case 0:
                            positions[vertex] = Vector3.Transform(new(value.X, value.Y, value.Z), transform);
                            hasPosition = true;
                            break;
                        case 3:
                            normals[vertex] = Vector3.TransformNormal(new(value.X, value.Y, value.Z), normalTransform);
                            hasNormal = true;
                            break;
                        case 4: uvs[vertex] = new(value.X, value.Y); break;
                        case 7: colors[vertex] = Vector4.Clamp(value, Vector4.Zero, Vector4.One); break;
                    }
                }
                if (!hasPosition || !Finite(positions[vertex]) || !Finite(normals[vertex]))
                    throw new InvalidDataException("A furnishing vertex could not be transformed.");
                if (positions[vertex].LengthSquared() > 1e12f)
                    throw new InvalidDataException("A furnishing model exceeds the preview coordinate range.");
            }
            var indexBytes = checked(indices.Length * 2);
            var indexStart = (long)mesh.StartIndex * 2;
            if (indexStart > model.FileHeader.IndexBufferSize[0] - (long)indexBytes)
                throw new InvalidDataException("A furnishing mesh exceeds its index buffer.");
            var indexData = Slice(model.Data, model.FileHeader.IndexOffset[0] + indexStart, indexBytes);
            for (var i = 0; i < indices.Length; i++)
            {
                if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(indexData.Slice(i * 2, 2));
                if (indices[i] >= positions.Length)
                    throw new InvalidDataException("A furnishing mesh references a missing vertex.");
            }
            if (transform.GetDeterminant() < 0)
                for (var i = 0; i < indices.Length; i += 3)
                    (indices[i + 1], indices[i + 2]) = (indices[i + 2], indices[i + 1]);
            if (!hasNormal)
                for (var i = 0; i < indices.Length; i += 3)
                {
                    if ((i & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                    var a = indices[i]; var b = indices[i + 1]; var c = indices[i + 2];
                    var normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                    normals[a] += normal; normals[b] += normal; normals[c] += normal;
                }
            for (var i = 0; i < positions.Length; i++)
            {
                if ((i & 255) == 0) cancellation.ThrowIfCancellationRequested();
                var length = normals[i].LengthSquared();
                normals[i] = float.IsFinite(length) && length > 1e-12f ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
            }
            foreach (var index in indices)
            {
                minimum = Vector3.Min(minimum, positions[index]);
                maximum = Vector3.Max(maximum, positions[index]);
            }
            ViewSingleItemFurnitureTexture? texture = null;
            if (mesh.MaterialIndex < model.MaterialNameOffsets.Length)
            {
                var materialPath = ReadString(model.Strings, model.MaterialNameOffsets[mesh.MaterialIndex]);
                texture = LoadMaterial(ResolveMaterial(path, materialPath));
            }
            else notes.Add("A material is unavailable; its mesh uses vertex colours.");
            meshes.Add(new(positions, normals, uvs, colors, indices, texture));
            triangleCount += indices.Length / 3;
            vertexCount += positions.Length;
        }

        private ViewSingleItemFurnitureTexture? LoadMaterial(string path)
        {
            cancellation.ThrowIfCancellationRequested();
            if (materials.TryGetValue(path, out var cached)) return cached;
            var file = Read<MtrlFile>(path);
            if (file == null)
            {
                notes.Add("A material is unavailable; its mesh uses vertex colours.");
                materials[path] = null;
                return null;
            }
            // Match actual colour samplers, never a normal/specular/mask texture.
            var selected = -1;
            var selectedRank = int.MaxValue;
            foreach (var sampler in file.Samplers)
            {
                var rank = (TextureUsage)sampler.SamplerId switch
                {
                    TextureUsage.SamplerDiffuse => 0,
                    TextureUsage.SamplerColorMap0 => 1,
                    TextureUsage.Sampler0 => 2,
                    TextureUsage.Sampler => 3,
                    TextureUsage.SamplerColorMap1 => 4,
                    TextureUsage.Sampler1 => 5,
                    _ => int.MaxValue,
                };
                if (rank < selectedRank && sampler.TextureIndex < file.TextureOffsets.Length)
                {
                    selectedRank = rank;
                    selected = sampler.TextureIndex;
                }
            }
            ViewSingleItemFurnitureTexture? texture = null;
            if (selected >= 0)
                texture = LoadTexture(GamePath(ReadString(file.Strings, file.TextureOffsets[selected].Offset), ".tex"));
            else notes.Add("Materials without a base-colour texture use vertex colours.");
            materials[path] = texture;
            return texture;
        }

        private ViewSingleItemFurnitureTexture? LoadTexture(string path)
        {
            if (textures.TryGetValue(path, out var cached)) return cached;
            var file = Read<TexFile>(path);
            if (file == null)
            {
                notes.Add("A base-colour texture is unavailable; its mesh uses vertex colours.");
                textures[path] = null;
                return null;
            }
            var width = (int)file.Header.Width;
            var height = (int)file.Header.Height;
            if (width <= 0 || height <= 0 || width > MaximumTextureDimension || height > MaximumTextureDimension)
                throw new InvalidDataException("A furnishing texture exceeds the preview dimensions limit.");
            var mip = 0;
            while ((width > DecodedTextureDimension || height > DecodedTextureDimension) && mip + 1 < file.Header.MipCount)
            {
                mip++;
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }
            var size = checked((long)width * height * 4);
            if (size > MaximumTextureBytes - textureBytes)
                throw new InvalidDataException("This furnishing exceeds the preview texture memory limit.");
            cancellation.ThrowIfCancellationRequested();
            var pixels = file.TextureBuffer.Filter(mip: mip, z: 0, format: TexFile.TextureFormat.B8G8R8A8).RawData;
            cancellation.ThrowIfCancellationRequested();
            if (pixels.LongLength != size)
                throw new InvalidDataException("A furnishing texture decoded to an unexpected size.");
            textureBytes += size;
            var texture = new ViewSingleItemFurnitureTexture(width, height, pixels);
            textures.Add(path, texture);
            return texture;
        }

        private static Matrix4x4 Transform(ReadOnlySpan<byte> data)
        {
            var scale = new Vector3(Single(data, 24), Single(data, 28), Single(data, 32));
            var rotation = new Vector3(Single(data, 12), Single(data, 16), Single(data, 20));
            var translation = new Vector3(Single(data, 0), Single(data, 4), Single(data, 8));
            if (!Finite(scale) || !Finite(rotation) || !Finite(translation))
                throw new InvalidDataException("A furnishing contains a non-finite scene transform.");
            // FileLayerGroupTransform stores XYZ Euler radians. This matches the
            // client math's qZ * qY * qX quaternion order for row-vector matrices.
            return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationX(rotation.X) *
                Matrix4x4.CreateRotationY(rotation.Y) * Matrix4x4.CreateRotationZ(rotation.Z) *
                Matrix4x4.CreateTranslation(translation);
        }

        private static string ResolveMaterial(string modelPath, string materialPath)
        {
            if (!materialPath.StartsWith('/')) return GamePath(materialPath, ".mtrl");
            var marker = modelPath.LastIndexOf("/model/", StringComparison.Ordinal);
            if (marker < 0) marker = modelPath.LastIndexOf("/bgparts/", StringComparison.Ordinal);
            if (marker < 0) throw new InvalidDataException("A furnishing material has an unsupported relative path.");
            return GamePath(modelPath[..marker] + "/material" + materialPath, ".mtrl");
        }

        private static string GamePath(string path, string extension)
        {
            path = path.Replace('\\', '/').ToLowerInvariant();
            if (path.Length is 0 or > 512 || path.StartsWith('/') || path.Contains(':') || path.Contains('\0') ||
                path.Contains("..", StringComparison.Ordinal) || !path.EndsWith(extension, StringComparison.Ordinal))
                throw new InvalidDataException("A furnishing asset has an invalid game path.");
            return path;
        }

        private static string ReadString(byte[] bytes, uint offset)
        {
            if (offset >= bytes.Length) throw new InvalidDataException("A furnishing string offset is invalid.");
            var start = checked((int)offset);
            var end = Array.IndexOf(bytes, (byte)0, start);
            if (end < start || end - start > 512) throw new InvalidDataException("A furnishing asset path is invalid.");
            return Encoding.UTF8.GetString(bytes, start, end - start);
        }

        private static ReadOnlySpan<byte> Slice(byte[] data, long offset, int length)
        {
            if (offset < 0 || length < 0 || offset > data.LongLength - length)
                throw new InvalidDataException("A furnishing data range is outside its asset.");
            return data.AsSpan(checked((int)offset), length);
        }

        private static uint UInt32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(data, offset, 4));
        private static int Int32(byte[] data, int offset) => BinaryPrimitives.ReadInt32LittleEndian(Slice(data, offset, 4));
        private static int Count(byte[] data, int offset, int maximum)
        {
            var count = Int32(data, offset);
            if (count < 0 || count > maximum) throw new InvalidDataException("A furnishing asset exceeds the preview structure limit.");
            return count;
        }
        private static int Relative(byte[] data, int origin, int field)
        {
            var offset = checked(origin + Int32(data, checked(origin + field)));
            Slice(data, offset, 1);
            return offset;
        }

        private static int ElementSize(byte type) => type switch
        {
            0 => 4, 1 => 8, 2 => 12, 3 => 16,
            5 or 6 or 8 or 9 or 11 or 13 => 4,
            7 or 10 or 12 or 14 => 8,
            _ => throw new InvalidDataException("A furnishing uses an unsupported vertex format."),
        };

        private static Vector4 ReadElement(ReadOnlySpan<byte> bytes, byte type)
        {
            switch (type)
            {
                case 0: return new(Single(bytes, 0), 0, 0, 1);
                case 1: return new(Single(bytes, 0), Single(bytes, 4), 0, 1);
                case 2: return new(Single(bytes, 0), Single(bytes, 4), Single(bytes, 8), 1);
                case 3: return new(Single(bytes, 0), Single(bytes, 4), Single(bytes, 8), Single(bytes, 12));
                case 5: return new(bytes[0], bytes[1], bytes[2], bytes[3]);
                case 8: return new Vector4(bytes[0], bytes[1], bytes[2], bytes[3]) / 255f;
                case 13: return new(Half(bytes, 0), Half(bytes, 2), 0, 1);
                case 14: return new(Half(bytes, 0), Half(bytes, 2), Half(bytes, 4), Half(bytes, 6));
                default:
                    var count = type is 6 or 9 or 11 ? 2 : 4;
                    Span<float> values = stackalloc float[4] { 0, 0, 0, 1 };
                    for (var i = 0; i < count; i++)
                        values[i] = type is 11 or 12
                            ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(i * 2, 2)) / 65535f
                            : type is 9 or 10
                                ? Math.Max(-1f, BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2)) / 32767f)
                                : BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2));
                    return new(values[0], values[1], values[2], values[3]);
            }
        }

        private static float Single(ReadOnlySpan<byte> bytes, int offset)
            => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4)));
        private static float Half(ReadOnlySpan<byte> bytes, int offset)
            => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2)));
        private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
        private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    }
}
