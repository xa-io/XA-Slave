using System;
using System.Collections;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;

namespace XASlave.Services;

internal static class SimpleTweaksDutyFinderIntegration
{
    private const string PluginName = "SimpleTweaksPlugin";
    private const string PluginType = "SimpleTweaksPlugin.SimpleTweaksPlugin";
    private const string TweakKey = "UiAdjustments@ImprovedDutyFinderSettings";
    private enum State { Unknown, NotLoaded, Disabled, Enabled }
    private static State state;
    private static long nextCheck;
    private static bool pending;
    private static string result = string.Empty;

    internal static void DrawSettings()
    {
        if (!pending && Environment.TickCount64 >= nextCheck)
        {
            var current = ReadState(out _, out _);
            if (current != state) result = string.Empty;
            state = current;
            nextCheck = Environment.TickCount64 + 1000;
        }

        var message = state switch
        {
            State.Enabled => "Conflict detected: Simple Tweaks > Improved Duty Finder Settings is enabled. Disable one of these features to avoid Duty Finder crashes.",
            State.Disabled => "Simple Tweaks is loaded; Improved Duty Finder Settings is disabled.",
            State.NotLoaded => "Simple Tweaks is not loaded.",
            _ => "Unable to verify Simple Tweaks' Improved Duty Finder Settings. Check it manually before using both features.",
        };
        ImGui.PushStyleColor(ImGuiCol.Text, state is State.Enabled or State.Unknown
            ? new Vector4(1f, 0.65f, 0.25f, 1f) : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
        try { ImGui.TextWrapped(message); }
        finally { ImGui.PopStyleColor(); }

        if (state == State.Enabled)
        {
            ImGui.BeginDisabled(pending);
            try
            {
                if (global::XASlave.Localization.UiNative.Button(global::XASlave.Localization.UiLocale.Native("SC050B7223E69A9A4")))
                {
                    pending = true;
                    result = "Disabling Simple Tweaks' Improved Duty Finder Settings...";
                    try { _ = Plugin.Framework.RunOnTick(Disable); }
                    catch (Exception ex) { Fail(ex); pending = false; }
                }
            }
            finally { ImGui.EndDisabled(); }
        }
        if (!string.IsNullOrEmpty(result)) ImGui.TextWrapped(result);
    }

    private static State ReadState(out object? instance, out object? tweak)
    {
        instance = null;
        tweak = null;
        try
        {
            if (!Plugin.PluginInterface.InstalledPlugins.Any(p => p.InternalName == PluginName && p.IsLoaded))
                return State.NotLoaded;
            // Never use an old assembly's static singleton after a plugin reload.
            instance = AutoRetainerUiReflectionService.TryGetLoadedPluginInstance(PluginName, PluginType);
            if (instance == null || instance.GetType().FullName != PluginType) return State.Unknown;
            var lookup = instance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(m => m.Name == "GetTweakById" && m.GetParameters().Length == 2
                    && m.GetParameters()[0].ParameterType == typeof(string));
            tweak = lookup?.Invoke(instance, new object?[] { TweakKey, null });
            if (tweak == null || ReadBool(tweak, "IsDisposed") != false || ReadBool(tweak, "Ready") != true
                || tweak.GetType().GetProperty("Key")?.GetValue(tweak) as string != TweakKey)
                return State.Unknown;
            return ReadBool(tweak, "Enabled") switch { true => State.Enabled, false => State.Disabled, _ => State.Unknown };
        }
        catch { return State.Unknown; }
    }

    private static bool? ReadBool(object value, string name)
        => value.GetType().GetProperty(name)?.GetValue(value) as bool?;

    private static void Disable()
    {
        try
        {
            // Re-resolve at execution time; the provider may have unloaded since the UI check.
            var before = ReadState(out var instance, out _);
            if (before != State.Enabled || instance == null)
            {
                result = before == State.Disabled ? "The Simple Tweaks setting is already disabled."
                    : "Simple Tweaks state changed or could not be verified. No disable request was sent.";
                return;
            }
            var handler = instance.GetType().GetMethod("OnConfigCommandHandler", new[] { typeof(object), typeof(object) })
                ?? throw new MissingMethodException("Simple Tweaks' disable command handler is unavailable.");
            // The provider owns InternalDisable, native UI cleanup, EnabledTweaks removal and Save.
            handler.Invoke(instance, new object[] { "/tweaks", "disable " + TweakKey });
            var after = ReadState(out var current, out _);
            if (!ReferenceEquals(instance, current) || after != State.Disabled)
                throw new InvalidOperationException("The setting did not report disabled after the request.");
            var config = instance.GetType().GetProperty("PluginConfig")?.GetValue(instance);
            var entries = config?.GetType().GetField("EnabledTweaks")?.GetValue(config)
                ?? config?.GetType().GetProperty("EnabledTweaks")?.GetValue(config);
            if (entries is not IEnumerable list || list.Cast<object>().Any(x => x as string == TweakKey))
                throw new InvalidOperationException("Could not confirm removal from Simple Tweaks' enabled settings.");
            result = "Disabled Improved Duty Finder Settings through Simple Tweaks' normal save path. Other tweaks were left unchanged.";
        }
        catch (Exception ex) { Fail(ex); }
        finally
        {
            pending = false;
            state = ReadState(out _, out _);
            nextCheck = 0;
        }
    }

    private static void Fail(Exception ex)
    {
        Plugin.Log.Warning(ex, "[XASlave] Simple Tweaks duty finder disable could not be confirmed.");
        result = "Could not confirm disabling and saving the Simple Tweaks setting. Check it in Simple Tweaks; no automatic retry will run.";
    }
}
