using System.Numerics;

namespace XASlave.Services;

internal sealed record ViewSingleItemFurnitureAsset(uint ItemId, ushort ModelKey, bool Outdoor);

// Managed snapshots only: these contain no live game, scene, or graphics pointers.
internal sealed record ViewSingleItemFurnitureModel(
    ViewSingleItemFurnitureMesh[] Meshes, Vector3 Minimum, Vector3 Maximum, string Notes)
{
    private sealed record Presentation(global::XASlave.Localization.UiMessage? NotesMessage);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ViewSingleItemFurnitureModel, Presentation> Presentations = new();
    private static readonly Presentation EmptyPresentation = new(null);
    private Presentation Messages => Presentations.TryGetValue(this, out var value) ? value : EmptyPresentation;
    internal global::XASlave.Localization.UiMessage? NotesMessage
    {
        get => Messages.NotesMessage;
        init => Presentations.AddOrUpdate(this, Messages with { NotesMessage = value });
    }
    private ViewSingleItemFurnitureModel(ViewSingleItemFurnitureModel original)
    {
        Meshes = original.Meshes;
        Minimum = original.Minimum;
        Maximum = original.Maximum;
        Notes = original.Notes;
        if (Presentations.TryGetValue(original, out var presentation)) Presentations.Add(this, presentation);
    }
    internal global::XASlave.Localization.UiMessage NotesPresentation => global::XASlave.Localization.UiMessage.Capture(NotesMessage, Notes);
}

internal sealed record ViewSingleItemFurnitureMesh(
    Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, Vector4[] Colors,
    int[] Indices, ViewSingleItemFurnitureTexture? Texture);

internal sealed record ViewSingleItemFurnitureTexture(int Width, int Height, byte[] Bgra);
