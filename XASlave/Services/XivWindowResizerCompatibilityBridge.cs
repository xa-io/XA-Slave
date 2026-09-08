using System;
using System.Reflection;

namespace XASlave.Services;

internal readonly record struct XivWindowResizerBridgeResult(
    bool Success,
    bool ReusedExistingHandle,
    string Message);

/// <summary>
/// Primes XIVWindowResizer's private cached game-window handle so its first explicit resize
/// does not need the native FINAL FANTASY XIV title. The bridge is deliberately reflection-
/// guarded because XIVWindowResizer does not expose this state through IPC or a public API.
/// </summary>
internal static class XivWindowResizerCompatibilityBridge
{
    private const string WindowSizeHelperMemberName = "_windowSizeHelper";
    private const string GameWindowHandleFieldName = "_gameWindowHandle";
    private const BindingFlags InstanceBindings = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static XivWindowResizerBridgeResult PrimeWindowHandle(
        object? pluginInstance,
        IntPtr gameWindowHandle,
        Func<IntPtr, bool> isUsableCurrentProcessWindow)
    {
        ArgumentNullException.ThrowIfNull(isUsableCurrentProcessWindow);

        if (pluginInstance == null)
        {
            return Failure("XA could not resolve the loaded XIVWindowResizer instance.");
        }

        if (gameWindowHandle == IntPtr.Zero || !isUsableCurrentProcessWindow(gameWindowHandle))
        {
            return Failure("XA could not resolve a valid current-process FFXIV window handle.");
        }

        try
        {
            var helper = GetMemberValueInHierarchy(pluginInstance, WindowSizeHelperMemberName);
            if (helper == null)
            {
                return Failure("XIVWindowResizer's window-size helper was not found; its private layout may have changed.");
            }

            var handleField = FindFieldInHierarchy(helper.GetType(), GameWindowHandleFieldName);
            if (handleField == null || handleField.FieldType != typeof(IntPtr))
            {
                return Failure("XIVWindowResizer's cached window-handle field was not found; its private layout may have changed.");
            }

            var cachedHandle = handleField.GetValue(helper) is IntPtr value ? value : IntPtr.Zero;
            if (cachedHandle != IntPtr.Zero && isUsableCurrentProcessWindow(cachedHandle))
            {
                return new XivWindowResizerBridgeResult(
                    true,
                    true,
                    "XIVWindowResizer already has a valid current-process window handle; the custom title remains active.");
            }

            handleField.SetValue(helper, gameWindowHandle);
            var verifiedHandle = handleField.GetValue(helper) is IntPtr verified ? verified : IntPtr.Zero;
            if (verifiedHandle != gameWindowHandle)
            {
                return Failure("XA wrote XIVWindowResizer's window-handle cache, but readback did not match.");
            }

            return new XivWindowResizerBridgeResult(
                true,
                false,
                "XA primed XIVWindowResizer's current-process window handle; the custom title remains active.");
        }
        catch (Exception ex)
        {
            return Failure($"XIVWindowResizer handle preparation failed: {ex.Message}");
        }
    }

    private static XivWindowResizerBridgeResult Failure(string message)
        => new(false, false, message);

    private static object? GetMemberValueInHierarchy(object source, string memberName)
    {
        for (var type = source.GetType(); type != null; type = type.BaseType)
        {
            var property = type.GetProperty(memberName, InstanceBindings | BindingFlags.DeclaredOnly);
            if (property != null)
                return property.GetValue(source);

            var field = type.GetField(memberName, InstanceBindings | BindingFlags.DeclaredOnly)
                ?? type.GetField($"<{memberName}>k__BackingField", InstanceBindings | BindingFlags.DeclaredOnly);
            if (field != null)
                return field.GetValue(source);
        }

        return null;
    }

    private static FieldInfo? FindFieldInHierarchy(Type type, string fieldName)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var field = current.GetField(fieldName, InstanceBindings | BindingFlags.DeclaredOnly);
            if (field != null)
                return field;
        }

        return null;
    }
}
