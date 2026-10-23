using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;

namespace XASlave.Services;

internal readonly record struct AutoRetainerUiReflectionResult(
    bool Success,
    int CharacterCount,
    string Message)
{
    internal AutoRetainerUiReflectionResult(bool success, int characterCount, global::XASlave.Localization.UiMessage message)
        : this(success, characterCount, AutoRetainerUiMessagePresentation.Store(message)) { }

    internal global::XASlave.Localization.UiMessage MessagePresentation => AutoRetainerUiMessagePresentation.Get(Message);
}

internal enum AutoRetainerRelogTargetStatus
{
    Unknown,
    Found,
    Missing,
}

internal readonly record struct AutoRetainerRelogTargetResult(
    AutoRetainerRelogTargetStatus Status,
    string Message)
{
    internal AutoRetainerRelogTargetResult(AutoRetainerRelogTargetStatus status, global::XASlave.Localization.UiMessage message)
        : this(status, AutoRetainerUiMessagePresentation.Store(message)) { }

    internal global::XASlave.Localization.UiMessage MessagePresentation => AutoRetainerUiMessagePresentation.Get(Message);
}

/// <summary>
/// Reflection bridge for AutoRetainer's character UI and read-only live relog roster diagnostics.
/// AutoRetainer does not publish these UI states through IPC, so the UI actions read live character
/// CIDs and write the matching Dear ImGui child-window state-storage entries.
/// </summary>
internal static class AutoRetainerUiReflectionService
{
    private const string AutoRetainerInternalName = "AutoRetainer";
    private const string AutoRetainerPluginTypeName = "AutoRetainer.AutoRetainer";
    private const string LifestreamInternalName = "Lifestream";
    private const string LifestreamPluginTypeName = "Lifestream.Lifestream";
    private const string AutoRetainerWindowFallbackName = "###AutoRetainer";
    private const string AutoRetainerTabBarId = "tabbar";
    private const BindingFlags InstanceBindings = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticBindings = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static object? filteredUiBuilder;
    private static FieldInfo? filteredUiBuilderDrawField;
    private static Action? originalAutoRetainerWindowSystemDraw;
    private static Action? filteredAutoRetainerWindowSystemDraw;
    private static AutoRetainerCharacterFilterMode characterFilterMode;
    private static HashSet<ulong>? registeredLifestreamFcAddressCids;

    public static AutoRetainerCharacterFilterMode CharacterFilterMode => characterFilterMode;
    public static bool DeployablesAttentionFilterEnabled => characterFilterMode == AutoRetainerCharacterFilterMode.Attention;

    // Read the effective plan (including additions), never the plan selected in AR's editor.
    // GetIMSettings is an extension method, exposed as a static method on Utils at runtime.
    public static string InspectNpcSellSettings() => InspectNpcSellSettingsMessage().Original;

    internal static global::XASlave.Localization.UiMessage InspectNpcSellSettingsMessage()
    {
        try
        {
            var instance = TryGetAutoRetainerPluginInstance();
            if (instance == null) return global::XASlave.Localization.UiMessage.Key("S1A9A138C423FF781");
            var type = instance.GetType();
            var data = type.GetProperty("Data", StaticBindings)?.GetValue(null);
            var utils = type.Assembly.GetType("AutoRetainer.Helpers.Utils");
            var method = utils?.GetMethods(StaticBindings).FirstOrDefault(m =>
                m.Name == "GetIMSettings" && m.GetParameters().Length == 2
                && data != null && m.GetParameters()[0].ParameterType.IsInstanceOfType(data)
                && m.GetParameters()[1].ParameterType == typeof(bool));
            var plan = method?.Invoke(null, new object?[] { data, false });
            if (plan == null) return global::XASlave.Localization.UiMessage.Key("SDABF2D6C0E5E37C4");
            global::XASlave.Localization.UiMessage Value(string name) => GetMemberValue(plan, name)?.ToString() is { } value ? global::XASlave.Localization.UiMessage.Raw(value) : global::XASlave.Localization.UiMessage.Key("SB23A6A8439C0DDE5");
            global::XASlave.Localization.UiMessage Ids(string name) => GetMemberValue(plan, name) is IEnumerable values
                ? global::XASlave.Localization.UiMessage.Raw(string.Join(",", values.Cast<object>().Take(256).Select(v => v.ToString()))) : global::XASlave.Localization.UiMessage.Key("SB23A6A8439C0DDE5");
            return global::XASlave.Localization.UiMessage.Format("S7E4D146EE744FC1D", Value("IMEnableNpcSell"), Value("IMAutoVendorHardStackLimit"), Ids("IMAutoVendorHard"), Ids("IMProtectList"), Ids("IMAutoVendorHardIgnoreStack"));
        }
        catch (Exception ex)
        {
            return global::XASlave.Localization.UiMessage.Format("SF2601F71DE2809CE", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Inspects the same live OfflineData roster used by /ays relog without changing it or dispatching
    /// a command. Found proves only an exact target exists, not that AutoRetainer can log into it now.
    /// </summary>
    public static AutoRetainerRelogTargetResult InspectRelogTarget(string characterNameWorld)
    {
        try
        {
            var pluginInstance = TryGetAutoRetainerPluginInstance();
            if (pluginInstance == null)
                return RelogTargetUnknown(global::XASlave.Localization.UiMessage.Key("S4F0739113FA9DEA9"));

            var config = GetMemberValue(pluginInstance, "config");
            if (config == null)
                return RelogTargetUnknown(global::XASlave.Localization.UiMessage.Key("SB2117390E22CC8CC"));

            return EvaluateRelogTargetRoster(GetMemberValue(config, "OfflineData"), characterNameWorld);
        }
        catch (Exception ex)
        {
            return RelogTargetUnknown(global::XASlave.Localization.UiMessage.Format("S1C667130ECAEC830", ex.GetType().Name));
        }
    }

    internal static AutoRetainerRelogTargetResult EvaluateRelogTargetRoster(object? rawRoster, string characterNameWorld)
    {
        if (string.IsNullOrWhiteSpace(characterNameWorld))
            return RelogTargetUnknown(global::XASlave.Localization.UiMessage.Key("S5EAD45ACE949A5DF"));

        var separator = characterNameWorld.IndexOf('@');
        if (separator <= 0 || separator != characterNameWorld.LastIndexOf('@')
            || separator == characterNameWorld.Length - 1
            || string.IsNullOrWhiteSpace(characterNameWorld[..separator])
            || string.IsNullOrWhiteSpace(characterNameWorld[(separator + 1)..]))
        {
            return RelogTargetUnknown(global::XASlave.Localization.UiMessage.Key("S718C1838268C9825"));
        }

        if (rawRoster is not IList roster)
            return RelogTargetUnknown(global::XASlave.Localization.UiMessage.Key("SEDA92C778D2B012C"));

        try
        {
            var found = false;
            foreach (var character in roster)
            {
                if (character == null
                    || GetMemberValue(character, "Name") is not string name
                    || GetMemberValue(character, "World") is not string world
                    || string.IsNullOrWhiteSpace(name)
                    || string.IsNullOrWhiteSpace(world)
                    || name.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                    || world.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                    || name.Contains('@') || world.Contains('@'))
                {
                    return RelogTargetUnknown(global::XASlave.Localization.UiMessage.Key("S9EC24C729F1F5A48"));
                }

                // AutoRetainer's command handler uses exact, case-sensitive Name@World equality.
                // Scan the entire list so a malformed later row cannot produce a false Missing result.
                found |= string.Equals($"{name}@{world}", characterNameWorld, StringComparison.Ordinal);
            }

            return found
                ? new(AutoRetainerRelogTargetStatus.Found, global::XASlave.Localization.UiMessage.Key("SA392A9B0FC637670"))
                : new(AutoRetainerRelogTargetStatus.Missing, global::XASlave.Localization.UiMessage.Key("S4EAD70158DB1C070"));
        }
        catch (Exception ex)
        {
            return RelogTargetUnknown(global::XASlave.Localization.UiMessage.Format("SF3C5098B448BE9D4", ex.GetType().Name));
        }
    }

    private static AutoRetainerRelogTargetResult RelogTargetUnknown(global::XASlave.Localization.UiMessage message)
        => new(AutoRetainerRelogTargetStatus.Unknown, message);

    public static unsafe AutoRetainerUiReflectionResult SetAllCharacterHeaders(
        AutoRetainerUiSection section,
        bool expanded)
    {
        try
        {
            var pluginInstance = TryGetAutoRetainerPluginInstance();
            if (pluginInstance == null)
                return Failure(global::XASlave.Localization.UiMessage.Key("S4F0739113FA9DEA9"));

            var config = GetMemberValue(pluginInstance, "config");
            if (config == null)
                return Failure(global::XASlave.Localization.UiMessage.Key("SB2117390E22CC8CC"));

            var characterIds = GetCharacterIds(config);
            if (characterIds.Count == 0)
                return Failure(global::XASlave.Localization.UiMessage.Key("SC7810BAEA57F4870"));

            var windowObject = GetMemberValue(pluginInstance, "AutoRetainerWindow");
            var reflectedWindowName = windowObject == null
                ? null
                : GetMemberValue(windowObject, "WindowName")?.ToString();

            var mainWindowName = string.IsNullOrWhiteSpace(reflectedWindowName)
                ? AutoRetainerWindowFallbackName
                : reflectedWindowName;
            var mainWindow = ImGuiP.FindWindowByName(mainWindowName);
            if (mainWindow.IsNull && !string.Equals(mainWindowName, AutoRetainerWindowFallbackName, StringComparison.Ordinal))
                mainWindow = ImGuiP.FindWindowByName(AutoRetainerWindowFallbackName);

            if (mainWindow.IsNull)
                return Failure(global::XASlave.Localization.UiMessage.Key("S923698004892A5DF"));

            var nativeWindowName = Marshal.PtrToStringUTF8((nint)mainWindow.Name);
            if (!string.IsNullOrWhiteSpace(nativeWindowName))
                mainWindowName = nativeWindowName;

            var sectionWindow = FindSectionWindow(mainWindow, mainWindowName, section);
            if (sectionWindow.IsNull)
            {
                var tabName = GetTabName(section);
                return Failure(global::XASlave.Localization.UiMessage.Format("S2C0C2B581A3E7B50", tabName));
            }

            var storage = new ImGuiStoragePtr(&sectionWindow.Handle->StateStorage);
            // ECommons ImGuiEx.EzTabBar pushes "{tab name}tab" inside the tab child before it
            // invokes AutoRetainer's draw callback. Character and header IDs are descendants of it.
            var tabContentScopeId = ImGuiP.GetIDWithSeed($"{GetTabName(section)}tab", sectionWindow.ID);
            foreach (var characterId in characterIds)
            {
                var characterText = characterId.ToString(CultureInfo.InvariantCulture);
                uint characterScopeId;
                uint headerId;

                if (section == AutoRetainerUiSection.Retainers)
                {
                    // EzTabBar: PushID("Retainerstab") -> MultiModeUI: PushID(CID.ToString())
                    // -> "...###workshop{CID}###chara{CID}".
                    characterScopeId = ImGuiP.GetIDWithSeed(characterText, tabContentScopeId);
                    headerId = ImGuiP.GetIDWithSeed($"###chara{characterText}", characterScopeId);
                }
                else
                {
                    // EzTabBar: PushID("Deployablestab") -> WorkshopUI: PushID("Player{CID}")
                    // -> "...###workshop{CID}".
                    characterScopeId = ImGuiP.GetIDWithSeed($"Player{characterText}", tabContentScopeId);
                    headerId = ImGuiP.GetIDWithSeed($"###workshop{characterText}", characterScopeId);
                }

                storage.SetInt(headerId, expanded ? 1 : 0);
            }

            var action = (expanded ? global::XASlave.Localization.UiMessage.Key("SE72D5D8D0B167D21") : global::XASlave.Localization.UiMessage.Key("SB322B65227BC3EC9"));
            return new AutoRetainerUiReflectionResult(
                true,
                characterIds.Count,
                global::XASlave.Localization.UiMessage.Format("S18EFD0A3CB0B93C3", action, characterIds.Count, GetTabDescriptionMessage(section)));
        }
        catch (Exception ex)
        {
            return Failure(global::XASlave.Localization.UiMessage.Format("S5DD4197EFDA84833", global::XASlave.Localization.UiMessage.FromException(ex)));
        }
    }

    public static AutoRetainerUiReflectionResult SetDeployablesAttentionFilter(bool enabled)
        => SetCharacterFilter(enabled
            ? AutoRetainerCharacterFilterMode.Attention
            : AutoRetainerCharacterFilterMode.None);

    public static AutoRetainerUiReflectionResult SetCharacterFilter(AutoRetainerCharacterFilterMode mode)
    {
        try
        {
            if (mode == AutoRetainerCharacterFilterMode.None)
            {
                RemoveCharacterFilter();
                return new AutoRetainerUiReflectionResult(
                    true,
                    0,
                    global::XASlave.Localization.UiMessage.Key("SC8813EAEB0C53A52"));
            }

            var pluginInstance = TryGetAutoRetainerPluginInstance();
            if (pluginInstance == null)
                return Failure(global::XASlave.Localization.UiMessage.Key("S4F0739113FA9DEA9"));

            var config = GetMemberValue(pluginInstance, "config");
            if (config == null)
                return Failure(global::XASlave.Localization.UiMessage.Key("SB2117390E22CC8CC"));

            if (GetMemberValue(config, "OfflineData") is not IList offlineData)
                return Failure(global::XASlave.Localization.UiMessage.Key("SE6916743F6EE93FF"));

            HashSet<ulong>? registeredFcAddressCids = null;
            if (mode == AutoRetainerCharacterFilterMode.MissingLifestreamFcAddress
                && !TryGetRegisteredLifestreamFcAddressCids(out registeredFcAddressCids, out var lifestreamError))
            {
                return Failure(lifestreamError);
            }

            var deployablesPreview = CreateFilteredList(
                pluginInstance,
                offlineData,
                AutoRetainerUiSection.Deployables,
                mode,
                registeredFcAddressCids);
            var retainersPreview = AutoRetainerCharacterFilterPolicy.IsMultiModeFilter(mode)
                ? CreateFilteredList(
                    pluginInstance,
                    offlineData,
                    AutoRetainerUiSection.Retainers,
                    mode,
                    registeredFcAddressCids)
                : null;
            if (deployablesPreview == null
                || (AutoRetainerCharacterFilterPolicy.IsMultiModeFilter(mode) && retainersPreview == null))
            {
                return Failure(global::XASlave.Localization.UiMessage.Key("S6AB141464A8B8402"));
            }

            RemoveCharacterFilter();

            var uiBuilder = TryGetAutoRetainerUiBuilder(pluginInstance);
            var windowSystem = GetMemberValue(pluginInstance, "WindowSystem");
            if (uiBuilder == null || windowSystem == null)
                return Failure(global::XASlave.Localization.UiMessage.Key("SD25FA79F26DEF957"));

            var drawField = uiBuilder.GetType().GetField("Draw", InstanceBindings);
            if (drawField?.GetValue(uiBuilder) is not Action currentDraw)
                return Failure(global::XASlave.Localization.UiMessage.Key("S92B3235395ECD78F"));

            var windowSystemDraw = currentDraw.GetInvocationList()
                .OfType<Action>()
                .FirstOrDefault(x => ReferenceEquals(x.Target, windowSystem)
                    && string.Equals(x.Method.Name, "Draw", StringComparison.Ordinal));
            if (windowSystemDraw == null)
                return Failure(global::XASlave.Localization.UiMessage.Key("SD4B707E99A809A89"));

            Action filteredDraw = () => DrawAutoRetainerWithCharacterFilter(pluginInstance, windowSystemDraw);
            var replacedDraw = ReplaceAction(currentDraw, windowSystemDraw, filteredDraw);
            if (replacedDraw == null)
                return Failure(global::XASlave.Localization.UiMessage.Key("S2DD77719D81ECCD4"));

            filteredUiBuilder = uiBuilder;
            filteredUiBuilderDrawField = drawField;
            originalAutoRetainerWindowSystemDraw = windowSystemDraw;
            filteredAutoRetainerWindowSystemDraw = filteredDraw;
            characterFilterMode = mode;
            registeredLifestreamFcAddressCids = registeredFcAddressCids;
            drawField.SetValue(uiBuilder, replacedDraw);

            if (retainersPreview != null)
            {
                return new AutoRetainerUiReflectionResult(
                    true,
                    retainersPreview.Value.CharacterCount + deployablesPreview.Value.CharacterCount,
                    global::XASlave.Localization.UiMessage.Concat(global::XASlave.Localization.UiMessage.Format("S80960BEC14214BF4", retainersPreview.Value.CharacterCount), global::XASlave.Localization.UiMessage.Format("S3238015CF41D9620", deployablesPreview.Value.CharacterCount, GetFilterResultLabel(mode))));
            }

            return new AutoRetainerUiReflectionResult(
                true,
                deployablesPreview.Value.CharacterCount,
                global::XASlave.Localization.UiMessage.Format("S118C343C23F9290C", deployablesPreview.Value.CharacterCount, GetFilterResultLabel(mode)));
        }
        catch (Exception ex)
        {
            RemoveCharacterFilter();
            return Failure(global::XASlave.Localization.UiMessage.Format("SA2190922A5A9E012", global::XASlave.Localization.UiMessage.FromException(ex)));
        }
    }

    public static void Dispose()
        => RemoveCharacterFilter();

    private static void RemoveCharacterFilter()
    {
        try
        {
            if (filteredUiBuilder != null
                && filteredUiBuilderDrawField != null
                && originalAutoRetainerWindowSystemDraw != null
                && filteredAutoRetainerWindowSystemDraw != null
                && filteredUiBuilderDrawField.GetValue(filteredUiBuilder) is Action currentDraw)
            {
                var restoredDraw = ReplaceAction(
                    currentDraw,
                    filteredAutoRetainerWindowSystemDraw,
                    originalAutoRetainerWindowSystemDraw);
                if (restoredDraw != null)
                    filteredUiBuilderDrawField.SetValue(filteredUiBuilder, restoredDraw);
            }
        }
        catch
        {
            // AutoRetainer may already be unloading and disposing its private UiBuilder.
        }
        finally
        {
            characterFilterMode = AutoRetainerCharacterFilterMode.None;
            registeredLifestreamFcAddressCids = null;
            filteredUiBuilder = null;
            filteredUiBuilderDrawField = null;
            originalAutoRetainerWindowSystemDraw = null;
            filteredAutoRetainerWindowSystemDraw = null;
        }
    }

    private static unsafe void DrawAutoRetainerWithCharacterFilter(object pluginInstance, Action originalDraw)
    {
        object? config = null;
        object? originalOfflineData = null;
        FieldInfo? offlineDataField = null;
        var temporaryListApplied = false;

        var activeSection = GetActiveFilterSection(pluginInstance, characterFilterMode);
        if (characterFilterMode != AutoRetainerCharacterFilterMode.None && activeSection != null)
        {
            try
            {
                config = GetMemberValue(pluginInstance, "config");
                offlineDataField = config?.GetType().GetField("OfflineData", InstanceBindings);
                originalOfflineData = config == null ? null : offlineDataField?.GetValue(config);
                if (config != null
                    && offlineDataField != null
                    && originalOfflineData is IList offlineData
                    && CreateFilteredList(
                        pluginInstance,
                        offlineData,
                        activeSection.Value,
                        characterFilterMode,
                        registeredLifestreamFcAddressCids) is { } filtered)
                {
                    offlineDataField.SetValue(config, filtered.List);
                    temporaryListApplied = true;
                }
            }
            catch
            {
                temporaryListApplied = false;
            }
        }

        try
        {
            originalDraw();
        }
        finally
        {
            if (temporaryListApplied && config != null && offlineDataField != null && originalOfflineData != null)
            {
                try
                {
                    offlineDataField.SetValue(config, originalOfflineData);
                }
                catch
                {
                    // The AutoRetainer UI callback is ending; avoid masking an original draw exception.
                }
            }
        }
    }

    private static (object List, int CharacterCount)? CreateFilteredList(
        object pluginInstance,
        IList offlineData,
        AutoRetainerUiSection section,
        AutoRetainerCharacterFilterMode mode,
        HashSet<ulong>? registeredFcAddressCids)
    {
        if (Activator.CreateInstance(offlineData.GetType()) is not IList filteredList)
            return null;

        MethodInfo? idleSubmarineMethod = null;
        MethodInfo? suboptimalBuildMethod = null;
        IDictionary? selectedRetainers = null;
        if (mode == AutoRetainerCharacterFilterMode.Attention)
        {
            var voyageUtilsType = pluginInstance.GetType().Assembly.GetType("AutoRetainer.Modules.Voyage.VoyageUtils");
            var characterType = offlineData.Cast<object?>().FirstOrDefault(x => x != null)?.GetType();
            if (voyageUtilsType == null || characterType == null)
                return characterType == null ? (filteredList, 0) : null;

            idleSubmarineMethod = FindBooleanExtension(
                voyageUtilsType,
                "IsThereNotAssignedSubmarine",
                characterType);
            suboptimalBuildMethod = FindBooleanExtension(
                voyageUtilsType,
                "AreAnySuboptimalBuildsFound",
                characterType);
            if (idleSubmarineMethod == null || suboptimalBuildMethod == null)
                return null;
        }
        else if (AutoRetainerCharacterFilterPolicy.IsMultiModeFilter(mode)
                 && section == AutoRetainerUiSection.Retainers)
        {
            var config = GetMemberValue(pluginInstance, "config");
            if (config == null || GetMemberValue(config, "SelectedRetainers") is not IDictionary reflectedRetainers)
                return null;

            selectedRetainers = reflectedRetainers;
        }

        var characterCount = 0;
        foreach (var character in offlineData)
        {
            if (character == null)
                continue;

            var excluded = section == AutoRetainerUiSection.Retainers
                ? GetBooleanMember(character, "ExcludeRetainer")
                : GetBooleanMember(character, "ExcludeWorkshop");
            var hasSectionData = section == AutoRetainerUiSection.Retainers
                ? GetCollectionCount(GetMemberValue(character, "RetainerData")) > 0
                : GetCollectionCount(GetMemberValue(character, "OfflineAirshipData"))
                    + GetCollectionCount(GetMemberValue(character, "OfflineSubmarineData")) > 0;
            if (excluded || !hasSectionData)
                continue;

            var includeCharacter = mode switch
            {
                AutoRetainerCharacterFilterMode.MultiEnabled or AutoRetainerCharacterFilterMode.MultiDisabled =>
                    AutoRetainerCharacterFilterPolicy.MatchesMultiModeFilter(
                        section,
                        mode,
                        GetBooleanMember(character, "Enabled"),
                        GetBooleanMember(character, "WorkshopEnabled"),
                        section == AutoRetainerUiSection.Retainers
                            && HasUncheckedRetainerOnMultiEnabledCharacter(character, selectedRetainers),
                        section == AutoRetainerUiSection.Deployables
                            && HasDisabledSubmarineOnMultiEnabledCharacter(character)),
                AutoRetainerCharacterFilterMode.MissingLifestreamFcAddress =>
                    HasNoRegisteredLifestreamFcAddress(character, registeredFcAddressCids),
                AutoRetainerCharacterFilterMode.Attention =>
                    GetInt32Member(character, "NumSubSlots")
                        > GetCollectionCount(GetMemberValue(character, "OfflineSubmarineData"))
                    || HasDisabledSubmarineOnMultiEnabledCharacter(character)
                    || InvokeBooleanMethod(idleSubmarineMethod!, character)
                    || InvokeBooleanMethod(suboptimalBuildMethod!, character),
                _ => true,
            };

            if (!includeCharacter)
                continue;

            filteredList.Add(character);
            characterCount++;
        }

        return (filteredList, characterCount);
    }

    private static AutoRetainerUiSection? GetActiveFilterSection(
        object pluginInstance,
        AutoRetainerCharacterFilterMode mode)
    {
        if (AutoRetainerCharacterFilterPolicy.IsMultiModeFilter(mode)
            && IsSectionActive(pluginInstance, AutoRetainerUiSection.Retainers))
        {
            return AutoRetainerUiSection.Retainers;
        }

        return IsSectionActive(pluginInstance, AutoRetainerUiSection.Deployables)
            ? AutoRetainerUiSection.Deployables
            : null;
    }

    private static unsafe bool IsSectionActive(object pluginInstance, AutoRetainerUiSection section)
    {
        var windowObject = GetMemberValue(pluginInstance, "AutoRetainerWindow");
        var reflectedWindowName = windowObject == null
            ? null
            : GetMemberValue(windowObject, "WindowName")?.ToString();
        var mainWindowName = string.IsNullOrWhiteSpace(reflectedWindowName)
            ? AutoRetainerWindowFallbackName
            : reflectedWindowName;
        var mainWindow = ImGuiP.FindWindowByName(mainWindowName);
        if (mainWindow.IsNull && !string.Equals(mainWindowName, AutoRetainerWindowFallbackName, StringComparison.Ordinal))
            mainWindow = ImGuiP.FindWindowByName(AutoRetainerWindowFallbackName);
        if (mainWindow.IsNull)
            return false;

        var nativeWindowName = Marshal.PtrToStringUTF8((nint)mainWindow.Name);
        if (!string.IsNullOrWhiteSpace(nativeWindowName))
            mainWindowName = nativeWindowName;

        var sectionWindow = FindSectionWindow(mainWindow, mainWindowName, section);
        return !sectionWindow.IsNull
            && (sectionWindow.WasActive || sectionWindow.LastFrameActive >= ImGui.GetFrameCount() - 1);
    }

    private static object? TryGetAutoRetainerUiBuilder(object pluginInstance)
    {
        var pluginLoadContext = AssemblyLoadContext.GetLoadContext(pluginInstance.GetType().Assembly);
        var eCommonsAssembly = pluginLoadContext?.Assemblies.FirstOrDefault(x =>
            string.Equals(x.GetName().Name, "ECommons", StringComparison.OrdinalIgnoreCase));
        var svcType = eCommonsAssembly?.GetType("ECommons.DalamudServices.Svc");
        var pluginInterface = svcType?.GetProperty("PluginInterface", StaticBindings)?.GetValue(null);
        return pluginInterface == null
            ? null
            : GetMemberValue(pluginInterface, "UiBuilder");
    }

    private static Action? ReplaceAction(Action source, Action target, Action replacement)
    {
        Action? rebuilt = null;
        var replaced = false;
        foreach (var action in source.GetInvocationList().OfType<Action>())
        {
            if (action.Equals(target))
            {
                rebuilt += replacement;
                replaced = true;
            }
            else
            {
                rebuilt += action;
            }
        }

        return replaced ? rebuilt : null;
    }

    private static MethodInfo? FindBooleanExtension(Type declaringType, string methodName, Type argumentType)
        => declaringType.GetMethods(StaticBindings)
            .FirstOrDefault(x => string.Equals(x.Name, methodName, StringComparison.Ordinal)
                && x.GetParameters() is [{ ParameterType: var parameterType }]
                && parameterType.IsAssignableFrom(argumentType));

    private static bool InvokeBooleanMethod(MethodInfo method, object argument)
    {
        try
        {
            return method.Invoke(null, [argument]) as bool? ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasDisabledSubmarineOnMultiEnabledCharacter(object character)
    {
        if (!GetBooleanMember(character, "WorkshopEnabled")
            || GetMemberValue(character, "OfflineSubmarineData") is not IEnumerable submarines)
        {
            return false;
        }

        var enabledSubmarineNames = new HashSet<string>(StringComparer.Ordinal);
        if (GetMemberValue(character, "EnabledSubs") is IEnumerable enabledSubs)
        {
            foreach (var enabledSub in enabledSubs)
            {
                var enabledName = enabledSub?.ToString();
                if (!string.IsNullOrEmpty(enabledName))
                    enabledSubmarineNames.Add(enabledName);
            }
        }

        foreach (var submarine in submarines)
        {
            var submarineName = submarine == null
                ? null
                : GetMemberValue(submarine, "Name")?.ToString();
            if (string.IsNullOrEmpty(submarineName) || !enabledSubmarineNames.Contains(submarineName))
                return true;
        }

        return false;
    }

    private static bool HasUncheckedRetainerOnMultiEnabledCharacter(
        object character,
        IDictionary? selectedRetainers)
    {
        if (!GetBooleanMember(character, "Enabled")
            || GetMemberValue(character, "RetainerData") is not IEnumerable retainers)
        {
            return false;
        }

        var enabledRetainerNames = new HashSet<string>(StringComparer.Ordinal);
        var cid = GetUInt64Member(character, "CID");
        if (cid != 0
            && selectedRetainers?.Contains(cid) == true
            && selectedRetainers[cid] is IEnumerable enabledRetainers)
        {
            foreach (var enabledRetainer in enabledRetainers)
            {
                var enabledName = enabledRetainer?.ToString();
                if (!string.IsNullOrEmpty(enabledName))
                    enabledRetainerNames.Add(enabledName);
            }
        }

        foreach (var retainer in retainers)
        {
            var retainerName = retainer == null
                ? null
                : GetMemberValue(retainer, "Name")?.ToString();
            if (string.IsNullOrEmpty(retainerName) || !enabledRetainerNames.Contains(retainerName))
                return true;
        }

        return false;
    }

    private static bool HasNoRegisteredLifestreamFcAddress(
        object character,
        HashSet<ulong>? registeredFcAddressCids)
    {
        var cid = GetUInt64Member(character, "CID");
        return cid != 0
            && registeredFcAddressCids != null
            && !registeredFcAddressCids.Contains(cid);
    }

    private static bool TryGetRegisteredLifestreamFcAddressCids(
        out HashSet<ulong>? registeredFcAddressCids,
        out global::XASlave.Localization.UiMessage error)
    {
        registeredFcAddressCids = null;
        error = global::XASlave.Localization.UiMessage.Raw(string.Empty);

        var pluginInstance = TryGetPluginInstance(LifestreamInternalName, LifestreamPluginTypeName);
        if (pluginInstance == null)
        {
            error = global::XASlave.Localization.UiMessage.Key("S2233D8B4FB0BA515");
            return false;
        }

        var config = GetMemberValue(pluginInstance, "Config");
        if (config == null)
        {
            error = global::XASlave.Localization.UiMessage.Key("SD188A9EDBDB40578");
            return false;
        }

        if (GetMemberValue(config, "HousePathDatas") is not IEnumerable housePathDatas)
        {
            error = global::XASlave.Localization.UiMessage.Key("SE93849ABE77975CA");
            return false;
        }

        var reflectedCids = new HashSet<ulong>();
        foreach (var housePathData in housePathDatas)
        {
            if (housePathData == null || GetBooleanMember(housePathData, "IsPrivate"))
                continue;

            var cid = GetUInt64Member(housePathData, "CID");
            if (cid != 0)
                reflectedCids.Add(cid);
        }

        registeredFcAddressCids = reflectedCids;
        return true;
    }

    private static int GetCollectionCount(object? collection)
    {
        if (collection is ICollection counted)
            return counted.Count;
        if (collection is not IEnumerable enumerable)
            return 0;

        var count = 0;
        foreach (var _ in enumerable)
            count++;
        return count;
    }

    private static int GetInt32Member(object source, string memberName)
    {
        var value = GetMemberValue(source, memberName);
        try
        {
            return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static ulong GetUInt64Member(object source, string memberName)
    {
        var value = GetMemberValue(source, memberName);
        try
        {
            return value == null ? 0 : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    private static unsafe ImGuiWindowPtr FindSectionWindow(
        ImGuiWindowPtr mainWindow,
        string mainWindowName,
        AutoRetainerUiSection section)
    {
        var tabName = GetTabName(section);
        var childLabel = $"{tabName}child";
        var tabBarId = ImGuiP.GetIDWithSeed(AutoRetainerTabBarId, mainWindow.ID);

        // EzTabBar currently scopes child content through the tab bar and selected tab item. Keep
        // fallback seeds for older/newer ImGui layouts where one of those scopes is not pushed.
        var tabItemIdFromTabBar = ImGuiP.GetIDWithSeed(tabName, tabBarId);
        var tabItemIdFromWindow = ImGuiP.GetIDWithSeed(tabName, mainWindow.ID);
        var candidateChildIds = new[]
        {
            ImGuiP.GetIDWithSeed(childLabel, tabItemIdFromTabBar),
            ImGuiP.GetIDWithSeed(childLabel, tabBarId),
            ImGuiP.GetIDWithSeed(childLabel, tabItemIdFromWindow),
            ImGuiP.GetIDWithSeed(childLabel, mainWindow.ID),
        };

        foreach (var childId in candidateChildIds.Distinct())
        {
            var childWindowName = $"{mainWindowName}/{childLabel}_{childId:X8}";
            var childWindow = ImGuiP.FindWindowByName(childWindowName);
            if (!childWindow.IsNull && childWindow.ParentWindow.Handle == mainWindow.Handle)
                return childWindow;
        }

        return ImGuiWindowPtr.Null;
    }

    private static List<ulong> GetCharacterIds(object config)
    {
        if (GetMemberValue(config, "OfflineData") is not IEnumerable offlineData)
            return [];

        var characterIds = new HashSet<ulong>();
        foreach (var character in offlineData)
        {
            if (character == null)
                continue;

            var rawCid = GetMemberValue(character, "CID");
            if (rawCid == null)
                continue;

            try
            {
                var cid = Convert.ToUInt64(rawCid, CultureInfo.InvariantCulture);
                if (cid != 0)
                    characterIds.Add(cid);
            }
            catch (Exception) when (rawCid is IConvertible)
            {
                // Ignore malformed rows; the remaining reflected character data can still be used.
            }
        }

        return characterIds.OrderBy(x => x).ToList();
    }

    internal static bool? GetGcDeliveryOperation()
    {
        // Read the exact loaded instance; never toggle another plugin's operation.
        if (!Plugin.PluginInterface.InstalledPlugins.Any(x => x.InternalName == AutoRetainerInternalName && x.IsLoaded)) return false;
        try
        {
            var instance = TryGetAutoRetainerPluginInstance();
            return instance?.GetType().Assembly.GetType("AutoRetainer.Modules.GcHandin.AutoGCHandin")?
                .GetField("Operation", StaticBindings)?.GetValue(null) is bool running ? running : null;
        }
        catch { return null; }
    }

    internal static object? TryGetAutoRetainerPluginInstance(bool allowAssemblyFallback = true)
        => TryGetPluginInstance(AutoRetainerInternalName, AutoRetainerPluginTypeName, allowAssemblyFallback);

    internal static object? TryGetLoadedPluginInstance(string internalName, string pluginTypeName)
        => TryGetPluginInstance(internalName, pluginTypeName, allowAssemblyFallback: false);

    private static object? TryGetPluginInstance(string internalName, string pluginTypeName, bool allowAssemblyFallback = true)
    {
        try
        {
            var pluginManagerServiceType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Service`1");
            var pluginManagerType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Plugin.Internal.PluginManager");
            if (pluginManagerServiceType == null || pluginManagerType == null)
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S8CD722880916685E"), originalMessage => new InvalidOperationException(originalMessage));

            var pluginManager = pluginManagerServiceType
                .MakeGenericType(pluginManagerType)
                .GetMethod("Get")?
                .Invoke(null, null);
            var installedPlugins = pluginManager?.GetType()
                .GetProperty("InstalledPlugins", InstanceBindings)?
                .GetValue(pluginManager) as IEnumerable;

            if (installedPlugins != null)
            {
                foreach (var pluginState in installedPlugins)
                {
                    if (pluginState == null
                        || !GetBooleanMember(pluginState, "IsLoaded")
                        || !IsPluginState(pluginState, internalName))
                    {
                        continue;
                    }

                    var instance = GetFieldValueInHierarchy(pluginState, "instance");
                    if (instance != null)
                        return instance;
                }
            }
        }
        catch
        {
            // Fall back to the plugin's own static live-instance field below.
        }

        // Mutating inventory operations require the actual loaded manager instance;
        // an old assembly can retain its static P field after a provider reload.
        if (!allowAssemblyFallback)
            return null;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(x => string.Equals(x.GetName().Name, internalName, StringComparison.OrdinalIgnoreCase)))
        {
            var pluginType = assembly.GetType(pluginTypeName);
            var instance = pluginType?.GetField("P", StaticBindings)?.GetValue(null);
            if (instance != null)
                return instance;
        }

        return null;
    }

    private static bool IsPluginState(object pluginState, string internalName)
    {
        return string.Equals(GetMemberValue(pluginState, "InternalName")?.ToString(), internalName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(GetMemberValue(pluginState, "Name")?.ToString(), internalName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool GetBooleanMember(object source, string memberName)
        => GetMemberValue(source, memberName) as bool? ?? false;

    private static object? GetFieldValueInHierarchy(object source, string fieldName)
    {
        for (var type = source.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(fieldName, InstanceBindings | BindingFlags.DeclaredOnly);
            if (field != null)
                return field.GetValue(source);
        }

        return null;
    }

    private static object? GetMemberValue(object source, string memberName)
    {
        var type = source.GetType();
        return type.GetProperty(memberName, InstanceBindings)?.GetValue(source)
            ?? type.GetField(memberName, InstanceBindings)?.GetValue(source);
    }


    private static global::XASlave.Localization.UiMessage GetTabDescriptionMessage(AutoRetainerUiSection section)
        => section == AutoRetainerUiSection.Retainers ? global::XASlave.Localization.UiMessage.Key("S17642A16F49FEE36") : global::XASlave.Localization.UiMessage.Key("SEC184B4F9A67F4C6");

    private static string GetTabName(AutoRetainerUiSection section)
        => section == AutoRetainerUiSection.Retainers ? "Retainers" : "Deployables";

    private static global::XASlave.Localization.UiMessage GetFilterResultLabel(AutoRetainerCharacterFilterMode mode)
        => mode switch
        {
            AutoRetainerCharacterFilterMode.Attention => global::XASlave.Localization.UiMessage.Key("SDF905058DD67A409"),
            AutoRetainerCharacterFilterMode.MultiEnabled => global::XASlave.Localization.UiMessage.Key("SF4E6CBA1503247A9"),
            AutoRetainerCharacterFilterMode.MultiDisabled => global::XASlave.Localization.UiMessage.Key("S6400D2AB61C3B09F"),
            AutoRetainerCharacterFilterMode.MissingLifestreamFcAddress => global::XASlave.Localization.UiMessage.Key("S99366CA2A74B57C7"),
            _ => global::XASlave.Localization.UiMessage.Key("S13A30363EB940C6C"),
        };

    private static AutoRetainerUiReflectionResult Failure(global::XASlave.Localization.UiMessage message)
        => new(false, 0, message);
}

/// <summary>Associates metadata only with a fresh producer-owned string identity; raw values are never matched by text.</summary>
internal static class AutoRetainerUiMessagePresentation
{
    private static readonly ConditionalWeakTable<string, global::XASlave.Localization.UiMessage> Messages = new();

    internal static string Store(global::XASlave.Localization.UiMessage message)
    {
        var original = new string(message.Original.AsSpan());
        Messages.AddOrUpdate(original, message);
        return original;
    }

    internal static global::XASlave.Localization.UiMessage Get(string original)
        => original != null && Messages.TryGetValue(original, out var message) ? message : global::XASlave.Localization.UiMessage.Raw(original ?? string.Empty);
}
