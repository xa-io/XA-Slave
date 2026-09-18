using System;
using System.Collections.Generic;

namespace XASlave.Services;

public sealed record AutoSortItemsSettings(bool SortOnEnable = true, bool SortAfterZoneChange = true,
    int ArmouryId = 0, int ArmouryItemLevel = 0, int ArmouryCategory = 0,
    int InventoryHq = 0, int InventoryId = 0, int InventoryItemLevel = 0, int InventoryCategory = 0,
    int InventoryTabs = 0, bool SendChat = false, bool SendNotification = true)
{
    public AutoSortItemsSettings Normalize() => this with
    {
        ArmouryId = Direction(ArmouryId), ArmouryItemLevel = Direction(ArmouryItemLevel), ArmouryCategory = Direction(ArmouryCategory),
        InventoryHq = Direction(InventoryHq), InventoryId = Direction(InventoryId), InventoryItemLevel = Direction(InventoryItemLevel),
        InventoryCategory = Direction(InventoryCategory), InventoryTabs = Direction(InventoryTabs),
    };

    private static int Direction(int value) => value == 1 ? 1 : 0;
}

internal enum AutoSortGroup { Armoury, Inventory }
internal enum AutoSortFunction { NullSentinel, IdPrimary, IdSecondary, Category, ItemLevel, Hq, Tab }
internal readonly record struct AutoSortRule(AutoSortFunction Function, bool Descending);
internal sealed record AutoSortCommand(AutoSortGroup Group, string Text, IReadOnlyList<AutoSortRule> ExpectedRules, bool Execute);

// Command order is source parity. Later native passes have priority; earlier
// passes are stable tie-breakers. Neither pointer vectors nor item slots are written.
internal static class AutoSortItemsPlan
{
    internal static void ValidateTokens(string command, Func<uint, string?> parameter)
    {
        if (command != "/itemsort") throw new InvalidOperationException("The canonical item-sort command is unavailable.");
        RequireToken(parameter, 250, "execute"); RequireToken(parameter, 252, "condition"); RequireToken(parameter, 254, "clear");
        RequireToken(parameter, 256, "Inventory"); RequireToken(parameter, 258, "ArmouryChest");
        RequireToken(parameter, 262, "category"); RequireToken(parameter, 266, "itemlevel"); RequireToken(parameter, 270, "id");
        RequireToken(parameter, 272, "tab"); RequireToken(parameter, 276, "hq"); RequireToken(parameter, 280, "asc"); RequireToken(parameter, 282, "des");
    }

    private static void RequireToken(Func<uint, string?> parameter, uint row, string expected)
    {
        if (!string.Equals(parameter(row), expected, StringComparison.Ordinal))
            throw new InvalidOperationException("The current client has incompatible canonical item-sort parameters.");
    }

    internal static IReadOnlyList<AutoSortCommand> Create(AutoSortItemsSettings requested)
    {
        var settings = requested.Normalize();
        var commands = new List<AutoSortCommand>();
        var armoury = new List<AutoSortRule>();
        commands.Add(new(AutoSortGroup.Armoury, Clear(AutoSortGroup.Armoury), armoury.ToArray(), false));
        Add(commands, armoury, AutoSortGroup.Armoury, "id", settings.ArmouryId);
        Add(commands, armoury, AutoSortGroup.Armoury, "itemlevel", settings.ArmouryItemLevel);
        Add(commands, armoury, AutoSortGroup.Armoury, "category", settings.ArmouryCategory);
        Execute(commands, armoury, AutoSortGroup.Armoury);
        var inventory = new List<AutoSortRule>();
        commands.Add(new(AutoSortGroup.Inventory, Clear(AutoSortGroup.Inventory), inventory.ToArray(), false));
        Add(commands, inventory, AutoSortGroup.Inventory, "hq", settings.InventoryHq);
        Add(commands, inventory, AutoSortGroup.Inventory, "id", settings.InventoryId);
        Add(commands, inventory, AutoSortGroup.Inventory, "itemlevel", settings.InventoryItemLevel);
        Add(commands, inventory, AutoSortGroup.Inventory, "category", settings.InventoryCategory);
        if (settings.InventoryTabs == 0) Add(commands, inventory, AutoSortGroup.Inventory, "tab", 1);
        Execute(commands, inventory, AutoSortGroup.Inventory);
        return commands.AsReadOnly();
    }

    private static void Add(List<AutoSortCommand> commands, List<AutoSortRule> rules, AutoSortGroup group, string criterion, int direction)
    {
        if (direction != 0 && direction != 1) throw new ArgumentException("Unknown sort direction.");
        // The native command seeds function 1 with direction=true, independent
        // of the requested criterion direction (dispatcher 0x937692/0x93772F).
        if (rules.Count == 0) rules.Add(new(AutoSortFunction.NullSentinel, true));
        var descending = direction == 0;
        if (criterion == "id")
        {
            rules.Add(new(AutoSortFunction.IdPrimary, descending));
            rules.Add(new(AutoSortFunction.IdSecondary, descending));
            rules.Add(new(AutoSortFunction.Category, descending));
        }
        else if (criterion == "category") rules.Add(new(AutoSortFunction.Category, descending));
        else if (criterion == "itemlevel") rules.Add(new(AutoSortFunction.ItemLevel, descending));
        else if (criterion == "hq") rules.Add(new(AutoSortFunction.Hq, descending));
        else if (criterion == "tab" && group == AutoSortGroup.Inventory) rules.Add(new(AutoSortFunction.Tab, false));
        else throw new ArgumentException("Unknown item-sort criterion or target.");
        if (rules.Count > 16) throw new InvalidOperationException("The native item-sort rule capacity was exceeded.");
        var suffix = criterion == "tab" ? "" : direction == 0 ? " des" : " asc";
        commands.Add(new(group, "/itemsort condition " + Target(group) + " " + criterion + suffix, rules.ToArray(), false));
    }

    private static void Execute(List<AutoSortCommand> commands, List<AutoSortRule> rules, AutoSortGroup group)
        => commands.Add(new(group, "/itemsort execute " + Target(group), rules.ToArray(), true));

    internal static string Clear(AutoSortGroup group) => "/itemsort clear " + Target(group);

    private static string Target(AutoSortGroup group)
    {
        if (group == AutoSortGroup.Armoury) return "ArmouryChest";
        if (group == AutoSortGroup.Inventory) return "Inventory";
        throw new ArgumentException("Item sorting only supports normal inventory and the current armoury chest.");
    }
}
