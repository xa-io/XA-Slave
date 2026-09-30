using System.Collections.Generic;

namespace XASlave.Data;

/// <summary>Clean Chat choices; the separate master switch defaults to off.</summary>
public sealed class CleanChatSettings
{
    public bool HideWelcome { get; set; } = true;
    public bool HideEvents { get; set; } = true;
    public bool HidePhishing { get; set; } = true;
    public bool HideRmt { get; set; } = true;
    public bool RegexEnabled { get; set; } = false;
    public string RegexRules { get; set; } = string.Empty;
    public HashSet<ushort> RegexChannels { get; set; } = new() { 3, 10, 11, 13, 30, 57 };
}
