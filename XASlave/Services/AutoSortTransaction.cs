using System;
using System.Collections.Generic;
using System.Linq;

namespace XASlave.Services;

// The caller owns the fifteen-second deadline and cancellation. No native sort
// is cancelled or replayed here; End only retires observation.
internal sealed class AutoSortTransaction
{
    private readonly IAutoSortAdapter adapter;
    private readonly long generation;
    private readonly Action requireRequest;
    private readonly AutoSortSnapshot initial;
    private readonly Dictionary<nint, AutoSortTarget> expected = new();
    private readonly Dictionary<nint, int> passes = new();
    private readonly HashSet<nint> started = new();
    private long lastSequence;
    private bool previousTerminal, submitted, cleanupAttempted;
    internal int StartedCount => started.Count;
    internal int ObservedStartCount => initial.Targets.Count(target => adapter.Started(target.Address).Count > 0);
    internal bool Complete { get; private set; }

    internal AutoSortTransaction(IAutoSortAdapter adapter, long generation, Action requireRequest)
    {
        this.adapter = adapter; this.generation = generation; this.requireRequest = requireRequest;
        requireRequest();
        initial = adapter.Capture();
        if (initial.Targets.Length != 13 || initial.Targets.Select(t => t.Address).Distinct().Count() != 13
            || initial.Targets.Count(t => t.Group == AutoSortGroup.Armoury) != 12
            || initial.Targets.Count(t => t.Group == AutoSortGroup.Inventory) != 1
            || initial.Targets.Any(t => t.PassIndex >= 0))
            throw new InvalidOperationException("Item sorters are unavailable or have active game sort commands.");
        foreach (var target in initial.Targets) expected.Add(target.Address, target);
        lastSequence = initial.FrameworkSequence;
        adapter.Begin(initial, generation);
    }

    private AutoSortSnapshot Current()
    {
        requireRequest(); adapter.Require(generation);
        var current = adapter.Capture();
        if (!initial.SameWorld(current)) throw new InvalidOperationException("The character, zone, containers or physical inventory changed.");
        return current;
    }

    private void Check(AutoSortSnapshot current)
    {
        foreach (var target in current.Targets)
        {
            var prior = expected[target.Address];
            var observed = adapter.Started(target.Address);
            if (!started.Contains(target.Address))
            {
                if (observed.Count != 0 || target.PassIndex >= 0 || !target.SameRules(prior))
                    throw new InvalidOperationException("Pending sort conditions changed outside this request.");
                continue;
            }
            if (observed.Count != 1 || observed.Started == null)
                throw new InvalidOperationException("An owned sort lost its command admission.");
            if (target.Terminal)
            {
                if (target.RulesBegin != prior.RulesBegin || target.RulesCapacity != prior.RulesCapacity)
                    throw new InvalidOperationException("Completed rule storage changed.");
                passes[target.Address] = int.MaxValue;
            }
            else
            {
                if (!target.SameRules(prior) || target.PassIndex < passes[target.Address] || target.PassIndex >= target.Rules.Length)
                    throw new InvalidOperationException("An active native sort changed rules or moved backwards.");
                passes[target.Address] = target.PassIndex;
            }
        }
    }

    internal void Submit(AutoSortItemsSettings settings)
    {
        if (submitted) throw new InvalidOperationException("Item-sort commands cannot be replayed.");
        submitted = true;
        foreach (var command in AutoSortItemsPlan.Create(settings))
        {
            var before = Current(); Check(before);
            var group = before.Targets.Where(t => t.Group == command.Group).ToArray();
            var rules = adapter.ResolveRules(command.ExpectedRules);
            if (command.Execute && group.Any(t => t.PassIndex >= 0 || !t.Rules.SequenceEqual(rules)))
                throw new InvalidOperationException("The complete owned conditions are required before native execution.");
            // No asynchronous step occurs between this final check, submission and read-back.
            requireRequest(); adapter.Require(generation);
            var sent = adapter.Send(command.Text, command.Group, command.Execute, generation);
            var after = Current();
            foreach (var target in after.Targets)
            {
                if (target.Group != command.Group) continue;
                var observed = adapter.Started(target.Address);
                if (command.Execute)
                {
                    if (observed.Count != 1 || observed.Started is not { } admission || !admission.SameIdentity(target)
                        || admission.PassIndex < 0 || admission.PassIndex >= rules.Length || !admission.Rules.SequenceEqual(rules))
                        throw new InvalidOperationException("Native execution did not activate the submitted conditions.");
                    started.Add(target.Address); expected[target.Address] = admission; passes[target.Address] = admission.PassIndex;
                }
                else
                {
                    if (observed.Count != 0 || target.PassIndex >= 0 || !target.Rules.SequenceEqual(rules))
                        throw new InvalidOperationException($"Native condition installation did not match the complete expected prefix after '{command.Text}': target {target.Address}, pass {target.PassIndex}, observed starts {observed.Count}, expected {rules.Length} rules, actual {target.Rules.Length} rules.");
                    expected[target.Address] = target;
                }
            }
            Check(after);
            if (!sent) throw new InvalidOperationException("The item-sort command was not submitted successfully.");
        }
        if (started.Count != 13) throw new InvalidOperationException("Not every requested native sort started.");
    }

    internal bool Poll()
    {
        if (Complete) return true;
        if (!submitted || started.Count != 13) throw new InvalidOperationException("Item sorting has not been fully submitted.");
        var current = Current(); Check(current);
        if (current.FrameworkSequence < lastSequence) throw new InvalidOperationException("The framework observation sequence moved backwards.");
        if (current.FrameworkSequence == lastSequence) return false;
        var terminal = current.Targets.All(t => t.Terminal);
        Complete = terminal && previousTerminal && current.FrameworkSequence == lastSequence + 1;
        previousTerminal = terminal; lastSequence = current.FrameworkSequence;
        return Complete;
    }

    // Cleanup is available only while the request is still valid. A cancelled or
    // retired request sends nothing, including clear. A failed read-back leaves
    // that group's unknown pending state intact.
    internal void TryCleanup()
    {
        if (cleanupAttempted) return;
        cleanupAttempted = true;
        foreach (var group in new[] { AutoSortGroup.Armoury, AutoSortGroup.Inventory })
        {
            try
            {
                var current = Current();
                var targets = current.Targets.Where(t => t.Group == group).ToArray();
                if (targets.Length != (group == AutoSortGroup.Armoury ? 12 : 1)
                    || targets.Any(t => started.Contains(t.Address) || adapter.Started(t.Address).Count != 0
                        || t.PassIndex >= 0 || !t.SameRules(expected[t.Address]))
                    || targets.All(t => t.Rules.Length == 0)) continue;
                var fresh = Current();
                if (targets.Any(t => !t.SameRules(fresh.Target(t.Address)) || fresh.Target(t.Address).PassIndex >= 0
                    || adapter.Started(t.Address).Count != 0)) continue;
                requireRequest(); adapter.Require(generation);
                var sent = adapter.Send(AutoSortItemsPlan.Clear(group), group, false, generation);
                var after = Current();
                if (!sent || targets.Any(t => !after.Target(t.Address).IdleEmpty || adapter.Started(t.Address).Count != 0)) continue;
                foreach (var target in targets) expected[target.Address] = after.Target(target.Address);
            }
            catch (Exception) { /* Unknown state stays native-owned; never retry this group. */ }
        }
    }
}
