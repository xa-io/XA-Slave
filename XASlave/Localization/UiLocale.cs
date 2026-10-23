using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;

namespace XASlave.Localization;

/// <summary>Consumer-owned UI resources. Game data, command tokens and saved user text stay opaque.</summary>
internal static class UiLocale
{
    internal readonly record struct Language(string Code, string Name, string CultureName);

    internal static readonly Language[] Languages =
    [
        new("en", "English", "en-US"),
        new("de", "Deutsch", "de-DE"),
        new("fr", "Français", "fr-FR"),
        new("es", "Español", "es-ES"),
        new("it", "Italiano", "it-IT"),
        new("ru", "Русский", "ru-RU"),
        new("ja", "日本語", "ja-JP"),
        new("ko", "한국어", "ko-KR"),
        new("zh-Hans", "简体中文", "zh-CN"),
        new("vi", "Tiếng Việt", "vi-VN"),
        new("pt-BR", "Português (Brasil)", "pt-BR"),
        new("id", "Bahasa Indonesia", "id-ID"),
        new("pl", "Polski", "pl-PL"),
        new("tr", "Türkçe", "tr-TR"),
    ];

    private sealed record Catalog(Dictionary<string, string> Values, string? Error, bool Missing = false);

    private static readonly Dictionary<string, Catalog> Catalogs = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> Keys = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> ReportedErrors = new(StringComparer.Ordinal);
    private static readonly Regex Placeholders = new(@"\{\d+(?:\s*,\s*[+-]?\d+)?(?::[^{}]*)?\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));
    private static Dictionary<string, string> active = new(StringComparer.Ordinal);
    private static HashSet<string> activeValues = new(StringComparer.Ordinal);
    private static Dictionary<string, string> englishValueKeys = new(StringComparer.Ordinal);
    private static Dictionary<string, string>? nativeLabels;
    private static string? nativeError;
    private static string? pending;
    private static string? selectionError;

    internal static string LanguageCode { get; private set; } = "en";
    internal static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
    internal static string? Error => selectionError ?? nativeError;
    internal static int Revision { get; private set; }

    internal static void Initialize(Configuration configuration)
    {
        Catalogs.Clear();
        Keys.Clear();
        ReportedErrors.Clear();
        active = new Dictionary<string, string>(StringComparer.Ordinal);
        activeValues = new HashSet<string>(StringComparer.Ordinal);
        englishValueKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        nativeLabels = null;
        nativeError = null;
        selectionError = null;
        LanguageCode = "en";
        Culture = CultureInfo.GetCultureInfo("en-US");
        pending = Normalize(configuration.UiLanguage);
        LoadNativeLabels();
        BeginFrame();
    }

    /// <summary>Apply only before drawing the first window, never in the middle of a native scope.</summary>
    internal static void BeginFrame()
    {
        if (pending is not { } next)
            return;

        pending = null;
        var catalog = Load(next);
        LanguageCode = next;
        Culture = CultureInfo.GetCultureInfo(Languages.First(item => item.Code == next).CultureName);
        selectionError = catalog.Error;
        if (catalog.Missing && next != "en" && ReportedErrors.TryAdd("selected/" + next, 0))
            Plugin.Log?.Error("[XASlave] The saved UI language {Language} has no translation resource set. Select English to recover.", next);
        active = catalog.Values;
        activeValues = active.Values.ToHashSet(StringComparer.Ordinal);
        // Edited English values may be passed by consumer-owned display helpers. Only an
        // unambiguous reverse mapping is safe; duplicate edited captions remain opaque.
        englishValueKeys = Load("en").Values.GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);
        Revision++;
    }

    internal static string Normalize(string? code)
    {
        if (string.Equals(code, "zh-CN", StringComparison.OrdinalIgnoreCase))
            return "zh-Hans";
        return Languages.FirstOrDefault(item => string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase)).Code ?? "en";
    }

    /// <summary>Translate an authored English phrase. This is never a general data/string translator.</summary>
    internal static string T(string english)
    {
        if (string.IsNullOrEmpty(english))
            return english;
        var key = Keys.TryGetValue(english, out var known) ? known : Key(english);
        if (active.TryGetValue(key, out var translated))
        {
            Keys.TryAdd(english, key);
            return translated;
        }
        if (activeValues.Contains(english))
            return english;
        if (englishValueKeys.TryGetValue(english, out key) && active.TryGetValue(key, out translated))
            return translated;
        // Dynamic/composed authored status values are not automatically resource keys. Callers
        // must use Get/FormatKey when a specific catalog key is required, and keep raw data out.
        return english;
    }

    /// <summary>English is authoritative too: editing its resource value changes the displayed text.</summary>
    internal static string Get(string key)
    {
        if (active.TryGetValue(key, out var translated))
            return translated;
        if (ReportedErrors.TryAdd(LanguageCode + "/" + key, 0))
            Plugin.Log?.Warning("[XASlave] Missing UI text key {Key} in locale {Language}.", key, LanguageCode);
        return "[Missing UI text: " + key + "]";
    }

    /// <summary>Immutable original captions preserve established native control IDs when English text is edited.</summary>
    internal static string Native(string key)
    {
        LoadNativeLabels();
        if (nativeLabels!.TryGetValue(key, out var original))
            return original;
        if (ReportedErrors.TryAdd("native/" + key, 0))
            Plugin.Log?.Warning("[XASlave] Missing original native UI label {Key}.", key);
        return "[Missing UI label: " + key + "]";
    }

    internal static string Translate(string english) => T(english);
    internal static bool HasOriginal(string key)
    {
        LoadNativeLabels();
        return nativeLabels!.ContainsKey(key);
    }
    internal static string F(FormattableString value) => FormatKey(Keys.GetOrAdd(value.Format, Key), value.GetArguments());
    internal static string Format(string english, params object?[] values) => FormatKey(Keys.GetOrAdd(english, Key), values);
    internal static string FormatKey(string key, params object?[] values)
    {
        try { return string.Format(Culture, Get(key), values); }
        catch (FormatException exception)
        {
            if (ReportedErrors.TryAdd("format/" + LanguageCode + "/" + key, 0))
                Plugin.Log?.Warning(exception, "[XASlave] Invalid UI format key {Key} in locale {Language}.", key, LanguageCode);
            return "[Invalid UI format: " + key + "]";
        }
    }
    internal static string Key(string english) => "S" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(english)))[..16];

    internal static float LanguageSelectorWidth(Configuration configuration)
    {
        var selected = Languages.First(item => item.Code == Normalize(configuration.UiLanguage));
        var minimum = ImGui.CalcTextSize(selected.Name).X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2;
        return MathF.Max(minimum, ImGui.GetFontSize() * 9);
    }

    internal static bool DrawLanguageSelector(string id, Configuration configuration, bool showLabel = false)
    {
        if (showLabel)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(T("Language"));
            ImGui.SameLine();
        }

        var saved = Normalize(configuration.UiLanguage);
        var selected = Languages.First(item => item.Code == saved);
        ImGui.SetNextItemWidth(LanguageSelectorWidth(configuration));
        var changed = false;
        if (ImGui.BeginCombo("##" + id, selected.Name))
        {
            try
            {
                foreach (var language in Languages)
                {
                    var catalog = Load(language.Code);
                    var unavailable = catalog.Error != null;
                    ImGui.PushID(language.Code);
                    ImGui.BeginDisabled(unavailable);
                    try
                    {
                        if (ImGui.Selectable(language.Name, language.Code == saved))
                            changed = SetLanguage(configuration, language.Code);
                        if (language.Code == saved)
                            ImGui.SetItemDefaultFocus();
                    }
                    finally
                    {
                        ImGui.EndDisabled();
                        ImGui.PopID();
                    }
                    if (unavailable && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                        ImGui.SetTooltip(T(catalog.Missing ? "Translation not available yet" : "This language is unavailable because its translation resources are incomplete."));
                }
            }
            finally
            {
                ImGui.EndCombo();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(T("Language"));
        return changed;
    }

    internal static bool SetLanguage(Configuration configuration, string code)
    {
        var normalized = Normalize(code);
        if (Load(normalized).Error != null)
            return false;
        var previous = configuration.UiLanguage;
        configuration.UiLanguage = normalized;
        try
        {
            configuration.Save();
            pending = normalized;
            return true;
        }
        catch (Exception exception)
        {
            configuration.UiLanguage = previous;
            Plugin.Log.Error(exception, "[XASlave] Unable to save the selected UI language.");
            return false;
        }
    }

    /// <summary>The selected catalog, English fallback, all dropdown names and typed format separators.</summary>
    internal static string RequiredText()
    {
        var builder = new StringBuilder();
        foreach (var text in Load("en").Values.Values)
            builder.Append(text);
        LoadNativeLabels();
        foreach (var text in nativeLabels!.Values)
            builder.Append(text);
        if (LanguageCode != "en")
            foreach (var text in Load(LanguageCode).Values.Values)
                builder.Append(text);
        foreach (var language in Languages)
            builder.Append(language.Name);
        builder.Append(Culture.NumberFormat.NumberGroupSeparator);
        builder.Append(Culture.NumberFormat.NumberDecimalSeparator);
        builder.Append(Culture.NumberFormat.NegativeSign);
        builder.Append("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz…•✓✕←→↑↓★☆⚠");
        return builder.ToString();
    }

    private static Catalog Load(string code)
    {
        if (Catalogs.TryGetValue(code, out var cached))
            return cached;
        try
        {
            var baseName = "XASlave.Lang." + code + ".Strings";
            if (!typeof(UiLocale).Assembly.GetManifestResourceNames().Contains(baseName + ".resources", StringComparer.Ordinal))
            {
                cached = new Catalog(new Dictionary<string, string>(StringComparer.Ordinal), "This language has not been supplied: " + code, Missing: true);
                Catalogs.Add(code, cached);
                // Non-English folders are reserved for future complete translations. Their absence
                // is expected and must not flood the host log when the selector is opened.
                if (code == "en")
                    Plugin.Log?.Error("[XASlave] The required English UI resource set is missing.");
                return cached;
            }
            var manager = new ResourceManager(baseName, typeof(UiLocale).Assembly);
            Dictionary<string, string> values;
            try
            {
                using var resourceSet = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)
                    ?? throw new MissingManifestResourceException("Missing resource set: " + baseName);
                values = new Dictionary<string, string>(StringComparer.Ordinal);
                var keysIgnoringCase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in resourceSet)
                {
                    if (entry.Key is not string key || entry.Value is not string value || !keysIgnoringCase.Add(key))
                        throw new InvalidOperationException("Invalid or duplicated translation key in " + baseName);
                    values.Add(key, value);
                }
                if (values.Count == 0)
                    throw new InvalidOperationException("Translation resource set is empty: " + baseName);
            }
            finally
            {
                manager.ReleaseAllResources();
            }

            if (code != "en")
            {
                var english = Load("en");
                if (english.Error != null)
                    throw new InvalidOperationException(english.Error);
                if (!english.Values.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(values.Keys))
                    throw new InvalidOperationException("Translation key coverage differs from English: " + code);
                foreach (var pair in english.Values)
                {
                    var translation = values[pair.Key];
                    if (pair.Value.Length != 0 && string.IsNullOrWhiteSpace(translation)
                        && !IsEmptyGrammarSuffix(pair.Key, pair.Value, translation))
                        throw new InvalidOperationException("Empty translation: " + code + "/" + pair.Key);
                    var originalTokens = FormatTokens(pair.Value).Order(StringComparer.Ordinal);
                    var translatedTokens = FormatTokens(translation).Order(StringComparer.Ordinal);
                    if (!originalTokens.SequenceEqual(translatedTokens))
                        throw new InvalidOperationException("Translation placeholders differ: " + code + "/" + pair.Key);
                }
            }
            cached = new Catalog(values, null);
        }
        catch (Exception exception)
        {
            cached = new Catalog(new Dictionary<string, string>(StringComparer.Ordinal), exception.Message);
            Plugin.Log.Error(exception, "[XASlave] UI translation resources are unavailable for {Language}.", code);
        }
        Catalogs.Add(code, cached);
        return cached;
    }

    // This one resource is only an English plural suffix in five counted messages.
    // Languages without that suffix may omit it; labels, whitespace-only values,
    // and any other key still require text. The source verifier audits its callers.
    private static bool IsEmptyGrammarSuffix(string key, string english, string translation)
        => key == "S043A718774C572BD" && english == "s" && translation.Length == 0;

    private static IEnumerable<string> FormatTokens(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            // Escaped braces are literals, while {{{0}}} still contains a real placeholder.
            if (index + 1 < text.Length && (text[index] == '{' || text[index] == '}') && text[index + 1] == text[index])
            {
                index++;
                continue;
            }
            if (text[index] != '{')
                continue;
            var token = Placeholders.Match(text, index);
            if (!token.Success || token.Index != index)
                continue;
            yield return token.Value;
            index += token.Length - 1;
        }
    }

    private static void LoadNativeLabels()
    {
        if (nativeLabels != null)
            return;
        nativeLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        var manager = new ResourceManager("XASlave.Lang.NativeLabels", typeof(UiLocale).Assembly);
        try
        {
            using var set = manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)
                ?? throw new MissingManifestResourceException("Missing original native-label resource set.");
            var caseIndependentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in set)
            {
                if (entry.Key is not string key || entry.Value is not string value || !caseIndependentKeys.Add(key))
                    throw new InvalidOperationException("Invalid or duplicated original native-label key.");
                if (Key(value) != key)
                    throw new InvalidOperationException("Original native label differs from its immutable identity: " + key);
                nativeLabels.Add(key, value);
            }
            if (nativeLabels.Count == 0)
                throw new InvalidOperationException("The original native-label resource set is empty.");
        }
        catch (Exception exception)
        {
            nativeLabels.Clear();
            nativeError = exception.Message;
            Plugin.Log?.Error(exception, "[XASlave] Original native UI labels are unavailable.");
        }
        finally
        {
            manager.ReleaseAllResources();
        }
    }
}
