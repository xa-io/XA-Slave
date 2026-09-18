using System;
using System.Collections.Generic;

namespace XASlave.Services;

// Observe synchronous /itemsort command admission through current sorter state.
// No hook, host-private reflection or trampoline lifetime is needed. The owning
// transaction checks rules, world identity and subsequent completion snapshots.
internal sealed class AutoSortStartObserver : IDisposable
{
    private readonly AutoSortNativeBinding binding;
    private readonly Func<nint, AutoSortTarget> readTarget;
    private readonly Dictionary<nint, AutoSortStartResult> starts = new();
    private readonly Dictionary<nint, AutoSortGroup> targets = new();
    private long generation;
    private bool active, disposed;
    private Scope? currentScope;

    internal AutoSortStartObserver(AutoSortNativeBinding binding, Func<nint, AutoSortTarget> readTarget)
    { this.binding = binding; this.readTarget = readTarget; }

    internal void Begin(AutoSortSnapshot initial, long owner)
    {
        NearbyZoneNativeBinding.RequireFramework();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (active) throw new InvalidOperationException("The item-sort observer already owns a request.");
        if (initial.Targets.Length != 13) throw new InvalidOperationException("All thirteen item sorters are required.");
        binding.Validate();
        targets.Clear(); starts.Clear(); generation = owner;
        foreach (var target in initial.Targets)
        {
            targets.Add(target.Address, target.Group);
            starts.Add(target.Address, new(0, null));
        }
        active = true;
    }

    internal IDisposable Enter(AutoSortGroup group, bool execute, long owner)
    {
        Require(owner);
        if (currentScope != null) throw new InvalidOperationException("Item-sort command scopes cannot be nested.");
        var scope = new Scope(this, owner);
        if (execute)
        {
            foreach (var (address, targetGroup) in targets)
            {
                if (targetGroup != group) continue;
                var before = readTarget(address);
                Require(owner);
                if (starts[address].Count != 0 || before.PassIndex >= 0 || before.Rules.Length == 0)
                    throw new InvalidOperationException("The requested sorter is already active or has no pending conditions.");
                scope.Expected.Add(before);
            }
        }
        currentScope = scope;
        return scope;
    }

    internal AutoSortStartResult Started(nint target)
        => starts.TryGetValue(target, out var result) ? result : new(0, null);

    internal void Require(long owner)
    {
        NearbyZoneNativeBinding.RequireFramework();
        if (!active || disposed || generation != owner)
            throw new InvalidOperationException("Item-sort observation is no longer owned.");
        binding.Validate();
    }

    private sealed class Scope(AutoSortStartObserver owner, long generation) : IDisposable
    {
        internal List<AutoSortTarget> Expected { get; } = new();
        public void Dispose()
        {
            if (owner.currentScope != this) return;
            owner.currentScope = null;
            owner.Require(generation);
            foreach (var before in Expected)
            {
                var after = owner.readTarget(before.Address);
                owner.Require(generation);
                if (!before.SameIdentity(after) || !before.SameRules(after)
                    || after.PassIndex < 0 || after.PassIndex >= after.Rules.Length)
                    throw new InvalidOperationException("The sort command did not activate its expected conditions.");
                owner.starts[before.Address] = new(1, after);
            }
        }
    }

    internal void End()
    {
        active = false; generation++; currentScope = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; End();
    }
}
