using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;

namespace XASlave.Services;

internal readonly record struct AutoRetainerUiReflectionResult(
    bool Success,
    int CharacterCount,
    string Message);

internal enum AutoRetainerRelogTargetStatus
{
    Unknown,
    Found,
    Missing,
}

internal readonly record struct AutoRetainerRelogTargetResult(
    AutoRetainerRelogTargetStatus Status,
    string Message);

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
                return RelogTargetUnknown("AutoRetainer is not loaded or its runtime instance could not be reflected.");

            var config = GetMemberValue(pluginInstance, "config");
            if (config == null)
                return RelogTargetUnknown("AutoRetainer's live configuration could not be reflected.");

            return EvaluateRelogTargetRoster(GetMemberValue(config, "OfflineData"), characterNameWorld);
        }
        catch (Exception ex)
        {
            return RelogTargetUnknown($"AutoRetainer live relog roster could not be inspected ({ex.GetType().Name}).");
        }
    }

    internal static AutoRetainerRelogTargetResult EvaluateRelogTargetRoster(object? rawRoster, string characterNameWorld)
    {
        if (string.IsNullOrWhiteSpace(characterNameWorld))
            return RelogTargetUnknown("The relog target is blank.");

        var separator = characterNameWorld.IndexOf('@');
        if (separator <= 0 || separator != characterNameWorld.LastIndexOf('@')
            || separator == characterNameWorld.Length - 1
            || string.IsNullOrWhiteSpace(characterNameWorld[..separator])
            || string.IsNullOrWhiteSpace(characterNameWorld[(separator + 1)..]))
        {
            return RelogTargetUnknown("The relog target does not contain one complete Name@World identity.");
        }

        if (rawRoster is not IList roster)
            return RelogTargetUnknown("AutoRetainer did not expose a readable live OfflineData list.");

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
                    return RelogTargetUnknown("AutoRetainer's live OfflineData list contains an unreadable or incomplete character identity.");
                }

                // AutoRetainer's command handler uses exact, case-sensitive Name@World equality.
                // Scan the entire list so a malformed later row cannot produce a false Missing result.
                found |= string.Equals($"{name}@{world}", characterNameWorld, StringComparison.Ordinal);
            }

            return found
                ? new(AutoRetainerRelogTargetStatus.Found, "The exact relog target exists in AutoRetainer's live OfflineData list.")
                : new(AutoRetainerRelogTargetStatus.Missing, "The exact relog target is absent from AutoRetainer's live OfflineData list.");
        }
        catch (Exception ex)
        {
            return RelogTargetUnknown($"AutoRetainer's live OfflineData list could not be read completely ({ex.GetType().Name}).");
        }
    }

    private static AutoRetainerRelogTargetResult RelogTargetUnknown(string message)
        => new(AutoRetainerRelogTargetStatus.Unknown, message);

    public static unsafe AutoRetainerUiReflectionResult SetAllCharacterHeaders(
        AutoRetainerUiSection section,
        bool expanded)
    {
        try
        {
            var pluginInstance = TryGetAutoRetainerPluginInstance();
            if (pluginInstance == null)
                return Failure("AutoRetainer is not loaded or its runtime instance could not be reflected.");

            var config = GetMemberValue(pluginInstance, "config");
            if (config == null)
                return Failure("AutoRetainer's live configuration could not be reflected.");

            var characterIds = GetCharacterIds(config);
            if (characterIds.Count == 0)
                return Failure("AutoRetainer did not expose any offline character IDs.");

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
                return Failure("AutoRetainer's main window has not been drawn yet. Open it once, then retry.");

            var nativeWindowName = Marshal.PtrToStringUTF8((nint)mainWindow.Name);
            if (!string.IsNullOrWhiteSpace(nativeWindowName))
                mainWindowName = nativeWindowName;

            var sectionWindow = FindSectionWindow(mainWindow, mainWindowName, section);
            if (sectionWindow.IsNull)
            {
                var tabName = GetTabName(section);
                return Failure($"AutoRetainer's {tabName} tab has not been drawn yet. Open that tab once, then retry.");
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

            var action = expanded ? "Expanded" : "Collapsed";
            return new AutoRetainerUiReflectionResult(
                true,
                characterIds.Count,
                $"AutoRetainer: {action} {characterIds.Count} {GetTabName(section).ToLowerInvariant()} character row(s).");
        }
        catch (Exception ex)
        {
            return Failure($"AutoRetainer UI reflection failed: {ex.Message}");
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
                    "AutoRetainer: Showing all character rows again.");
            }

            var pluginInstance = TryGetAutoRetainerPluginInstance();
            if (pluginInstance == null)
                return Failure("AutoRetainer is not loaded or its runtime instance could not be reflected.");

            var config = GetMemberValue(pluginInstance, "config");
            if (config == null)
                return Failure("AutoRetainer's live configuration could not be reflected.");

            if (GetMemberValue(config, "OfflineData") is not IList offlineData)
                return Failure("AutoRetainer did not expose its offline character list.");

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
                return Failure("AutoRetainer's filtered character list could not be created.");
            }

            RemoveCharacterFilter();

            var uiBuilder = TryGetAutoRetainerUiBuilder(pluginInstance);
            var windowSystem = GetMemberValue(pluginInstance, "WindowSystem");
            if (uiBuilder == null || windowSystem == null)
                return Failure("AutoRetainer's UI draw surface could not be reflected.");

            var drawField = uiBuilder.GetType().GetField("Draw", InstanceBindings);
            if (drawField?.GetValue(uiBuilder) is not Action currentDraw)
                return Failure("AutoRetainer's UI draw callbacks could not be reflected.");

            var windowSystemDraw = currentDraw.GetInvocationList()
                .OfType<Action>()
                .FirstOrDefault(x => ReferenceEquals(x.Target, windowSystem)
                    && string.Equals(x.Method.Name, "Draw", StringComparison.Ordinal));
            if (windowSystemDraw == null)
                return Failure("AutoRetainer's WindowSystem draw callback could not be identified.");

            Action filteredDraw = () => DrawAutoRetainerWithCharacterFilter(pluginInstance, windowSystemDraw);
            var replacedDraw = ReplaceAction(currentDraw, windowSystemDraw, filteredDraw);
            if (replacedDraw == null)
                return Failure("AutoRetainer's WindowSystem draw callback could not be wrapped.");

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
                    $"AutoRetainer: Showing {retainersPreview.Value.CharacterCount} Retainers and "
                    + $"{deployablesPreview.Value.CharacterCount} Deployables {GetFilterResultLabel(mode)} character row(s) only.");
            }

            return new AutoRetainerUiReflectionResult(
                true,
                deployablesPreview.Value.CharacterCount,
                $"AutoRetainer: Showing {deployablesPreview.Value.CharacterCount} {GetFilterResultLabel(mode)} Deployables character row(s) only.");
        }
        catch (Exception ex)
        {
            RemoveCharacterFilter();
            return Failure($"AutoRetainer character filter failed: {ex.Message}");
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
        out string error)
    {
        registeredFcAddressCids = null;
        error = string.Empty;

        var pluginInstance = TryGetPluginInstance(LifestreamInternalName, LifestreamPluginTypeName);
        if (pluginInstance == null)
        {
            error = "Lifestream is not loaded or its runtime instance could not be reflected.";
            return false;
        }

        var config = GetMemberValue(pluginInstance, "Config");
        if (config == null)
        {
            error = "Lifestream's live configuration could not be reflected.";
            return false;
        }

        if (GetMemberValue(config, "HousePathDatas") is not IEnumerable housePathDatas)
        {
            error = "Lifestream did not expose its registered house-address list.";
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

    private static object? TryGetAutoRetainerPluginInstance()
        => TryGetPluginInstance(AutoRetainerInternalName, AutoRetainerPluginTypeName);

    private static object? TryGetPluginInstance(string internalName, string pluginTypeName)
    {
        try
        {
            var pluginManagerServiceType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Service`1");
            var pluginManagerType = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Plugin.Internal.PluginManager");
            if (pluginManagerServiceType == null || pluginManagerType == null)
                throw new InvalidOperationException("Dalamud plugin-manager reflection types were not found.");

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

    private static string GetTabName(AutoRetainerUiSection section)
        => section == AutoRetainerUiSection.Retainers ? "Retainers" : "Deployables";

    private static string GetFilterResultLabel(AutoRetainerCharacterFilterMode mode)
        => mode switch
        {
            AutoRetainerCharacterFilterMode.Attention => "alert",
            AutoRetainerCharacterFilterMode.MultiEnabled => "multi-enabled",
            AutoRetainerCharacterFilterMode.MultiDisabled => "multi-disabled",
            AutoRetainerCharacterFilterMode.MissingLifestreamFcAddress => "missing-FC-address",
            _ => "filtered",
        };

    private static AutoRetainerUiReflectionResult Failure(string message)
        => new(false, 0, message);
}
