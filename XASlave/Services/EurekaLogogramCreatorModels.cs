using System;
using System.Collections.Generic;

namespace XASlave.Services;

[Serializable]
public sealed class FavoritePlate
{
    public string Name { get; set; } = string.Empty;
    public uint? AstralActionId { get; set; }
    public uint? UmbralActionId { get; set; }
    public List<uint> ActionIds { get; set; } = [];
}

[Serializable]
public sealed class Logogram
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class LogogramItem
{
    public ulong Id { get; set; }
    public List<int> Contents { get; set; } = [];
}

public sealed class LogogramSourceDefinition
{
    public ulong ItemId { get; }
    public string Name { get; }
    public int DefaultGilCost { get; }

    public LogogramSourceDefinition(ulong itemId, string name, int defaultGilCost)
    {
        ItemId = itemId;
        Name = name;
        DefaultGilCost = defaultGilCost;
    }
}

[Serializable]
public sealed class LogosAction
{
    public uint Id { get; set; }
    public uint IconID { get; set; }
    public string? Duration { get; set; }
    public string? Cast { get; set; }
    public string? Recast { get; set; }
    public List<List<Recipe>> Recipes { get; set; } = [];
    public List<uint> Roles { get; set; } = [];
}

[Serializable]
public sealed class PlateActionSelection
{
    public PlateSide Side { get; set; }
    public uint ActionId { get; set; }
    public string ActionName { get; set; } = string.Empty;
    public List<Recipe> Recipe { get; set; } = [];
    public int RecipeIndex { get; set; }
}

[Serializable]
public sealed class PlateQueueRequest
{
    public PlateActionSelection? Astral { get; set; }
    public PlateActionSelection? Umbral { get; set; }
    public string Label { get; set; } = string.Empty;

    public IEnumerable<PlateActionSelection> GetOrderedSelections()
    {
        if (Astral != null)
            yield return Astral;

        if (Umbral != null)
            yield return Umbral;
    }
}

public enum PlateSide
{
    Astral,
    Umbral,
}

[Serializable]
public sealed class Recipe
{
    public int LogogramID { get; set; }
    public int Quantity { get; set; }
}
