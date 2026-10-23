using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;

namespace XASlave.Localization;

/// <summary>Retains the host's default face and size while adding the active catalog's missing glyphs.</summary>
internal sealed class UiLocaleFonts : IDisposable
{
    [ThreadStatic] private static ImFontPtr priorHostFont;
    [ThreadStatic] private static int fontScopeDepth;
    private readonly IUiBuilder uiBuilder;
    private readonly IFontAtlas atlas;
    private IFontHandle? handle;
    private int revision = -1;
    private bool disposed;
    private string? constructionError;

    internal bool IsReady => UiLocale.Error == null && constructionError == null && handle is { Available: true, LoadException: null };

    internal UiLocaleFonts(IUiBuilder uiBuilder)
    {
        this.uiBuilder = uiBuilder;
        atlas = uiBuilder.CreateFontAtlas(FontAtlasAutoRebuildMode.Async, true, "XA Slave languages");
    }

    /// <summary>Called once before any XA Slave native window begins; a false result requires DrawStatus.</summary>
    internal bool BeginFrame()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        UiLocale.BeginFrame();
        if (revision != UiLocale.Revision)
        {
            revision = UiLocale.Revision;
            constructionError = null;
            handle?.Dispose();
            handle = null;
            try
            {
                handle = CreateHandle(UiLocale.RequiredText(), UiLocale.LanguageCode);
            }
            catch (Exception exception)
            {
                constructionError = exception.Message;
                Plugin.Log.Error(exception, "[XASlave] Unable to prepare localization fonts.");
            }
        }
        return IsReady;
    }

    internal IDisposable Push()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (handle == null)
            throw new InvalidOperationException("Localization font has not been prepared.");
        var previous = priorHostFont;
        if (fontScopeDepth == 0)
            priorHostFont = ImGui.GetFont();
        var pushed = handle.Push();
        fontScopeDepth++;
        return new Scope(() =>
        {
            try { pushed.Dispose(); }
            finally { fontScopeDepth--; priorHostFont = previous; }
        });
    }

    /// <summary>Do not impose XA Slave's font on an externally supplied task panel.</summary>
    internal static IDisposable PreserveExternalFont()
    {
        if (fontScopeDepth == 0)
            return new Scope(null);
        ImGui.PushFont(priorHostFont);
        return new Scope(ImGui.PopFont);
    }

    private sealed class Scope(Action? restore) : IDisposable
    {
        private Action? restore = restore;
        public void Dispose() => System.Threading.Interlocked.Exchange(ref restore, null)?.Invoke();
    }

    /// <summary>ASCII-only recovery remains readable when the selected catalog or atlas cannot load.</summary>
    internal void DrawStatus(Configuration configuration)
    {
        ImGui.SetNextWindowSize(new Vector2(ImGui.GetFontSize() * 30, 0), ImGuiCond.Always);
        if (ImGui.Begin("XA Slave - Language###XASlaveLanguageStatus", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
        {
            var error = UiLocale.Error ?? constructionError ?? handle?.LoadException?.Message;
            ImGui.TextWrapped(error == null
                ? "Loading XA Slave language fonts..."
                : "The selected XA Slave language could not load. Choose English to recover, or check the Dalamud log.");
            if (error != null && ImGui.Button("Retry##XASlaveLanguageRetry"))
                revision = -1;
            if (error != null)
                ImGui.SameLine();
            if (ImGui.Button("Use English##XASlaveLanguageEnglish"))
                UiLocale.SetLanguage(configuration, "en");
        }
        ImGui.End();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        handle?.Dispose();
        atlas.Dispose();
    }

    private unsafe IFontHandle CreateHandle(string requiredText, string language)
    {
        // Every dropdown's native name is included even in English; only the selected catalog is baked.
        var required = requiredText.Where(character => !char.IsControl(character) && !char.IsSurrogate(character)).Distinct().Order().ToArray();
        var ranges = FontAtlasBuildToolkitUtilities.ToGlyphRange(required.AsSpan(), true, true);
        var cjk = required.Where(IsCjk).ToArray();
        var cjkRanges = FontAtlasBuildToolkitUtilities.ToGlyphRange(cjk.AsSpan(), true, true);
        var regionalFont = default(ImFontPtr);
        var face = language switch { "ko" => 1, "zh-Hans" => 2, _ => 0 };
        var fontsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        var textFont = Path.Combine(fontsDirectory, "segoeui.ttf");
        var symbolFont = Path.Combine(fontsDirectory, "seguisym.ttf");

        return atlas.NewDelegateFontHandle(step =>
        {
            step.OnPreBuild(build =>
            {
                build.NewImAtlas.TexDesiredWidth = 4096;
                build.NewImAtlas.TexDesiredHeight = 4096;
                var size = uiBuilder.DefaultFontSpec.SizePx;
                var font = build.AddDalamudDefaultFont(-1);
                build.Font = font;

                // Merge mode fills missing glyphs, preserving the user's existing Latin body face.
                var supplement = new SafeFontConfig { SizePx = size, MergeFont = font, GlyphRanges = ranges };
                if (File.Exists(textFont))
                    build.AddFontFromFile(textFont, supplement);
                if (File.Exists(symbolFont))
                    build.AddFontFromFile(symbolFont, supplement);
                build.AttachExtraGlyphsForDalamudLanguage(supplement);

                // Host-language fallback can already include CJK. A separate regional source permits
                // replacing only those glyphs after baking without replacing Latin text or UI styling.
                regionalFont = build.AddDalamudAssetFont(DalamudAsset.NotoSansCjkRegular,
                    new SafeFontConfig { SizePx = size, GlyphRanges = cjkRanges, FontNo = face });
            });

            step.OnPostBuild(build =>
            {
                for (var index = 0; index + 1 < cjkRanges.Length && cjkRanges[index] != 0; index += 2)
                {
                    if (IsCjk((char)cjkRanges[index]))
                        build.CopyGlyphsAcrossFonts(regionalFont, build.Font, false, false, (char)cjkRanges[index], (char)cjkRanges[index + 1]);
                }
                build.BuildLookupTable(build.Font);
                var missing = required.Where(character => build.Font.FindGlyphNoFallback(character) == null).Take(12).ToArray();
                if (missing.Length != 0)
                    throw new InvalidOperationException("XA Slave " + language + " font is missing " + string.Join(", ", missing.Select(character => "U+" + ((int)character).ToString("X4"))));
            });
        });
    }

    private static bool IsCjk(char character) => character is >= '\u2E80' and <= '\uA4CF'
        or >= '\uAC00' and <= '\uD7AF'
        or >= '\uF900' and <= '\uFAFF'
        or >= '\uFE30' and <= '\uFE4F'
        or >= '\uFF00' and <= '\uFFEF';
}
