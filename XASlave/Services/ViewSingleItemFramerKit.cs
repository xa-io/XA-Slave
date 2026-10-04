using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

internal sealed record ViewSingleItemKitArtwork(string Label, uint IconId);

// A kit can grant several plate and portrait components. These are ordinary
// game textures; resolving them never opens an editor or changes unlocks.
internal static class ViewSingleItemFramerKit
{
    internal static IReadOnlyList<ViewSingleItemKitArtwork> Load(uint kitId)
    {
        var conditions = new HashSet<uint>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<BannerCondition>())
            if (row.UnlockType1 == 9 && row.UnlockCriteria1[0].RowId == kitId)
                conditions.Add(row.RowId);

        var artwork = new List<ViewSingleItemKitArtwork>();
        // Kit zero is Paladin's kit, not an empty or unavailable reference.
        foreach (var row in Plugin.DataManager.GetExcelSheet<CharaCardBase>())
            if (conditions.Contains(row.UnlockCondition.RowId))
                Add(artwork, "Plate Base", row.Name.ExtractText(), row.Image);
        foreach (var row in Plugin.DataManager.GetExcelSheet<CharaCardHeader>())
            if (conditions.Contains(row.UnlockCondition.RowId))
            {
                Add(artwork, "Plate Top Border", row.Name.ExtractText(), row.TopImage);
                Add(artwork, "Plate Bottom Border", row.Name.ExtractText(), row.BottomImage);
            }
        foreach (var row in Plugin.DataManager.GetExcelSheet<CharaCardDecoration>())
            if (conditions.Contains(row.UnlockCondition.RowId))
                Add(artwork, DecorationLabel(row.Component), row.Name.ExtractText(), row.Image);
        // Image is the full component artwork; Icon is only its menu thumbnail.
        foreach (var row in Plugin.DataManager.GetExcelSheet<BannerBg>())
            if (conditions.Contains(row.UnlockCondition.RowId))
                Add(artwork, "Portrait Background", row.Name.ExtractText(), row.Image);
        foreach (var row in Plugin.DataManager.GetExcelSheet<BannerFrame>())
            if (conditions.Contains(row.UnlockCondition.RowId))
                Add(artwork, "Portrait Frame", row.Name.ExtractText(), row.Image);
        foreach (var row in Plugin.DataManager.GetExcelSheet<BannerDecoration>())
            if (conditions.Contains(row.UnlockCondition.RowId))
                Add(artwork, "Portrait Accent", row.Name.ExtractText(), row.Image);
        return artwork.ToArray();
    }

    private static void Add(List<ViewSingleItemKitArtwork> artwork, string component, string name, int iconId)
    {
        if (iconId <= 0) return;
        artwork.Add(new(string.IsNullOrWhiteSpace(name) ? component : component + ": " + name, (uint)iconId));
    }

    private static string DecorationLabel(byte component) => component switch
    {
        1 => "Plate Backing", 2 => "Plate Pattern Overlay", 3 => "Plate Portrait Frame",
        4 => "Plate Frame", 5 => "Plate Accent", _ => "Plate Decoration",
    };
}
