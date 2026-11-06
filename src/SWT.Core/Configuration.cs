using System.Collections.Immutable;
using System.Globalization;
using Tomlyn;
using Tomlyn.Model;

namespace SWT.Core;

public sealed record Appearance(
    bool Decorations, double CornerRadius, double BackgroundOpacity, int Columns, int Rows,
    string FontFamily, double FontSize, string Foreground, string Background,
    string Cursor, ImmutableArray<string> Ansi);

public sealed record ShellSettings(string Executable, ImmutableArray<string> Arguments, string WorkingDirectory);

public sealed record TerminalSettings(int ScrollbackLines);

public sealed record Configuration(Appearance Appearance, ShellSettings Shell, TerminalSettings Terminal)
{
    public const string DefaultToml = """
        version = 1

        [window]
        decorations = true
        # Radius of the app window corners; 0 is square. Max 32.
        corner_radius = 0.0
        # Background only; text remains opaque.
        background_opacity = 0.90
        columns = 120
        rows = 32

        [font]
        family = "Cascadia Mono"
        size = 14.0

        [terminal]
        # Lines kept above the screen; 0 disables scrollback. Max 100000.
        scrollback_lines = 10000

        [shell]
        # Empty selects pwsh.exe, then Windows PowerShell.
        executable = ""
        arguments = []
        # Empty selects your home directory.
        working_directory = ""

        [colors]
        foreground = "#D8DEE9"
        background = "#20242C"
        cursor = "#ECEFF4"
        ansi = [
          "#20242C", "#BF616A", "#A3BE8C", "#EBCB8B",
          "#81A1C1", "#B48EAD", "#88C0D0", "#D8DEE9",
          "#5C6370", "#D57780", "#B1D196", "#F5DB9C",
          "#93B5D6", "#C79AC0", "#9DD6DF", "#ECEFF4"
        ]
        """;

    public static Configuration Parse(string text)
    {
        var syntax = Toml.Parse(text);
        if (syntax.HasErrors)
            throw new ConfigurationException(syntax.Diagnostics.ToString());
        var root = syntax.ToModel();
        CheckKeys(root, "", "version", "window", "font", "terminal", "shell", "colors");
        if (Integer(root, "version", 1, 1, 1) != 1)
            throw new ConfigurationException("version: only version 1 is supported.");
        var window = Table(root, "window");
        var font = Table(root, "font");
        var terminal = Table(root, "terminal");
        var shell = Table(root, "shell");
        var colors = Table(root, "colors");
        CheckKeys(window, "window.", "decorations", "corner_radius", "background_opacity", "columns", "rows");
        CheckKeys(font, "font.", "family", "size");
        CheckKeys(terminal, "terminal.", "scrollback_lines");
        CheckKeys(shell, "shell.", "executable", "arguments", "working_directory");
        CheckKeys(colors, "colors.", "foreground", "background", "cursor", "ansi");
        var defaults = new[] {
            "#20242C", "#BF616A", "#A3BE8C", "#EBCB8B",
            "#81A1C1", "#B48EAD", "#88C0D0", "#D8DEE9",
            "#5C6370", "#D57780", "#B1D196", "#F5DB9C",
            "#93B5D6", "#C79AC0", "#9DD6DF", "#ECEFF4" };
        var ansi = Strings(colors, "ansi", defaults);
        if (ansi.Length != 16)
            throw new ConfigurationException("colors.ansi: exactly 16 colors are required.");
        foreach (var color in ansi) ValidateColor("colors.ansi", color);
        return new Configuration(
            new Appearance(
                Boolean(window, "decorations", true),
                Number(window, "corner_radius", 0, 0, 32, "window."),
                Number(window, "background_opacity", .9, 0, 1, "window."),
                Integer(window, "columns", 120, 20, 500, "window."),
                Integer(window, "rows", 32, 5, 200, "window."),
                Text(font, "family", "Cascadia Mono", "font.", allowEmpty: false),
                Number(font, "size", 14, 6, 72, "font."),
                Color(colors, "foreground", "#D8DEE9"),
                Color(colors, "background", "#20242C"),
                Color(colors, "cursor", "#ECEFF4"), ansi),
            new ShellSettings(
                Text(shell, "executable", "", "shell."),
                Strings(shell, "arguments", []),
                Text(shell, "working_directory", "", "shell.")),
            new TerminalSettings(Integer(terminal, "scrollback_lines", 10000, 0, TerminalScreen.MaxScrollbackLines, "terminal.")));
    }

    private static TomlTable Table(TomlTable root, string key) =>
        !root.TryGetValue(key, out var value) ? new TomlTable() :
        value as TomlTable ?? throw new ConfigurationException($"{key}: expected a table.");

    private static void CheckKeys(TomlTable table, string prefix, params string[] keys)
    {
        foreach (var key in table.Keys)
            if (!keys.Contains(key, StringComparer.Ordinal))
                throw new ConfigurationException($"{prefix}{key}: unknown setting.");
    }

    private static string Text(TomlTable table, string key, string fallback, string prefix, bool allowEmpty = true)
    {
        if (!table.TryGetValue(key, out var value)) return fallback;
        if (value is not string text || (!allowEmpty && string.IsNullOrWhiteSpace(text)) || text.Contains('\0'))
            throw new ConfigurationException($"{prefix}{key}: expected {(allowEmpty ? "a" : "a nonempty")} string without NUL.");
        return text;
    }

    private static bool Boolean(TomlTable table, string key, bool fallback) =>
        !table.TryGetValue(key, out var value) ? fallback :
        value is bool b ? b : throw new ConfigurationException($"window.{key}: expected a boolean.");

    private static int Integer(TomlTable table, string key, int fallback, int min, int max, string prefix = "")
    {
        if (!table.TryGetValue(key, out var value)) return fallback;
        if (value is not long number || number < min || number > max)
            throw new ConfigurationException($"{prefix}{key}: expected an integer from {min} to {max}.");
        return (int)number;
    }

    private static double Number(TomlTable table, string key, double fallback, double min, double max, string prefix)
    {
        if (!table.TryGetValue(key, out var value)) return fallback;
        var number = value switch { double d => d, long l => (double)l, _ => double.NaN };
        if (!double.IsFinite(number) || number < min || number > max)
            throw new ConfigurationException($"{prefix}{key}: expected a number from {min} to {max}.");
        return number;
    }

    private static ImmutableArray<string> Strings(TomlTable table, string key, string[] fallback)
    {
        if (!table.TryGetValue(key, out var value)) return [.. fallback];
        if (value is not TomlArray array || array.Any(item => item is not string s || s.Contains('\0')))
            throw new ConfigurationException($"{key}: expected an array of strings without NUL.");
        return [.. array.Cast<string>()];
    }

    private static string Color(TomlTable table, string key, string fallback)
    {
        var color = Text(table, key, fallback, "colors.");
        ValidateColor($"colors.{key}", color);
        return color;
    }

    private static void ValidateColor(string key, string color)
    {
        if (color.Length != 7 || color[0] != '#' ||
            !uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            throw new ConfigurationException($"{key}: expected #RRGGBB.");
    }
}

public sealed class ConfigurationException(string message) : Exception(message);
