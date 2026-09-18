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
            if (block == null) throw new ArgumentException("Command text cannot be null.");
            foreach (var line in block.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var entry = line.Trim();
                if (entry.Length == 0) continue;
                entries.Add(entry);
                if (entries.Count > 15) throw new ArgumentException("A rule may contain at most 15 command entries.");
            }
        }
        return entries.ToArray();
    }

    internal static NearbyPlayerCommandTemplate Parse(string template, NearbyPlayerEntryMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Unknown chat-entry mode.");
        RequirePlainText(template);
        template = template.Trim();
        if (template.Length == 0) throw new ArgumentException("Command entry is empty.");
        var slash = template[0] == '/';
        if (mode == NearbyPlayerEntryMode.SlashCommand && !slash) throw new ArgumentException("Slash-command mode requires a leading slash.");
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
            if (character == '}') throw new ArgumentException("Unbalanced closing template brace.");
            if (commandHead) throw new ArgumentException("A command name must be authored literally, not supplied by a player placeholder.");
            var end = template.IndexOf('}', index + 1);
            if (end < 0) throw new ArgumentException("Unbalanced opening template brace.");
            var name = template.Substring(index + 1, end - index - 1);
            var replacement = name == "0" || name == "name" ? 1 : name == "world" ? 2 : 0;
            if (replacement == 0) throw new ArgumentException("Unknown template placeholder: " + name);
            if (literal.Length != 0) { parts.Add(new(literal.ToString(), 0)); literal.Clear(); }
            parts.Add(new(string.Empty, replacement));
            index = end;
        }
        if (literal.Length != 0) parts.Add(new(literal.ToString(), 0));
        var literalBytes = 0;
        foreach (var part in parts) literalBytes += StrictUtf8.GetByteCount(part.Literal);
        if (literalBytes > 500) throw new ArgumentException("Command literals exceed 500 UTF-8 bytes.");
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
        if (string.IsNullOrWhiteSpace(entry)) throw new ArgumentException("Expanded entry is empty.");
        if (mode == NearbyPlayerEntryMode.SlashCommand && entry[0] != '/') throw new ArgumentException("Expanded entry is not a slash command.");
        if (!authoredSlash && entry.TrimStart().StartsWith('/')) throw new ArgumentException("Player text cannot introduce a command prefix.");
        if (StrictUtf8.GetByteCount(entry) > 500) throw new ArgumentException("Expanded entry exceeds 500 UTF-8 bytes.");
        return entry;
    }

    private static void RequirePlainText(string value)
    {
        if (value == null) throw new ArgumentException("Text cannot be null.");
        foreach (var character in value)
            if (char.IsControl(character) || character == '\u2028' || character == '\u2029')
                throw new ArgumentException("Text contains a control character or line separator.");
        _ = StrictUtf8.GetByteCount(value);
    }
}
