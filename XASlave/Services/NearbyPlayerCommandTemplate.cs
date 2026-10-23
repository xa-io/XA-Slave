using System;
using System.Collections.Generic;
using System.Text;
using XASlave.Data;

namespace XASlave.Services;

// Parsed once on configuration publication. Replacements are appended as inert text.
internal sealed class NearbyPlayerCommandTemplate
{
    private readonly record struct Part(string Literal, int Replacement);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Part[] parts;
    private readonly NearbyPlayerEntryMode mode;
    private readonly bool authoredSlash;
    private NearbyPlayerCommandTemplate(Part[] parts, NearbyPlayerEntryMode mode, bool authoredSlash)
    { this.parts = parts; this.mode = mode; this.authoredSlash = authoredSlash; }

    internal static string[] SplitEntries(IEnumerable<string> configured)
    {
        var entries = new List<string>();
        foreach (var block in configured)
        {
            if (block == null) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S3995C2402EB84C3D"), originalMessageText => new ArgumentException(originalMessageText));
            foreach (var line in block.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var entry = line.Trim();
                if (entry.Length == 0) continue;
                entries.Add(entry);
                if (entries.Count > 15) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S75312EA6D8F855DA"), originalMessageText => new ArgumentException(originalMessageText));
            }
        }
        return entries.ToArray();
    }

    internal static NearbyPlayerCommandTemplate Parse(string template, NearbyPlayerEntryMode mode)
    {
        if (!Enum.IsDefined(mode)) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SBAF0E97FE6440018"), originalMessageText => new ArgumentException(originalMessageText));
        RequirePlainText(template);
        template = template.Trim();
        if (template.Length == 0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SDE0089523D8AC723"), originalMessageText => new ArgumentException(originalMessageText));
        var slash = template[0] == '/';
        if (mode == NearbyPlayerEntryMode.SlashCommand && !slash) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S0E1E8D56E11E7F32"), originalMessageText => new ArgumentException(originalMessageText));
        var parts = new List<Part>();
        var literal = new StringBuilder();
        var commandHead = slash;
        for (var index = 0; index < template.Length; index++)
        {
            var character = template[index];
            if (char.IsWhiteSpace(character)) commandHead = false;
            if (character != '{' && character != '}') { literal.Append(character); continue; }
            if (index + 1 < template.Length && template[index + 1] == character)
            { literal.Append(character); index++; continue; }
            if (character == '}') throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S2BD93EB55CCF0CD1"), originalMessageText => new ArgumentException(originalMessageText));
            if (commandHead) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S28F5FB7C12EBE162"), originalMessageText => new ArgumentException(originalMessageText));
            var end = template.IndexOf('}', index + 1);
            if (end < 0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SE8188598DD582E9A"), originalMessageText => new ArgumentException(originalMessageText));
            var name = template.Substring(index + 1, end - index - 1);
            var replacement = name == "0" || name == "name" ? 1 : name == "world" ? 2 : 0;
            if (replacement == 0) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Format("S672B51492C2D4A4B" , name), originalMessageText => new ArgumentException(originalMessageText));
            if (literal.Length != 0) { parts.Add(new(literal.ToString(), 0)); literal.Clear(); }
            parts.Add(new(string.Empty, replacement));
            index = end;
        }
        if (literal.Length != 0) parts.Add(new(literal.ToString(), 0));
        var literalBytes = 0;
        foreach (var part in parts) literalBytes += StrictUtf8.GetByteCount(part.Literal);
        if (literalBytes > 500) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S339612DEF1BA6A97"), originalMessageText => new ArgumentException(originalMessageText));
        return new(parts.ToArray(), mode, slash);
    }

    internal string Expand(string playerName, string homeWorld)
    {
        RequirePlainText(playerName); RequirePlainText(homeWorld);
        var result = new StringBuilder();
        foreach (var part in parts)
            result.Append(part.Replacement == 1 ? playerName : part.Replacement == 2 ? homeWorld : part.Literal);
        var entry = result.ToString();
        RequirePlainText(entry);
        if (string.IsNullOrWhiteSpace(entry)) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S64D355DEF6EB7DC0"), originalMessageText => new ArgumentException(originalMessageText));
        if (mode == NearbyPlayerEntryMode.SlashCommand && entry[0] != '/') throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S08302E371B29F669"), originalMessageText => new ArgumentException(originalMessageText));
        if (!authoredSlash && entry.TrimStart().StartsWith('/')) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SD42091FFFD5EBE8B"), originalMessageText => new ArgumentException(originalMessageText));
        if (StrictUtf8.GetByteCount(entry) > 500) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S0DCCAF9184B388EF"), originalMessageText => new ArgumentException(originalMessageText));
        return entry;
    }

    private static void RequirePlainText(string value)
    {
        if (value == null) throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("SD981441616A0B9EF"), originalMessageText => new ArgumentException(originalMessageText));
        foreach (var character in value)
            if (char.IsControl(character) || character == '\u2028' || character == '\u2029')
                throw global::XASlave.Localization.UiMessage.CreateException(global::XASlave.Localization.UiMessage.Key("S4A130B57FFEB62CF"), originalMessageText => new ArgumentException(originalMessageText));
        _ = StrictUtf8.GetByteCount(value);
    }
}
