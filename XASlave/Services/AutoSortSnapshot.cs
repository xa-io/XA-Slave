using System;
using System.Collections.Generic;
using System.Linq;

namespace XASlave.Services;

internal readonly record struct AutoSortNativeRule(nint Function, bool Descending);
internal readonly record struct AutoSortContainer(uint Type, nint Address, nint Items, int Size);
internal readonly record struct AutoSortPhysicalItem(uint Container, ushort Slot, uint Quantity, InventoryShuttleItemState State);
internal readonly record struct AutoSortSession(ulong Character, uint Territory, uint Map, nint Module);

internal sealed record AutoSortTarget(nint Address, AutoSortGroup Group, uint Container, int ItemsPerPage,
    nint ItemsBegin, nint ItemsEnd, nint ItemsCapacity, nint RulesBegin, nint RulesCapacity,
    int PassIndex, int Percent, AutoSortNativeRule[] Rules)
{
    internal bool Terminal => PassIndex == -1 && Percent == 100 && Rules.Length == 0;
    internal bool IdleEmpty => PassIndex < 0 && Rules.Length == 0;
    internal bool SameIdentity(AutoSortTarget other) => Address == other.Address && Group == other.Group && Container == other.Container
        && ItemsPerPage == other.ItemsPerPage && ItemsBegin == other.ItemsBegin && ItemsEnd == other.ItemsEnd && ItemsCapacity == other.ItemsCapacity;
    internal bool SameRules(AutoSortTarget other) => RulesBegin == other.RulesBegin && RulesCapacity == other.RulesCapacity && Rules.SequenceEqual(other.Rules);
}

internal sealed record AutoSortSnapshot(AutoSortSession Session, long FrameworkSequence, AutoSortTarget[] Targets,
    AutoSortContainer[] Containers, AutoSortPhysicalItem[] PhysicalItems)
{
    internal bool SameWorld(AutoSortSnapshot other) => Session == other.Session && Containers.SequenceEqual(other.Containers)
        && PhysicalItems.SequenceEqual(other.PhysicalItems) && Targets.Length == other.Targets.Length
        && Targets.Zip(other.Targets).All(pair => pair.First.SameIdentity(pair.Second));
    internal AutoSortTarget Target(nint address) => Targets.Single(target => target.Address == address);
}

internal readonly record struct AutoSortStartResult(int Count, AutoSortTarget? Started);

internal interface IAutoSortAdapter : IDisposable
{
    AutoSortSnapshot Capture();
    AutoSortNativeRule[] ResolveRules(IReadOnlyList<AutoSortRule> rules);
    void Begin(AutoSortSnapshot initial, long generation);
    void Require(long generation);
    AutoSortStartResult Started(nint target);
    bool Send(string command, AutoSortGroup group, bool execute, long generation);
    void End();
}
