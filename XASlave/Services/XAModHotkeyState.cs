using System;
using XASlave.Data;

namespace XASlave.Services;

/// <summary>One primary key's release latch. Modifier changes cannot create another primary-key edge.</summary>
internal sealed class XAModHotkeyState
{
    private bool armed;
    private bool wasDown;

    internal bool Observe(bool pressed, bool inputAllowed)
    {
        if (!inputAllowed)
        {
            armed = false;
            wasDown = pressed;
            return false;
        }
        if (!pressed)
        {
            armed = true;
            wasDown = false;
            return false;
        }
        if (wasDown) return false;
        wasDown = true;
        if (!armed) return false;
        armed = false;
        return true;
    }

    internal void Suppress()
    {
        armed = false;
        wasDown = false;
    }

    internal static bool IsSupportedPrimaryKey(int key)
        => (key >= 0x30 && key <= 0x39)
            || (key >= 0x41 && key <= 0x5A)
            || (key >= 0x60 && key <= 0x69)
            || (key >= 0x70 && key <= 0x7B)
            || (key >= 0x21 && key <= 0x28)
            || key == 0x2D || key == 0x2E
            || key == 0x6A || key == 0x6B || key == 0x6D || key == 0x6E || key == 0x6F;

    internal static bool IsValidModifiers(XAModHotkeyModifiers modifiers)
        => ((int)modifiers & ~7) == 0;

    internal static bool MatchesModifiers(XAModHotkeyModifiers required, XAModHotkeyModifiers held)
        => IsValidModifiers(required) && IsValidModifiers(held) && required == held;

    internal static bool RequiresCtrlShift(string key)
        => string.Equals(key, "auto-unlock-expert-delivery", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "quick-return", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "instant-logout", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "instant-teleport", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "moveable-after-death", StringComparison.OrdinalIgnoreCase);

    internal static bool CanApply(string key, bool enabled, XAModHotkeyModifiers held)
        => !enabled || !RequiresCtrlShift(key)
            || (IsValidModifiers(held)
                && (held & (XAModHotkeyModifiers.Control | XAModHotkeyModifiers.Shift))
                    == (XAModHotkeyModifiers.Control | XAModHotkeyModifiers.Shift));
}
