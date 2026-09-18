using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace XASlave.Services;

internal sealed class AutoSortNativeAdapter : IAutoSortAdapter
{
    private readonly AutoSortNativeBinding binding;
    private readonly AutoSortWorld world;
    private readonly AutoSortStartObserver observer;
    private bool disposed;

    internal AutoSortNativeAdapter(Func<long> frameworkSequence)
    {
        NearbyZoneNativeBinding.RequireFramework();
        ValidateTokens();
        binding = new(); world = new(binding, frameworkSequence);
        observer = new(binding, world.ReadTarget);
    }

    private static void ValidateTokens()
    {
        var commands = Plugin.DataManager.GetExcelSheet<TextCommand>();
        var parameters = Plugin.DataManager.GetExcelSheet<TextCommandParam>();
        if (commands == null || parameters == null || !commands.TryGetRow(210, out var command))
            throw new InvalidOperationException("Canonical item-sort command data is unavailable.");
        AutoSortItemsPlan.ValidateTokens(command.Command.ExtractText(),
            id => parameters.TryGetRow(id, out var parameter) ? parameter.Param.ExtractText() : null);
    }

    public AutoSortSnapshot Capture()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return world.Capture();
    }

    public AutoSortNativeRule[] ResolveRules(IReadOnlyList<AutoSortRule> rules)
        => rules.Select(rule => new AutoSortNativeRule(binding.Function(rule.Function), rule.Descending)).ToArray();

    public void Begin(AutoSortSnapshot initial, long generation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        observer.Begin(initial, generation);
    }

    public void Require(long generation)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        observer.Require(generation);
    }

    public AutoSortStartResult Started(nint target) => observer.Started(target);

    public bool Send(string command, AutoSortGroup group, bool execute, long generation)
    {
        Require(generation); ValidateTokens();
        using (observer.Enter(group, execute, generation))
            return ChatHelper.TrySend(command);
    }

    public void End() => observer.End();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; observer.Dispose();
    }
}
