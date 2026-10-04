using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Hooking;

namespace XASlave.Services;

// Diagnostic reads only. This never follows jumps, changes hooks or admits code.
internal static class ViewSingleItemRendererDiagnostics
{
    internal static string DescribeSavedEntry(byte[]? saved)
    {
        if (saved == null) return "Saved entry: none.";
        return $"Saved entry: length={saved.Length}, prefix={Convert.ToHexString(saved.AsSpan(0, Math.Min(16, saved.Length)))}.";
    }

    internal static string DescribeBytes(int rva, byte[] expected, byte[] actual)
    {
        var common = Math.Min(expected.Length, actual.Length);
        var first = 0;
        while (first < common && expected[first] == actual[first]) first++;
        if (first == common && expected.Length == actual.Length) return "No byte difference.";
        var start = Math.Max(0, first - 8);
        var expectedCount = Math.Min(16, expected.Length - start);
        var actualCount = Math.Min(16, actual.Length - start);
        return $"First difference at RVA 0x{rva + first:X} (+0x{first:X}); "
            + $"window RVA 0x{rva + start:X}: expected {Convert.ToHexString(expected.AsSpan(start, expectedCount))}, "
            + $"actual {Convert.ToHexString(actual.AsSpan(start, actualCount))}.";
    }

    internal static string DescribeOwners(nint entry, int length)
    {
        try
        {
            var manager = typeof(IDalamudHook).Assembly.GetType("Dalamud.Hooking.Internal.HookManager");
            if (manager == null) return "Dalamud hook registry unavailable.";
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic;
            var owners = new List<string>();
            if (manager.GetProperty("TrackedHooks", flags | BindingFlags.Static)?.GetValue(null) is IDictionary tracked)
            {
                foreach (var value in tracked.Values)
                {
                    if (value == null) continue;
                    try
                    {
                        var type = value.GetType();
                        if (type.GetProperty("Hook", flags | BindingFlags.Instance)?.GetValue(value) is not IDalamudHook hook
                            || hook.IsDisposed) continue;
                        var offset = (long)hook.Address - (long)entry;
                        if (offset < 0 || offset >= length) continue;
                        var assembly = type.GetProperty("Assembly", flags | BindingFlags.Instance)?.GetValue(value) as Assembly;
                        owners.Add($"{assembly?.GetName().Name ?? "unknown assembly"} "
                            + $"({hook.BackendName}, enabled={hook.IsEnabled}, +0x{offset:X})");
                        if (owners.Count == 4) break;
                    }
                    catch (ObjectDisposedException) { }
                }
            }
            else return "Dalamud hook registry unavailable.";
            var registered = manager.GetProperty("Unhookers", flags | BindingFlags.Static)?.GetValue(null) is IDictionary unhookers
                && unhookers.Contains(entry);
            return "Registered hooks in renderer: " + (owners.Count == 0 ? "none" : string.Join(", ", owners))
                + $"; entry has prior Dalamud hook registration={registered}.";
        }
        catch (Exception error)
        {
            return "Dalamud hook registry unavailable (" + error.GetType().Name + ").";
        }
    }
}
