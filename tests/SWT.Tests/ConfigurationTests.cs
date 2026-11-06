using SWT.Core;
using Xunit;

namespace SWT.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void DefaultDocumentMatchesOmittedDefaults()
    {
        var defaults = Configuration.Parse("");
        var document = Configuration.Parse(Configuration.DefaultToml);
        Assert.Equal(defaults.Appearance with { Ansi = document.Appearance.Ansi }, document.Appearance);
        Assert.Equal(16, document.Appearance.Ansi.Length);
        Assert.Empty(document.Shell.Arguments);
        Assert.Equal(defaults.Terminal, document.Terminal);
        Assert.Equal(10000, document.Terminal.ScrollbackLines);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100000)]
    public void ScrollbackAcceptsEndpoints(int lines) =>
        Assert.Equal(lines, Configuration.Parse($"[terminal]\nscrollback_lines = {lines}").Terminal.ScrollbackLines);

    [Theory]
    [InlineData(0)]
    [InlineData(.5)]
    [InlineData(1)]
    public void OpacityAcceptsEndpoints(double opacity)
    {
        var config = Configuration.Parse($"[window]\nbackground_opacity = {opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        Assert.Equal(opacity, config.Appearance.BackgroundOpacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(32)]
    public void CornerRadiusAcceptsRange(double radius) =>
        Assert.Equal(radius, Configuration.Parse($"[window]\ncorner_radius = {radius}").Appearance.CornerRadius);

    [Theory]
    [InlineData("[window]\nbackground_opacity = -0.01", "window.background_opacity")]
    [InlineData("[window]\nbackground_opacity = 1.01", "window.background_opacity")]
    [InlineData("[window]\nbackground_opacity = nan", "window.background_opacity")]
    [InlineData("[window]\nbackground_opacity = \"0.5\"", "window.background_opacity")]
    [InlineData("[window]\ncorner_radius = -0.1", "window.corner_radius")]
    [InlineData("[window]\ncorner_radius = 32.1", "window.corner_radius")]
    [InlineData("[window]\ncorner_radius = \"4\"", "window.corner_radius")]
    [InlineData("[window]\ncolumns = 0", "window.columns")]
    [InlineData("[window]\nrows = 201", "window.rows")]
    [InlineData("[window]\ndecorations = 1", "window.decorations")]
    [InlineData("[font]\nsize = 100", "font.size")]
    [InlineData("[font]\nfamily = \"\"", "font.family")]
    [InlineData("[colors]\nforeground = \"red\"", "colors.foreground")]
    [InlineData("[colors]\nbackground = \"#GG0000\"", "colors.background")]
    [InlineData("[colors]\nansi = [\"#000000\"]", "colors.ansi")]
    [InlineData("[shell]\narguments = [1]", "arguments")]
    [InlineData("[terminal]\nscrollback_lines = -1", "terminal.scrollback_lines")]
    [InlineData("[terminal]\nscrollback_lines = 100001", "terminal.scrollback_lines")]
    [InlineData("[terminal]\nhistory = 5", "terminal.history")]
    [InlineData("[window]\ndecoration = true", "window.decoration")]
    [InlineData("version = 2", "version")]
    [InlineData("font = false", "font")]
    public void InvalidSettingsReportKey(string text, string key) =>
        Assert.Contains(key, Assert.Throws<ConfigurationException>(() => Configuration.Parse(text)).Message);

    [Fact]
    public void BrokenTomlProducesDiagnostic() =>
        Assert.Throws<ConfigurationException>(() => Configuration.Parse("[window"));
}
