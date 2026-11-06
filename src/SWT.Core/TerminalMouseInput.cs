namespace SWT.Core;

[Flags]
public enum TerminalMouseModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Control = 4
}

public static class TerminalMouseInput
{
    public static string? Encode(
        int button,
        int column,
        int row,
        bool release,
        bool motion,
        TerminalMouseModifiers modifiers,
        bool sgr)
    {
        if (button is not (>= 0 and <= 2 or 3 or 64 or 65) ||
            button == 3 && (!motion || release))
            throw new ArgumentOutOfRangeException(nameof(button));
        ArgumentOutOfRangeException.ThrowIfLessThan(column, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(row, 1);

        var modifierCode = (modifiers.HasFlag(TerminalMouseModifiers.Shift) ? 4 : 0)
            + (modifiers.HasFlag(TerminalMouseModifiers.Alt) ? 8 : 0)
            + (modifiers.HasFlag(TerminalMouseModifiers.Control) ? 16 : 0);
        var code = button + (motion ? 32 : 0) + modifierCode;
        if (sgr)
            return $"\x1b[<{code};{column};{row}{(release ? 'm' : 'M')}";

        var legacyButton = (release ? 3 : code) + (release ? modifierCode : 0);
        if (legacyButton > 95 || column > 95 || row > 95) return null;
        return $"\x1b[M{(char)(legacyButton + 32)}{(char)(column + 32)}{(char)(row + 32)}";
    }
}
