using System;
using Dalamud.Hooking;

namespace XASlave.Services;

internal static class HookCleanup
{
    public static void DisposeAndClear<T>(ref Hook<T>? hook)
        where T : Delegate
    {
        var hookToDispose = hook;
        hook = null;
        hookToDispose?.Dispose();
    }
}
