using System.Numerics;

namespace XASlave.Services;

internal sealed record ViewSingleItemFurnitureAsset(uint ItemId, ushort ModelKey, bool Outdoor);

// Managed snapshots only: these contain no live game, scene, or graphics pointers.
internal sealed record ViewSingleItemFurnitureModel(
    ViewSingleItemFurnitureMesh[] Meshes, Vector3 Minimum, Vector3 Maximum, string Notes);

internal sealed record ViewSingleItemFurnitureMesh(
    Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, Vector4[] Colors,
    int[] Indices, ViewSingleItemFurnitureTexture? Texture);

internal sealed record ViewSingleItemFurnitureTexture(int Width, int Height, byte[] Bgra);
