using System;

namespace XASlave.Data;

[Flags]
public enum XAModHotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
}

/// <summary>A keyboard chord targets the canonical XA Mod key, never its translated label.</summary>
[Serializable]
public sealed class XAModHotkeyBinding
{
    public string ModKey { get; set; } = string.Empty;
    public int VirtualKey { get; set; }
    public XAModHotkeyModifiers Modifiers { get; set; }
    public bool Enabled { get; set; } = true;

    public XAModHotkeyBinding Clone() => new()
    {
        ModKey = ModKey,
        VirtualKey = VirtualKey,
        Modifiers = Modifiers,
        Enabled = Enabled,
    };
}
