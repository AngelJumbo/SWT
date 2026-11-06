using System.Runtime.ExceptionServices;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SWT.Core;
using SWT.Desktop;
using Xunit;
using Xunit.Abstractions;

namespace SWT.Tests;

public sealed class RenderingTests(ITestOutputHelper output)
{
    [Fact]
    public void OscTitlesUpdateWindowAndCustomChromeWithoutReplacingSession()
    {
        OnSta(() =>
        {
            var window = new MainWindow(Configuration.Parse(""), "", smoke: true);
            var field = typeof(MainWindow).GetField("_screen",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var screen = Assert.IsType<TerminalScreen>(field!.GetValue(window));
            var root = Assert.IsType<Grid>(window.Content);
            var frame = Assert.IsType<Border>(root.Children[0]);
            var content = Assert.IsType<Grid>(frame.Child);
            var title = Assert.IsType<Border>(content.Children[0]);
            var chrome = Assert.IsType<Grid>(title.Child);
            var titleContent = Assert.IsType<DockPanel>(chrome.Children[0]);
            var titleText = Assert.IsType<TextBlock>(titleContent.Children[1]);
            foreach (var text in new[] { "Editor", new string('x', 1024), "" })
            {
                screen.Feed(Encoding.UTF8.GetBytes($"\x1b]2;{text}\a"));
                Assert.Equal(text.Length == 0 ? "SWT" : text, window.Title);
                Assert.Equal(window.Title, titleText.Text);
                Assert.Same(screen, field.GetValue(window));
            }
        });
    }

    [Fact]
    public void SearchToolbarUsesCompactIconsAndFitsMinimumWindowWidth()
    {
        OnSta(() =>
        {
            var window = new MainWindow(Configuration.Parse(""), "", smoke: true);
            var root = Assert.IsType<Grid>(window.Content);
            var toolbar = Assert.IsType<Border>(root.Children[2]);
            toolbar.Visibility = Visibility.Visible;
            var controls = Assert.IsType<Grid>(toolbar.Child);
            var input = Assert.IsType<TextBox>(controls.Children[0]);
            var actions = Assert.IsType<StackPanel>(controls.Children[1]);
            Assert.Equal(4, actions.Children.Count);
            Assert.Single(actions.Children.OfType<CheckBox>());
            foreach (var button in actions.Children.OfType<Button>())
            {
                Assert.IsType<System.Windows.Shapes.Path>(button.Content);
                Assert.NotNull(button.ToolTip);
                Assert.NotNull(button.Template);
            }
            foreach (var width in new[] { 320, 800 })
            {
                root.Measure(new Size(width, 300));
                root.Arrange(new Rect(0, 0, width, 300));
                root.UpdateLayout();
                Assert.InRange(toolbar.ActualWidth, 200, 420);
                Assert.True(input.ActualWidth > 100);
                Assert.True(actions.ActualWidth + input.ActualWidth <= controls.ActualWidth);
                var bitmap = new RenderTargetBitmap(width, 300, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(root);
            }
        });
    }

    [Fact]
    public void SearchToolbarFollowsTitleVisibilityWithoutOverlappingDecorations()
    {
        OnSta(() =>
        {
            var window = new MainWindow(Configuration.Parse(""), "", smoke: true);
            var root = Assert.IsType<Grid>(window.Content);
            var frame = Assert.IsType<Border>(root.Children[0]);
            var content = Assert.IsType<Grid>(frame.Child);
            var title = Assert.IsType<Border>(content.Children[0]);
            var toolbar = Assert.IsType<Border>(root.Children[2]);
            toolbar.Visibility = Visibility.Visible;
            foreach (var visibility in new[] { Visibility.Collapsed, Visibility.Visible, Visibility.Collapsed })
            {
                title.Visibility = visibility;
                root.Measure(new Size(800, 300));
                root.Arrange(new Rect(0, 0, 800, 300));
                root.UpdateLayout();
                var top = toolbar.TranslatePoint(new Point(), root).Y;
                if (visibility == Visibility.Visible)
                {
                    var titleBottom = title.TranslatePoint(new Point(0, title.ActualHeight), root).Y;
                    Assert.True(top >= titleBottom + 6);
                    Assert.Equal(title.Height + 12, toolbar.Margin.Top);
                }
                else
                {
                    Assert.Equal(12, toolbar.Margin.Top);
                    Assert.Equal(12, top);
                }
            }
        });
    }

    [Fact]
    public void SearchNavigatesHistoryAndLiveRowsInBothDirectionsAndWraps()
    {
        OnSta(() =>
        {
            var screen = new TerminalScreen(20, 2, 100);
            screen.Feed(Encoding.UTF8.GetBytes("match old\r\nother\r\nMATCH middle\r\nother\r\nmatch new"));
            var view = new TerminalView(screen, Configuration.Parse("").Appearance);
            Assert.True(view.FindText("match"));
            Assert.Equal(0, view.ScrollOffset);
            Assert.True(view.FindText("match", previous: true));
            Assert.Equal(1, view.ScrollOffset);
            Assert.True(view.FindText("match", previous: true));
            Assert.Equal(3, view.ScrollOffset);
            Assert.True(view.FindText("match", previous: true));
            Assert.Equal(0, view.ScrollOffset);
            Assert.True(view.FindText("match"));
            Assert.Equal(3, view.ScrollOffset);
            Assert.True(view.FindText("match"));
            Assert.Equal(1, view.ScrollOffset);
            Assert.Null(view.SelectedText);
            Assert.False(view.FindText("missing"));
            Assert.Equal(1, view.ScrollOffset);
            Assert.False(view.FindText(""));
        });
    }

    [Fact]
    public void SearchHandlesOutputEvictionClearResizeAndAlternateScreen()
    {
        OnSta(() =>
        {
            var screen = new TerminalScreen(12, 2, 2);
            screen.Feed(Encoding.UTF8.GetBytes("needle\r\nother\r\nlive"));
            var view = new TerminalView(screen, Configuration.Parse("").Appearance);
            Assert.True(view.FindText("needle"));
            screen.Feed(Encoding.UTF8.GetBytes("\r\nnext"));
            view.ScreenChanged();
            Assert.True(view.FindText("needle"));
            Assert.Equal(2, view.ScrollOffset);
            screen.Feed(Encoding.UTF8.GetBytes("\r\nlast"));
            view.ScreenChanged();
            Assert.False(view.FindText("needle"));
            Assert.True(view.FindText("live"));
            screen.Feed(Encoding.UTF8.GetBytes("\x1b[?1049h"));
            Assert.False(view.FindText("live"));
            screen.Feed(Encoding.UTF8.GetBytes("alternate"));
            Assert.True(view.FindText("alternate"));
            screen.Feed(Encoding.UTF8.GetBytes("\x1b[?1049l\x1b[3J"));
            Assert.False(view.FindText("live"));
            screen.Resize(6, 2);
            Assert.True(view.FindText("last"));
            view.ClearSearch();
            Assert.True(view.FindText("last"));
        });
    }

    [Fact]
    public void SearchIsLiteralAndMatchesAcrossHardBreaksWithoutRegex()
    {
        OnSta(() =>
        {
            var screen = new TerminalScreen(12, 2, 10);
            screen.Feed(Encoding.UTF8.GetBytes("a.*b\r\nneedle\r\nnext"));
            var view = new TerminalView(screen, Configuration.Parse("").Appearance);
            Assert.True(view.FindText("a.*b"));
            Assert.Equal(1, view.ScrollOffset);
            Assert.False(view.FindText("a.+b"));
            Assert.True(view.FindText("needle\nnext"));
            Assert.True(view.FindText("needle\\nnext"));
            Assert.True(view.FindText("NEXT"));
            Assert.Equal(0, view.ScrollOffset);
            Assert.True(view.FindText("a.*b"));
            Assert.Equal(1, view.ScrollOffset);
            view.ClearSearch();
            Assert.Equal(1, view.ScrollOffset);
            screen.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[Haaaa"));
            Assert.True(view.FindText("aaa"));
            Assert.True(view.FindText("aaa", previous: true));
        });
    }

    [Fact]
    public void SearchSupportsRegexSoftWrapsAndNewlinesAcrossHistoryBoundary()
    {
        OnSta(() =>
        {
            var screen = new TerminalScreen(8, 2, 20);
            screen.Feed(Encoding.UTF8.GetBytes("abcdefghijk\r\nnext 42"));
            var view = new TerminalView(screen, Configuration.Parse("").Appearance);
            Assert.True(view.FindText("ghijk"));
            Assert.Equal(1, view.ScrollOffset);
            Assert.True(view.FindText("ijk\\nnext"));
            Assert.Equal(0, view.ScrollOffset);
            Assert.True(view.FindText(@"ghijk\nnext \d+", regex: true));
            Assert.Equal(1, view.ScrollOffset);
            Assert.True(view.FindText(@"^next \d+$", regex: true));
            Assert.False(view.FindText(@"next \d+", regex: false));
            Assert.True(view.FindText("abcdefghijk"));
            screen.Resize(5, 2);
            Assert.True(view.FindText("abcdefghijk"));
            screen.Resize(16, 2);
            Assert.True(view.FindText(@"abcdefghijk\nnext \d+", regex: true));
        });
    }

    [Fact]
    public void SearchReportsInvalidOrPathologicalRegexAndRecovers()
    {
        OnSta(() =>
        {
            var screen = new TerminalScreen(1000, 2, 10);
            screen.Feed(Encoding.UTF8.GetBytes(new string('a', 900) + "!"));
            var view = new TerminalView(screen, Configuration.Parse("").Appearance);
            Assert.False(view.FindText("[", regex: true));
            Assert.StartsWith("Invalid regex:", view.SearchError);
            Assert.False(view.FindText("(a+)+$", regex: true));
            Assert.Contains("timed out", view.SearchError);
            Assert.False(view.FindText("^", regex: true));
            Assert.Null(view.SearchError);
            Assert.True(view.FindText("!"));
            Assert.Null(view.SearchError);
        });
    }

    [Fact]
    public void SearchReportsSnapshotLimitWithoutReturningPartialMatches()
    {
        OnSta(() =>
        {
            var screen = new TerminalScreen(1000, 2, 200);
            var cell = "a" + new string('\u0301', 63);
            var row = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(cell, 1000)) + "\r\n");
            for (var i = 0; i < 132; i++) screen.Feed(row);
            var view = new TerminalView(screen, Configuration.Parse("").Appearance);
            Assert.False(view.FindText("a"));
            Assert.Contains("exceeds 8 Mi", view.SearchError);
            screen.ClearScrollback();
            screen.Feed(Encoding.UTF8.GetBytes("\x1b[2J\x1b[Hrecovered"));
            Assert.True(view.FindText("recovered"));
            Assert.Null(view.SearchError);
        });
    }

    [Fact]
    public void ResizeInvalidatesPhysicalSelectionAndSearchAnchors()
    {
        OnSta(() =>
        {
            var screen = new TerminalScreen(8, 3, 20);
            screen.Feed(Encoding.UTF8.GetBytes("abcdefghijklmnop"));
            var view = new TerminalView(screen, Configuration.Parse("").Appearance);
            view.BeginSelection(new Point(8, 4));
            view.EndSelection(new Point(8 + view.CellWidth, 4));
            Assert.NotNull(view.SelectedText);
            screen.Resize(6, 3);
            _ = view.ScrollOffset;
            Assert.Null(view.SelectedText);
            Assert.True(view.FindText("ghijk"));
            screen.Resize(12, 3);
            Assert.True(view.FindText("ghijk"));
            Assert.Null(view.SearchError);
        });
    }

    [Fact]
    public void SelectionOmitsSoftWrapBreaksButRetainsExplicitNewlines()
    {
        OnSta(() =>
        {
            var screen = new TerminalScreen(6, 3);
            screen.Feed(Encoding.UTF8.GetBytes("abcdefgh\r\nnext"));
            var view = new TerminalView(screen, Configuration.Parse("").Appearance);
            Point Cell(int row, int column) =>
                new(8 + (column + .2) * view.CellWidth, 4 + (row + .2) * view.CellHeight);
            view.BeginSelection(Cell(0, 0));
            view.EndSelection(Cell(2, 3));
            Assert.Equal($"abcdefgh{Environment.NewLine}next", view.SelectedText);
        });
    }

    [Fact]
    public void SearchHighlightsCompleteUnicodeCellsAndMultipleMatchesWithoutChangingText()
    {
        OnSta(() =>
        {
            var screen = new TerminalScreen(20, 2);
            screen.Feed(Encoding.UTF8.GetBytes("\u4e2de\u0301 \u4e2de\u0301"));
            var appearance = Configuration.Parse("").Appearance with { Background = "#000000", BackgroundOpacity = 1 };
            var view = new TerminalView(screen, appearance);
            var size = new Size(20 * view.CellWidth + 16, 2 * view.CellHeight + 8);
            view.Measure(size);
            view.Arrange(new Rect(size));
            byte[] Pixels()
            {
                view.InvalidateVisual();
                view.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width),
                    (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(view);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                return pixels;
            }
            var original = screen.Line(0);
            var plain = Pixels();
            Assert.True(view.FindText("\u4e2de\u0301"));
            var last = Pixels();
            Assert.False(plain.SequenceEqual(last));
            Assert.True(view.FindText("\u4e2de\u0301", previous: true));
            Assert.False(last.SequenceEqual(Pixels()));
            view.ClearSearch();
            Assert.Equal(plain, Pixels());
            Assert.Equal(original, screen.Line(0));
        });
    }

    [Fact]
    public void UnicodeSelectionCopiesCompleteCellsAndRendersWideAndCombinedText()
    {
        OnSta(() =>
        {
            var screen = new TerminalScreen(10, 2);
            screen.Feed(Encoding.UTF8.GetBytes("e\u0301\u4e2d\u6587B"));
            var view = new TerminalView(screen, Configuration.Parse("").Appearance);
            var size = new Size(10 * view.CellWidth + 16, 2 * view.CellHeight + 8);
            view.Measure(size);
            view.Arrange(new Rect(size));
            Point At(int column) => new(8 + (column + .2) * view.CellWidth, 4 + .2 * view.CellHeight);
            view.BeginSelection(At(0));
            view.UpdateSelection(At(2));
            view.EndSelection(At(2));
            Assert.Equal("e\u0301\u4e2d", view.SelectedText);
            view.BeginSelection(At(2));
            view.UpdateSelection(At(3));
            view.EndSelection(At(3));
            Assert.Equal("\u4e2d\u6587", view.SelectedText);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width),
                (int)Math.Ceiling(size.Height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            Assert.Equal(2, view.LinesBuilt);
        });
    }

    [Fact]
    public void ImeCompositionPreviewRendersWithoutChangingTerminalCells()
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with
            {
                FontFamily = "Consolas", Foreground = "#FFFFFF",
                Background = "#000000", BackgroundOpacity = 1
            };
            var screen = new TerminalScreen(10, 2);
            var view = new TerminalView(screen, appearance);
            var size = new Size(10 * view.CellWidth + 16, 2 * view.CellHeight + 8);
            view.Measure(size);
            view.Arrange(new Rect(size));

            static int BrightPixels(TerminalView terminal, Size size)
            {
                terminal.InvalidateVisual();
                terminal.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(terminal);
                var pixels = new byte[(int)size.Width * (int)size.Height * 4];
                bitmap.CopyPixels(pixels, (int)size.Width * 4, 0);
                return Enumerable.Range(0, pixels.Length / 4)
                    .Count(i => pixels[i * 4] > 200 && pixels[i * 4 + 1] > 200 && pixels[i * 4 + 2] > 200);
            }

            var emptyPixels = BrightPixels(view, size);
            view.SetCompositionText("\u306B\u307B\u3093");
            Assert.Equal("          ", screen.Line(0));
            Assert.True(BrightPixels(view, size) > emptyPixels);

            view.SetCompositionText("");
            Assert.Equal(emptyPixels, BrightPixels(view, size));
        });
    }

    [Fact]
    public void StartupDiagnosticsRecordWindowGridAndConPtyDimensions()
    {
        OnSta(() =>
        {
            using var log = new DiagnosticLog();
            try
            {
                var configuration = Configuration.Parse("");
                var window = new MainWindow(configuration, "", smoke: true, log);
                var root = Assert.IsType<Grid>(window.Content);
                var frame = Assert.IsType<Border>(root.Children[0]);
                var grid = Assert.IsType<Grid>(frame.Child);
                grid.Measure(new Size(800, 600));
                root.Measure(new Size(800, 600));
                root.Arrange(new Rect(0, 0, 800, 600));
                window.ResizeTerminal();
                log.Dispose();
                var entries = File.ReadAllLines(log.Path);
                Assert.NotEmpty(entries);
                using var last = JsonDocument.Parse(entries[^1]);
                Assert.Equal("resize-applied", last.RootElement.GetProperty("reason").GetString());
                Assert.Equal(Assert.IsType<TerminalView>(grid.Children[1]).Screen.Rows,
                    last.RootElement.GetProperty("screen").GetProperty("rows").GetInt32());
                Assert.Equal(JsonValueKind.Null,
                    last.RootElement.GetProperty("conpty").GetProperty("rows").ValueKind);
            }
            finally
            {
                log.Dispose();
                File.Delete(log.Path);
            }
        });
    }

    [Fact]
    public void MouseSelectionCopiesRowsInEitherDirectionAndHighlightsCells()
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with { FontFamily = "Consolas" };
            var screen = new TerminalScreen(10, 3, 100);
            screen.Feed(Encoding.UTF8.GetBytes("alpha\r\nbeta\r\ngamma"));
            var view = new TerminalView(screen, appearance);
            var surface = BackgroundHost(view, appearance);
            surface.Measure(new Size(200, 80));
            surface.Arrange(new Rect(0, 0, 200, 80));

            Point Cell(int row, int column) =>
                new(8 + (column + .2) * view.CellWidth, 4 + (row + .2) * view.CellHeight);
            view.BeginSelection(Cell(0, 2));
            view.UpdateSelection(Cell(1, 1));
            view.EndSelection(Cell(1, 1));
            Assert.Equal($"pha{Environment.NewLine}be", view.SelectedText);

            view.BeginSelection(Cell(1, 1));
            view.UpdateSelection(Cell(0, 2));
            view.EndSelection(Cell(0, 2));
            Assert.Equal($"pha{Environment.NewLine}be", view.CopyText);

            screen.Feed(Encoding.UTF8.GetBytes("\r\nnext"));
            view.ScreenChanged();
            Assert.Equal(1, view.ScrollOffset);
            Assert.Equal($"pha{Environment.NewLine}be", view.SelectedText);

            view.BeginSelection(Cell(2, 2));
            view.UpdateSelection(Cell(2, 4));
            view.EndSelection(Cell(2, 4));
            Render(view, 200, 80);
            var bitmap = new RenderTargetBitmap(200, 80, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            byte[] Pixel(int x, int y)
            {
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
                return pixel;
            }
            var selected = Pixel((int)(8 + 3.5 * view.CellWidth), (int)(4 + 2.5 * view.CellHeight));
            var unselected = Pixel((int)(8 + 6.5 * view.CellWidth), (int)(4 + 2.5 * view.CellHeight));
            Assert.True(selected[0] > unselected[0] + 20);

            view.ScrollToBottom();
            screen.Feed(Encoding.UTF8.GetBytes("\r\nlive"));
            view.ScreenChanged();
            Assert.Equal(0, view.ScrollOffset);
            Assert.Null(view.SelectedText);
            Assert.Equal("live", screen.GetViewportLine(2, view.ScrollOffset).Text(10).TrimEnd());

            view.ClearSelection();
            Assert.Null(view.SelectedText);
            Assert.Equal($"gamma{Environment.NewLine}next{Environment.NewLine}live", view.CopyText);
        });
    }

    [Fact]
    public void ClearingScreenRemovesPreviouslyRenderedText()
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with { FontFamily = "Consolas" };
            var screen = new TerminalScreen(20, 3, 100);
            screen.Feed(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("MMMMMMMM\r\n", 25))));
            Assert.True(screen.ScrollbackCount > 0);
            var view = new TerminalView(screen, appearance);

            static byte[] RenderPixels(TerminalView terminal, int width, int height)
            {
                terminal.Measure(new Size(width, height));
                terminal.Arrange(new Rect(0, 0, width, height));
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(terminal);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                return pixels;
            }

            var before = RenderPixels(view, 200, 80);
            screen.Feed(Encoding.UTF8.GetBytes("\x1b[H\x1b[2J"));
            Assert.All(Enumerable.Range(0, screen.Rows).SelectMany(y =>
                Enumerable.Range(0, screen.Columns).Select(x => screen[y, x])),
                cell => Assert.Equal(" ", cell.Text));
            view.ScreenChanged();
            view.InvalidateVisual();
            var after = RenderPixels(view, 200, 80);
            static int BrightPixels(byte[] pixels) => Enumerable.Range(0, pixels.Length / 4)
                .Count(i => pixels[i * 4 + 2] > 170 && pixels[i * 4 + 1] > 170 && pixels[i * 4 + 3] > 200);
            Assert.True(BrightPixels(before) > 0);
            Assert.Equal(0, BrightPixels(after));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TerminalFillsWindowBelowOptionalTitleWithoutStatusBar(bool decorations)
    {
        OnSta(() =>
        {
            var configuration = Configuration.Parse("");
            configuration = configuration with
            {
                Appearance = configuration.Appearance with { Decorations = decorations, FontFamily = "Consolas" }
            };
            var window = new MainWindow(configuration, "", smoke: true);
            var root = Assert.IsType<Grid>(window.Content);
            var frame = Assert.IsType<Border>(root.Children[0]);
            var grid = Assert.IsType<Grid>(frame.Child);
            Assert.Equal(2, grid.RowDefinitions.Count);
            Assert.Equal(2, grid.Children.Count);
            var title = Assert.IsType<Border>(grid.Children[0]);
            var terminal = Assert.IsType<TerminalView>(grid.Children[1]);
            grid.Measure(new Size(800, 600));
            root.Measure(new Size(800, 600));
            root.Arrange(new Rect(0, 0, 800, 600));

            var top = terminal.TranslatePoint(new Point(), root).Y;
            Assert.Equal(decorations ? title.Height : 0, top);
            Assert.Equal(600, top + terminal.ActualHeight);
            window.ResizeTerminal();
            Assert.Equal((int)((terminal.ActualWidth - 16) / terminal.CellWidth), terminal.Screen.Columns);
            Assert.Equal((int)((terminal.ActualHeight - 8) / terminal.CellHeight), terminal.Screen.Rows);
        });
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(.5, 128)]
    [InlineData(1, 255)]
    public void AnsiBackgroundUsesConfiguredOpacity(double opacity, int expectedAlpha)
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with { BackgroundOpacity = opacity, FontFamily = "Consolas" };
            var screen = new TerminalScreen(10, 2);
            screen.Feed(Encoding.UTF8.GetBytes("\x1b[41m "));
            var view = new TerminalView(screen, appearance);
            view.Measure(new Size(200, 80));
            view.Arrange(new Rect(0, 0, 200, 80));
            var bitmap = new RenderTargetBitmap(200, 80, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var pixels = new byte[4];
            bitmap.CopyPixels(new Int32Rect(9, 5, 1, 1), pixels, 4, 0);
            // An explicit background replaces, rather than layers over, the default.
            Assert.Equal(expectedAlpha, pixels[3]);
        });
    }

    [Fact]
    public void CellBackgroundsHaveNoSeamsAtScaledDpi()
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with
            {
                BackgroundOpacity = .5, FontFamily = "Consolas"
            };
            var view = new TerminalView(new TerminalScreen(10, 4), appearance);
            var surface = BackgroundHost(view, appearance);
            surface.Measure(new Size(200, 80));
            surface.Arrange(new Rect(0, 0, 200, 80));
            var bitmap = new RenderTargetBitmap(300, 120, 144, 144, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var pixels = new byte[300 * 120 * 4];
            bitmap.CopyPixels(pixels, 300 * 4, 0);

            for (var y = 8; y < 112; y++)
            for (var x = 14; x < 280; x++)
                Assert.Equal(128, pixels[(y * 300 + x) * 4 + 3]);
        });
    }

    [Theory]
    [InlineData(0x2580, 0, 0, 8, 4)]
    [InlineData(0x2581, 0, 7, 8, 8)]
    [InlineData(0x2582, 0, 6, 8, 8)]
    [InlineData(0x2583, 0, 5, 8, 8)]
    [InlineData(0x2584, 0, 4, 8, 8)]
    [InlineData(0x2585, 0, 3, 8, 8)]
    [InlineData(0x2586, 0, 2, 8, 8)]
    [InlineData(0x2587, 0, 1, 8, 8)]
    [InlineData(0x2588, 0, 0, 8, 8)]
    [InlineData(0x2589, 0, 0, 7, 8)]
    [InlineData(0x258A, 0, 0, 6, 8)]
    [InlineData(0x258B, 0, 0, 5, 8)]
    [InlineData(0x258C, 0, 0, 4, 8)]
    [InlineData(0x258D, 0, 0, 3, 8)]
    [InlineData(0x258E, 0, 0, 2, 8)]
    [InlineData(0x258F, 0, 0, 1, 8)]
    [InlineData(0x2590, 4, 0, 8, 8)]
    [InlineData(0x2594, 0, 0, 8, 1)]
    [InlineData(0x2595, 7, 0, 8, 8)]
    public void FractionalBlocksFillExactCellPortions(int codepoint, int left, int top, int right, int bottom) =>
        AssertBlockPixels(codepoint, (x, y) => x >= left && x < right && y >= top && y < bottom);

    [Theory]
    [InlineData(0x2596, false, false, true, false)]
    [InlineData(0x2597, false, false, false, true)]
    [InlineData(0x2598, true, false, false, false)]
    [InlineData(0x2599, true, false, true, true)]
    [InlineData(0x259A, true, false, false, true)]
    [InlineData(0x259B, true, true, true, false)]
    [InlineData(0x259C, true, true, false, true)]
    [InlineData(0x259D, false, true, false, false)]
    [InlineData(0x259E, false, true, true, false)]
    [InlineData(0x259F, false, true, true, true)]
    public void QuadrantBlocksFillCorrectCorners(int codepoint, bool upperLeft, bool upperRight,
        bool lowerLeft, bool lowerRight) =>
        AssertBlockPixels(codepoint, (x, y) => y < 4
            ? (x < 4 ? upperLeft : upperRight)
            : (x < 4 ? lowerLeft : lowerRight));

    private static void AssertBlockPixels(int codepoint, Func<int, int, bool> filled)
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with
            {
                FontFamily = "Consolas", FontSize = 40, Foreground = "#FFFFFF",
                Background = "#000000", BackgroundOpacity = 1
            };
            var screen = new TerminalScreen(4, 1);
            screen.Feed(Encoding.UTF8.GetBytes($"A{(char)codepoint}B"));
            var view = new TerminalView(screen, appearance);
            var size = new Size(4 * view.CellWidth + 16, view.CellHeight + 8);
            var surface = BackgroundHost(view, appearance);
            surface.Measure(size);
            surface.Arrange(new Rect(size));
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            foreach (var column in new[] { 0, 2 })
            {
                var cellWidth = (int)view.CellWidth;
                var cellHeight = (int)view.CellHeight;
                var pixels = new byte[cellWidth * cellHeight * 4];
                bitmap.CopyPixels(new Int32Rect(8 + column * cellWidth, 4, cellWidth, cellHeight),
                    pixels, cellWidth * 4, 0);
                Assert.Contains(Enumerable.Range(0, cellWidth * cellHeight), i => pixels[i * 4] >= 240);
            }
            for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                var pixel = new byte[4];
                var px = (int)(8 + view.CellWidth * (1 + (x + .5) / 8));
                var py = (int)(4 + view.CellHeight * (y + .5) / 8);
                bitmap.CopyPixels(new Int32Rect(px, py, 1, 1), pixel, 4, 0);
                var expected = filled(x, y) ? 255 : 0;
                Assert.Equal(expected, pixel[0]);
                Assert.Equal(expected, pixel[1]);
                Assert.Equal(expected, pixel[2]);
                Assert.Equal(255, pixel[3]);
            }
        });
    }

    [Theory]
    [InlineData(0x2591, 64)]
    [InlineData(0x2592, 128)]
    [InlineData(0x2593, 191)]
    public void ShadesUseConsistentForegroundCoverage(int codepoint, int expected)
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with
            {
                FontFamily = "Consolas", Foreground = "#FFFFFF",
                Background = "#000000", BackgroundOpacity = 1
            };
            var screen = new TerminalScreen(2, 1);
            screen.Feed(Encoding.UTF8.GetBytes($"{(char)codepoint}"));
            var view = new TerminalView(screen, appearance);
            var surface = BackgroundHost(view, appearance);
            surface.Measure(new Size(100, 80));
            surface.Arrange(new Rect(0, 0, 100, 80));
            var bitmap = new RenderTargetBitmap(100, 80, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(9, 5, 1, 1), pixel, 4, 0);
            for (var channel = 0; channel < 3; channel++)
                Assert.InRange((int)pixel[channel], expected - 1, expected + 1);
            Assert.Equal(255, pixel[3]);
        });
    }

    [Theory]
    [InlineData(96, 0, "Consolas", 14)]
    [InlineData(120, .5, "Consolas", 13.5)]
    [InlineData(144, 1, "Consolas", 14)]
    [InlineData(168, .5, "Courier New", 15.5)]
    [InlineData(192, 0, "Courier New", 14)]
    public void FullBlocksHaveNoGapsAndKeepForegroundOpaque(double dpi, double opacity, string font, double fontSize)
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with
            {
                FontFamily = font, FontSize = fontSize, BackgroundOpacity = opacity
            };
            var screen = new TerminalScreen(8, 4);
            var blocks = new string('\u2588', 8);
            screen.Feed(Encoding.UTF8.GetBytes("\x1b[1;38;2;255;64;32m" +
                string.Join("\r\n", Enumerable.Repeat(blocks, 4))));
            var view = new TerminalView(screen, appearance);
            var size = new Size(8 * view.CellWidth + 16, 4 * view.CellHeight + 8);
            view.Measure(size);
            view.Arrange(new Rect(size));
            var scale = dpi / 96;
            var width = (int)Math.Ceiling(size.Width * scale);
            var height = (int)Math.Ceiling(size.Height * scale);
            var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            for (var y = (int)Math.Ceiling(4 * scale); y < (int)((4 + 4 * view.CellHeight) * scale); y++)
            for (var x = (int)Math.Ceiling(8 * scale); x < (int)((8 + 8 * view.CellWidth) * scale); x++)
            {
                var offset = (y * width + x) * 4;
                Assert.Equal(32, pixels[offset]);
                Assert.Equal(64, pixels[offset + 1]);
                Assert.Equal(255, pixels[offset + 2]);
                Assert.Equal(255, pixels[offset + 3]);
            }
        });
    }

    [Theory]
    [InlineData('\u2500', true, true, false, false)]
    [InlineData('\u2502', false, false, true, true)]
    [InlineData('\u250C', false, true, false, true)]
    [InlineData('\u2518', true, false, true, false)]
    [InlineData('\u253C', true, true, true, true)]
    [InlineData('\u2501', true, true, false, false)]
    [InlineData('\u2504', true, true, false, false)]
    [InlineData('\u2505', true, true, false, false)]
    [InlineData('\u251D', false, true, true, true)]
    [InlineData('\u251E', false, true, true, true)]
    [InlineData('\u2521', false, true, true, true)]
    [InlineData('\u252D', true, true, false, true)]
    [InlineData('\u253D', true, true, true, true)]
    [InlineData('\u2542', true, true, true, true)]
    [InlineData('\u2547', true, true, true, true)]
    [InlineData('\u254A', true, true, true, true)]
    public void BoxDrawingStrokesConnectAtCellEdges(char codepoint, bool left, bool right, bool up, bool down)
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with
            {
                FontFamily = "Consolas", FontSize = 24, Foreground = "#FFFFFF",
                Background = "#000000", BackgroundOpacity = 1
            };
            var screen = new TerminalScreen(1, 1);
            screen.Feed(Encoding.UTF8.GetBytes(codepoint.ToString()));
            var view = new TerminalView(screen, appearance);
            var size = new Size(view.CellWidth + 16, view.CellHeight + 8);
            var surface = BackgroundHost(view, appearance);
            surface.Measure(size);
            surface.Arrange(new Rect(size));
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);

            bool IsForeground(double x, double y)
            {
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect((int)(8 + x * view.CellWidth),
                    (int)(4 + y * view.CellHeight), 1, 1), pixel, 4, 0);
                return pixel[0] > 200 && pixel[1] > 200 && pixel[2] > 200;
            }

            Assert.Equal(left, IsForeground(.15, .5));
            Assert.Equal(right, IsForeground(.85, .5));
            Assert.Equal(up, IsForeground(.5, .15));
            Assert.Equal(down, IsForeground(.5, .85));
        });
    }

    [Theory]
    [InlineData('\u251D')]
    [InlineData('\u252D')]
    [InlineData('\u254A')]
    public void MixedWeightJunctionsKeepLightAndHeavyStrokeWidths(char codepoint)
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with
            {
                FontFamily = "Consolas", FontSize = 24, Foreground = "#FFFFFF",
                Background = "#000000", BackgroundOpacity = 1
            };
            var screen = new TerminalScreen(1, 1);
            screen.Feed(Encoding.UTF8.GetBytes(codepoint.ToString()));
            var view = new TerminalView(screen, appearance);
            var size = new Size(view.CellWidth + 16, view.CellHeight + 8);
            var surface = BackgroundHost(view, appearance);
            surface.Measure(size);
            surface.Arrange(new Rect(size));
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);

            int CountStrokePixels(double x, double y, bool vertical)
            {
                var count = 0;
                var length = vertical ? (int)view.CellHeight : (int)view.CellWidth;
                for (var i = 0; i < length; i++)
                {
                    var px = vertical ? (int)(8 + x * view.CellWidth) : 8 + i;
                    var py = vertical ? 4 + i : (int)(4 + y * view.CellHeight);
                    var pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect(px, py, 1, 1), pixel, 4, 0);
                    if (pixel[0] > 200 && pixel[1] > 200 && pixel[2] > 200) count++;
                }
                return count;
            }

            if (codepoint == '\u251D')
            {
                Assert.True(CountStrokePixels(.85, .5, true) > CountStrokePixels(.5, .15, false));
            }
            else if (codepoint == '\u252D')
            {
                Assert.True(CountStrokePixels(.15, .5, true) > CountStrokePixels(.85, .5, true));
            }
            else
            {
                Assert.True(CountStrokePixels(.85, .5, true) > CountStrokePixels(.15, .5, true));
            }
        });
    }

    [Theory]
    [InlineData('\u2574', true, false, false, false)]
    [InlineData('\u2575', false, false, true, false)]
    [InlineData('\u2576', false, true, false, false)]
    [InlineData('\u2577', false, false, false, true)]
    [InlineData('\u2578', true, false, false, false)]
    [InlineData('\u2579', false, false, true, false)]
    [InlineData('\u257A', false, true, false, false)]
    [InlineData('\u257B', false, false, false, true)]
    [InlineData('\u257C', true, true, false, false)]
    [InlineData('\u257D', false, false, true, true)]
    [InlineData('\u257E', true, true, false, false)]
    [InlineData('\u257F', false, false, true, true)]
    public void BoxDrawingHalfLinesConnectOnlyTheirNamedEdges(
        char codepoint, bool left, bool right, bool up, bool down)
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with
            {
                FontFamily = "Consolas", FontSize = 24, Foreground = "#FFFFFF",
                Background = "#000000", BackgroundOpacity = 1
            };
            var screen = new TerminalScreen(1, 1);
            screen.Feed(Encoding.UTF8.GetBytes(codepoint.ToString()));
            var view = new TerminalView(screen, appearance);
            var size = new Size(view.CellWidth + 16, view.CellHeight + 8);
            var surface = BackgroundHost(view, appearance);
            surface.Measure(size);
            surface.Arrange(new Rect(size));
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);

            bool IsForeground(double x, double y)
            {
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect((int)(8 + x * view.CellWidth),
                    (int)(4 + y * view.CellHeight), 1, 1), pixel, 4, 0);
                return pixel[0] > 200 && pixel[1] > 200 && pixel[2] > 200;
            }

            Assert.Equal(left, IsForeground(.15, .5));
            Assert.Equal(right, IsForeground(.85, .5));
            Assert.Equal(up, IsForeground(.5, .15));
            Assert.Equal(down, IsForeground(.5, .85));
        });
    }

    [Theory]
    [InlineData('\u256D', 0, .5, 1, 1, .5, .65, .65)]
    [InlineData('\u256E', 1, .5, 1, 0, .5, .35, .65)]
    [InlineData('\u256F', 2, .5, 0, 0, .5, .35, .35)]
    [InlineData('\u2570', 3, .5, 0, 1, .5, .65, .35)]
    public void RoundedBoxCornersFollowCurvedCellAlignedPaths(
        char codepoint, int column, double startX, double startY, double endX, double endY,
        double arcX, double arcY)
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with
            {
                FontFamily = "Consolas", FontSize = 24, Foreground = "#FFFFFF",
                Background = "#000000", BackgroundOpacity = 1
            };
            var screen = new TerminalScreen(4, 1);
            screen.Feed(Encoding.UTF8.GetBytes(new string(' ', column) + codepoint));
            var view = new TerminalView(screen, appearance);
            var size = new Size(4 * view.CellWidth + 16, view.CellHeight + 8);
            var surface = BackgroundHost(view, appearance);
            surface.Measure(size);
            surface.Arrange(new Rect(size));
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);

            bool IsForeground(double x, double y)
            {
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect((int)(8 + (column + x) * view.CellWidth),
                    (int)(4 + y * view.CellHeight), 1, 1), pixel, 4, 0);
                return pixel[0] > 200 && pixel[1] > 200 && pixel[2] > 200;
            }

            Assert.True(IsForeground(startX, startY));
            Assert.True(IsForeground(endX, endY));
            Assert.True(IsForeground(arcX, arcY));
            Assert.False(IsForeground(.5, .5));
            Assert.False(IsForeground(arcX, 1 - arcY));
            if (column > 0) Assert.False(IsForeground(-.5, .5));
            if (column < 3) Assert.False(IsForeground(1.5, .5));
        });
    }

    [Theory]
    [InlineData('\u2550')]
    [InlineData('\u2551')]
    [InlineData('\u256C')]
    public void DoubleBoxDrawingUsesParallelStrokes(char codepoint)
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with
            {
                FontFamily = "Consolas", FontSize = 24, Foreground = "#FFFFFF",
                Background = "#000000", BackgroundOpacity = 1
            };
            var screen = new TerminalScreen(1, 1);
            screen.Feed(Encoding.UTF8.GetBytes(codepoint.ToString()));
            var view = new TerminalView(screen, appearance);
            var size = new Size(view.CellWidth + 16, view.CellHeight + 8);
            var surface = BackgroundHost(view, appearance);
            surface.Measure(size);
            surface.Arrange(new Rect(size));
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);

            bool IsForeground(double x, double y)
            {
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect((int)(8 + x * view.CellWidth),
                    (int)(4 + y * view.CellHeight), 1, 1), pixel, 4, 0);
                return pixel[0] > 200 && pixel[1] > 200 && pixel[2] > 200;
            }

            if (codepoint is '\u2550' or '\u256C')
            {
                Assert.True(IsForeground(.2, .39));
                Assert.True(IsForeground(.2, .61));
            }
            if (codepoint is '\u2551' or '\u256C')
            {
                Assert.True(IsForeground(.39, .2));
                Assert.True(IsForeground(.61, .2));
            }
        });
    }

    [Theory]
    [InlineData('\u2571', true, false)]
    [InlineData('\u2572', false, true)]
    [InlineData('\u2573', true, true)]
    public void BoxDrawingDiagonalsRenderAcrossCell(char codepoint, bool upward, bool downward)
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with
            {
                FontFamily = "Consolas", FontSize = 24, Foreground = "#FFFFFF",
                Background = "#000000", BackgroundOpacity = 1
            };
            var screen = new TerminalScreen(2, 1);
            screen.Feed(Encoding.UTF8.GetBytes(" " + codepoint));
            var view = new TerminalView(screen, appearance);
            var size = new Size(2 * view.CellWidth + 16, view.CellHeight + 8);
            var surface = BackgroundHost(view, appearance);
            surface.Measure(size);
            surface.Arrange(new Rect(size));
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);

            bool IsForeground(double x, double y, int column = 1)
            {
                var pixel = new byte[4];
                bitmap.CopyPixels(new Int32Rect((int)(8 + (column + x) * view.CellWidth),
                    (int)(4 + y * view.CellHeight), 1, 1), pixel, 4, 0);
                return pixel[0] > 200 && pixel[1] > 200 && pixel[2] > 200;
            }

            Assert.Equal(upward, IsForeground(.2, .8));
            Assert.Equal(upward, IsForeground(.8, .2));
            Assert.Equal(downward, IsForeground(.2, .2));
            Assert.Equal(downward, IsForeground(.8, .8));
            Assert.False(IsForeground(.2, .2, 0));
            Assert.False(IsForeground(.8, .8, 0));
        });
    }

    [Fact]
    public void RecordOffscreenRenderingBaseline()
    {
        OnSta(() =>
        {
            foreach (var streaming in new[] { false, true })
            foreach (var opacity in new[] { .5, 1.0 })
            {
                var appearance = Configuration.Parse("").Appearance with { BackgroundOpacity = opacity, FontFamily = "Consolas" };
                var screen = new TerminalScreen(120, 32, 10000);
                screen.Feed(Encoding.UTF8.GetBytes(string.Join("\r\n", Enumerable.Range(0, 32).Select(i => $"{i:D3}" + new string('W', 117)))));
                var view = new TerminalView(screen, appearance);
                var size = new Size(120 * view.CellWidth + 16, 32 * view.CellHeight + 8);
                view.Measure(size);
                view.Arrange(new Rect(size));
                var times = new List<double>();
                var builds = new List<double>();
                for (var i = 0; i < 40; i++)
                {
                    var watch = Stopwatch.StartNew();
                    if (streaming) screen.Feed(Encoding.UTF8.GetBytes($"\r\n{i:D3}" + new string('S', 117)));
                    view.InvalidateVisual();
                    view.UpdateLayout();
                    if (i >= 5) builds.Add(watch.Elapsed.TotalMilliseconds);
                    var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(view);
                    bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), new byte[4], 4, 0);
                    watch.Stop();
                    if (i >= 5) times.Add(watch.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                builds.Sort();
                static double P95(List<double> values) => values[(int)Math.Ceiling(values.Count * .95) - 1];
                output.WriteLine($"120x32, Consolas 14, 96 DPI, opacity {opacity}, {(streaming ? "1 new line/frame" : "static")}: " +
                    $"UI-thread render p95 {P95(builds):F2} ms, offscreen incl. software raster p95 {P95(times):F2} ms, lines built {view.LinesBuilt}.");
            }
        });
    }

    [Fact]
    public void RenderRebuildsOnlyChangedLines()
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with { FontFamily = "Consolas" };
            var screen = new TerminalScreen(20, 5, 100);
            var view = new TerminalView(screen, appearance);
            Render(view, 300, 120);
            Assert.Equal(5, view.LinesBuilt);
            Render(view, 300, 120);
            Assert.Equal(5, view.LinesBuilt);
            screen.Feed(Encoding.UTF8.GetBytes("abc"));
            Render(view, 300, 120);
            Assert.Equal(6, view.LinesBuilt);
            // Scrolling back one line reuses drawings of lines that only moved.
            screen.Feed(Encoding.UTF8.GetBytes("\r\n\r\n\r\n\r\n\r\n"));
            Render(view, 300, 120);
            var built = view.LinesBuilt;
            view.ScrollBy(1);
            Render(view, 300, 120);
            Assert.Equal(built + 1, view.LinesBuilt);
        });
    }

    [Fact]
    public void ScrolledViewStaysAnchoredAndShowsHistory()
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with { FontFamily = "Consolas" };
            var screen = new TerminalScreen(10, 3, 100);
            screen.Feed(Encoding.UTF8.GetBytes(string.Join("\r\n", Enumerable.Range(0, 10).Select(i => $"L{i}"))));
            var view = new TerminalView(screen, appearance);
            view.ScrollBy(2);
            Assert.Equal(2, view.ScrollOffset);
            Assert.Equal("L5", screen.GetViewportLine(0, view.ScrollOffset).Text(10).TrimEnd());
            screen.Feed(Encoding.UTF8.GetBytes("\r\nL10\r\nL11"));
            Assert.Equal(4, view.ScrollOffset);
            Assert.Equal("L5", screen.GetViewportLine(0, view.ScrollOffset).Text(10).TrimEnd());
            view.ScrollToTop();
            Assert.Equal(screen.ScrollbackCount, view.ScrollOffset);
            view.ScrollToBottom();
            screen.Feed(Encoding.UTF8.GetBytes("\r\nL12"));
            Assert.Equal(0, view.ScrollOffset);
        });
    }

    private static void Render(TerminalView view, int width, int height)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.InvalidateVisual();
        view.UpdateLayout();
        new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32).Render(view);
    }

    private static Border BackgroundHost(TerminalView view, Appearance appearance)
    {
        var color = (Color)ColorConverter.ConvertFromString(appearance.Background);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(appearance.BackgroundOpacity * 255), color.R, color.G, color.B)),
            Child = view
        };
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(.5, 128)]
    [InlineData(1, 255)]
    public void BackgroundOpacityDoesNotFadeText(double opacity, int expectedAlpha)
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with
            {
                BackgroundOpacity = opacity, Foreground = "#FFFFFF", Background = "#000000", FontFamily = "Consolas"
            };
            var screen = new TerminalScreen(10, 2);
            screen.Feed(Encoding.UTF8.GetBytes("MMMM"));
            var view = new TerminalView(screen, appearance);
            var surface = BackgroundHost(view, appearance);
            surface.Measure(new Size(200, 80));
            surface.Arrange(new Rect(0, 0, 200, 80));
            var bitmap = new RenderTargetBitmap(200, 80, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var pixels = new byte[200 * 80 * 4];
            bitmap.CopyPixels(pixels, 200 * 4, 0);
            Assert.Equal(expectedAlpha, pixels[(70 * 200 + 190) * 4 + 3]);
            Assert.Contains(Enumerable.Range(0, 200 * 40), i =>
                pixels[i * 4 + 3] >= 240 && pixels[i * 4] >= 200);
        });
    }

    [Fact]
    public void FontSizeChangesCellSizeAndClamps()
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with { FontFamily = "Consolas", FontSize = 12 };
            var view = new TerminalView(new TerminalScreen(4, 1), appearance);
            var width = view.CellWidth;
            view.SetFontSize(24);
            Assert.Equal(24, view.FontSize);
            Assert.True(view.CellWidth > width);
            view.SetFontSize(1000);
            Assert.Equal(TerminalView.MaxFontSize, view.FontSize);
            view.SetFontSize(1);
            Assert.Equal(TerminalView.MinFontSize, view.FontSize);
        });
    }

    [Fact]
    public void SgrRenditionAttributesAffectRenderedCells()
    {
        OnSta(() =>
        {
            var appearance = Configuration.Parse("").Appearance with
            {
                FontFamily = "Consolas", Foreground = "#FFFFFF",
                Background = "#000000", BackgroundOpacity = 1
            };
            static byte[] Pixels(string text, Appearance appearance, out int width, out int height)
            {
                var screen = new TerminalScreen(4, 1);
                screen.Feed(Encoding.UTF8.GetBytes(text));
                var view = new TerminalView(screen, appearance);
                width = (int)Math.Ceiling(4 * view.CellWidth + 16);
                height = (int)Math.Ceiling(view.CellHeight + 8);
                var surface = BackgroundHost(view, appearance);
                surface.Measure(new Size(width, height));
                surface.Arrange(new Rect(0, 0, width, height));
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var pixels = new byte[width * height * 4];
                bitmap.CopyPixels(pixels, width * 4, 0);
                return pixels;
            }

            var plain = Pixels("M", appearance, out var width, out var height);
            foreach (var styled in new[] { "\x1b[2mM", "\x1b[3mM", "\x1b[1;3mM", "\x1b[9mM" })
            {
                var rendered = Pixels(styled, appearance, out _, out _);
                Assert.False(plain.SequenceEqual(rendered), $"Expected visible change for {styled}");
            }

            static int BrightPixels(byte[] pixels, int width, int height, int column, double minY, double maxY)
            {
                var left = 8 + column * (width - 16) / 4;
                var right = 8 + (column + 1) * (width - 16) / 4;
                return Enumerable.Range((int)(minY * height), (int)((maxY - minY) * height))
                    .SelectMany(y => Enumerable.Range(left, right - left).Select(x => (y * width + x) * 4))
                    .Count(i => pixels[i + 2] > 200 && pixels[i + 1] > 200 && pixels[i] > 200);
            }

            var underline = Pixels("\x1b[4m ", appearance, out width, out height);
            var reverse = Pixels("\x1b[7m ", appearance, out _, out _);
            var underlineInk = BrightPixels(underline, width, height, 0, 0, 1);
            var reverseFill = BrightPixels(reverse, width, height, 0, 0, 1);
            Assert.True(underlineInk > 0);
            Assert.True(reverseFill > underlineInk * 5);
        });
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
