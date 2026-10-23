using System;
using System.Reflection;

namespace XASlave.Services;

internal readonly record struct XivWindowResizerBridgeResult(
    bool Success,
    bool ReusedExistingHandle,
    global::XASlave.Localization.UiMessage MessageToken)
{
    public string Message => MessageToken.Original;
}

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
            return Failure(global::XASlave.Localization.UiMessage.Key("SEC10ECED5DD9088B"));
        }

        if (gameWindowHandle == IntPtr.Zero || !isUsableCurrentProcessWindow(gameWindowHandle))
        {
            return Failure(global::XASlave.Localization.UiMessage.Key("SF7E4CCBCC973C966"));
        }

        try
        {
            var helper = GetMemberValueInHierarchy(pluginInstance, WindowSizeHelperMemberName);
            if (helper == null)
            {
                return Failure(global::XASlave.Localization.UiMessage.Key("S51664605DBF862CF"));
            }

            var handleField = FindFieldInHierarchy(helper.GetType(), GameWindowHandleFieldName);
            if (handleField == null || handleField.FieldType != typeof(IntPtr))
            {
                return Failure(global::XASlave.Localization.UiMessage.Key("SC98837ACC8DFBAAB"));
            }

            var cachedHandle = handleField.GetValue(helper) is IntPtr value ? value : IntPtr.Zero;
            if (cachedHandle != IntPtr.Zero && isUsableCurrentProcessWindow(cachedHandle))
            {
                return new XivWindowResizerBridgeResult(
                    true,
                    true,
                    global::XASlave.Localization.UiMessage.Key("SD1AC575EE02B31E6"));
            }

            handleField.SetValue(helper, gameWindowHandle);
            var verifiedHandle = handleField.GetValue(helper) is IntPtr verified ? verified : IntPtr.Zero;
            if (verifiedHandle != gameWindowHandle)
            {
                return Failure(global::XASlave.Localization.UiMessage.Key("S7593CAE1F242941F"));
            }

            return new XivWindowResizerBridgeResult(
                true,
                false,
                global::XASlave.Localization.UiMessage.Key("SF10F088DC484DEFA"));
        }
        catch (Exception ex)
        {
            return Failure(global::XASlave.Localization.UiMessage.Format("S223B32E3FF56A524", ex.Message));
        }
    }

    private static XivWindowResizerBridgeResult Failure(global::XASlave.Localization.UiMessage message)
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
