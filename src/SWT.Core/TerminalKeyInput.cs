namespace SWT.Core;

[Flags]
public enum TerminalKeyModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Control = 4
}

public enum TerminalKey
{
    Up,
    Down,
    Right,
    Left,
    Home,
    End,
    Insert,
    Delete,
    PageUp,
    PageDown,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12
}

public static class TerminalKeyInput
{
    public static string? Encode(TerminalKey key, TerminalKeyModifiers modifiers, bool applicationCursorKeys)
    {
        var modifier = 1
            + (modifiers.HasFlag(TerminalKeyModifiers.Shift) ? 1 : 0)
            + (modifiers.HasFlag(TerminalKeyModifiers.Alt) ? 2 : 0)
            + (modifiers.HasFlag(TerminalKeyModifiers.Control) ? 4 : 0);
        var modified = modifier != 1;

        var final = key switch
        {
            TerminalKey.Up => 'A',
            TerminalKey.Down => 'B',
            TerminalKey.Right => 'C',
            TerminalKey.Left => 'D',
            TerminalKey.Home => 'H',
            TerminalKey.End => 'F',
            _ => '\0'
        };
        if (final != '\0')
        {
            if (modified) return $"\x1b[1;{modifier}{final}";
            if (key is >= TerminalKey.Up and <= TerminalKey.Left && applicationCursorKeys)
                return $"\x1bO{final}";
            return $"\x1b[{final}";
        }

        var functionFinal = key switch
        {
            TerminalKey.F1 => 'P',
            TerminalKey.F2 => 'Q',
            TerminalKey.F3 => 'R',
            TerminalKey.F4 => 'S',
            _ => '\0'
        };
        if (functionFinal != '\0')
            return modified ? $"\x1b[1;{modifier}{functionFinal}" : $"\x1bO{functionFinal}";

        var tildeCode = key switch
        {
            TerminalKey.Insert => 2,
            TerminalKey.Delete => 3,
            TerminalKey.PageUp => 5,
            TerminalKey.PageDown => 6,
            TerminalKey.F5 => 15,
            TerminalKey.F6 => 17,
            TerminalKey.F7 => 18,
            TerminalKey.F8 => 19,
            TerminalKey.F9 => 20,
            TerminalKey.F10 => 21,
            TerminalKey.F11 => 23,
            TerminalKey.F12 => 24,
            _ => 0
        };
        if (tildeCode == 0) return null;
        return modified ? $"\x1b[{tildeCode};{modifier}~" : $"\x1b[{tildeCode}~";
    }
}
