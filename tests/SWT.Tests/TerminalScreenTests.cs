using System.Text;
using SWT.Core;
using Xunit;

namespace SWT.Tests;

public sealed class TerminalScreenTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static void Feed(TerminalScreen screen, string text) => screen.Feed(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void CombiningMarksAndCjkUseTerminalCellWidthsAtEveryUtf8Split()
    {
        var bytes = Encoding.UTF8.GetBytes("e\u0301 A\u4e2d\u6587B");
        for (var split = 0; split <= bytes.Length; split++)
        {
            var screen = new TerminalScreen(12, 2);
            screen.Feed(bytes.AsSpan(0, split));
            screen.Feed(bytes.AsSpan(split));
            Assert.Equal("e\u0301", screen[0, 0].Text);
            Assert.Equal(2, screen[0, 3].Width);
            Assert.Equal(0, screen[0, 4].Width);
            Assert.Equal(2, screen[0, 5].Width);
            Assert.Equal("B", screen[0, 7].Text);
            Assert.Equal(8, screen.CursorX);
            Assert.StartsWith("e\u0301 A\u4e2d\u6587B", screen.Line(0));
        }
    }

    [Theory]
    [InlineData("\x1b[?9h", TerminalMouseMode.X10)]
    [InlineData("\x1b[?1000h", TerminalMouseMode.Normal)]
    [InlineData("\x1b[?1002h", TerminalMouseMode.ButtonEvent)]
    [InlineData("\x1b[?1003h", TerminalMouseMode.AnyEvent)]
    public void MouseTrackingModesAreEnabledByPrivateMode(string sequence, TerminalMouseMode expected)
    {
        var screen = new TerminalScreen(10, 3);
        Feed(screen, sequence);
        Assert.Equal(expected, screen.MouseMode);
    }

    [Fact]
    public void MouseTrackingModeResetDoesNotDisableDifferentActiveMode()
    {
        var screen = new TerminalScreen(10, 3);
        Feed(screen, "\x1b[?1002h\x1b[?1000l");
        Assert.Equal(TerminalMouseMode.ButtonEvent, screen.MouseMode);
        Feed(screen, "\x1b[?1002l");
        Assert.Equal(TerminalMouseMode.None, screen.MouseMode);
    }

    [Fact]
    public void SgrMouseModeCanBeEnabledAndDisabledIndependently()
    {
        var screen = new TerminalScreen(10, 3);
        Feed(screen, "\x1b[?1006h\x1b[?1000h");
        Assert.True(screen.SgrMouse);
        Assert.Equal(TerminalMouseMode.Normal, screen.MouseMode);
        Feed(screen, "\x1b[?1006l");
        Assert.False(screen.SgrMouse);
        Assert.Equal(TerminalMouseMode.Normal, screen.MouseMode);
    }

    [Theory]
    [InlineData(0, 1, 1, false, false, TerminalMouseModifiers.None, true, "\x1b[<0;1;1M")]
    [InlineData(2, 8, 4, true, false, TerminalMouseModifiers.None, true, "\x1b[<2;8;4m")]
    [InlineData(1, 5, 6, false, true, TerminalMouseModifiers.Shift | TerminalMouseModifiers.Control, true, "\x1b[<53;5;6M")]
    [InlineData(64, 3, 2, false, false, TerminalMouseModifiers.Alt, true, "\x1b[<72;3;2M")]
    [InlineData(3, 7, 9, false, true, TerminalMouseModifiers.None, true, "\x1b[<35;7;9M")]
    [InlineData(0, 1, 1, false, false, TerminalMouseModifiers.None, false, "\x1b[M !!")]
    [InlineData(3, 7, 9, false, true, TerminalMouseModifiers.None, false, "\x1b[MC')")]
    public void MouseInputEncodesSgrAndLegacyReports(int button, int column, int row, bool release, bool motion,
        TerminalMouseModifiers modifiers, bool sgr, string expected)
    {
        Assert.Equal(expected, TerminalMouseInput.Encode(button, column, row, release, motion, modifiers, sgr));
    }

    [Fact]
    public void LegacyMouseEncodingRejectsCoordinatesOutsideAsciiRange()
    {
        Assert.Null(TerminalMouseInput.Encode(0, 96, 1, false, false, TerminalMouseModifiers.None, sgr: false));
    }

    [Fact]
    public void MouseNoButtonCodeIsValidOnlyForMotionReports()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TerminalMouseInput.Encode(3, 1, 1, release: false, motion: false, TerminalMouseModifiers.None, sgr: true));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TerminalMouseInput.Encode(3, 1, 1, release: true, motion: true, TerminalMouseModifiers.None, sgr: true));
    }

    [Fact]
    public void CombiningAtRightEdgeDoesNotTriggerWrap()
    {
        var screen = new TerminalScreen(2, 2);
        Feed(screen, "ae\u0301");
        Assert.Equal("e\u0301", screen[0, 1].Text);
        Assert.Equal((1, 0), (screen.CursorX, screen.CursorY));
        Feed(screen, "x");
        Assert.Equal("x", screen[1, 0].Text);
    }

    [Fact]
    public void WideCharactersWrapBeforeLastColumnAndDeferWrapWhenTheyFit()
    {
        var screen = new TerminalScreen(4, 3);
        Feed(screen, "abc\u4e2d");
        Assert.Equal("abc ", screen.Line(0));
        Assert.Equal("\u4e2d", screen[1, 0].Text);
        Assert.Equal((2, 1), (screen.CursorX, screen.CursorY));
        Feed(screen, "\u6587\u0301");
        Assert.Equal("\u6587\u0301", screen[1, 2].Text);
        Assert.Equal((3, 1), (screen.CursorX, screen.CursorY));
        Feed(screen, "X");
        Assert.Equal("X", screen[2, 0].Text);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void OverwriteOrEraseEitherWideHalfClearsEntireCharacter(int column)
    {
        var screen = new TerminalScreen(5, 2);
        Feed(screen, $"a\u4e2db\x1b[1;{column}HX");
        Assert.Equal(column == 2 ? "aX b " : "a Xb ", screen.Line(0));
        Feed(screen, $"\r\x1b[2Ka\u4e2db\x1b[1;{column}H\x1b[X");
        Assert.Equal("a  b ", screen.Line(0));
        Assert.All(Enumerable.Range(0, 5), x => Assert.Equal(1, screen[0, x].Width));
    }

    [Fact]
    public void CharacterEditsAndResizeNeverLeaveOrphanWideHalves()
    {
        var screen = new TerminalScreen(5, 2);
        Feed(screen, "a\u4e2db\x1b[1;3H\x1b[P");
        Assert.Equal("a b  ", screen.Line(0));
        Feed(screen, "\r\x1b[2Ka\u4e2db\x1b[1;3H\x1b[@");
        Assert.Equal("a   b", screen.Line(0));
        Feed(screen, "\r\x1b[2Ka\u4e2d");
        screen.Resize(2, 2);
        Assert.Equal("a ", screen.Line(0));
        screen.Resize(5, 2);
        Assert.Equal("a\u4e2d  ", screen.Line(0));
    }

    [Fact]
    public void UnicodeCopyUsesCellRangesAndHistoryRetainsWideContinuation()
    {
        var screen = new TerminalScreen(6, 1, scrollbackLines: 5);
        Feed(screen, "e\u0301\u4e2d");
        Assert.Equal("e\u0301", screen.GetLine(0).Text(0, 1));
        Assert.Equal("\u4e2d", screen.GetLine(0).Text(2, 1));
        Assert.Equal("\u4e2d", screen.GetLine(0).Text(1, 1));
        Feed(screen, "\r\n");
        var history = screen.GetScrollbackLine(0);
        Assert.Equal(3, history.Length);
        Assert.Equal(0, history[2].Width);
        Assert.Equal("e\u0301\u4e2d", history.Text(3));
    }

    [Fact]
    public void CombiningStorageIsBoundedAndWideCharacterInOneColumnUsesReplacement()
    {
        var screen = new TerminalScreen(1, 1);
        Feed(screen, "\u0301");
        Assert.Equal(" ", screen.Line(0));
        Feed(screen, "\u4e2d");
        Assert.Equal("\uFFFD", screen.Line(0));
        Feed(screen, new string('\u0301', 1000));
        Assert.InRange(screen[0, 0].Text!.Length, 1, 64);
    }

    [Theory]
    [InlineData("@", "ab cde")]
    [InlineData("0@", "ab cde")]
    [InlineData("2@", "ab  cd")]
    [InlineData("99999@", "ab    ")]
    [InlineData("P", "abdef ")]
    [InlineData("0P", "abdef ")]
    [InlineData("2P", "abef  ")]
    [InlineData("99999P", "ab    ")]
    public void CharacterEditsShiftWithinRowAndKeepCursor(string operation, string expected)
    {
        var screen = new TerminalScreen(6, 2, scrollbackLines: 10);
        Feed(screen, "abcdef\x1b[2;1Hother\x1b[1;3H\x1b[" + operation);
        Assert.Equal(expected, screen.Line(0));
        Assert.Equal("other ", screen.Line(1));
        Assert.Equal((2, 0), (screen.CursorX, screen.CursorY));
        Assert.Equal(0, screen.ScrolledLines);
    }

    [Theory]
    [InlineData("@", 1, 2)]
    [InlineData("P", 1, 1)]
    public void CharacterEditsPreserveShiftedCellAttributesAndInvalidateOnlyEditedRow(
        string operation, int sourceColumn, int destinationColumn)
    {
        var screen = new TerminalScreen(5, 2);
        Feed(screen, "a\x1b[1;31;44mbc\x1b[0mde\x1b[1;2H");
        var original = screen[0, operation == "P" ? sourceColumn + 1 : sourceColumn];
        var row = screen.GetLine(0);
        var other = screen.GetLine(1);
        var version = row.Version;
        var otherVersion = other.Version;
        Feed(screen, "\x1b[32;45m\x1b[" + operation);
        Assert.Equal(original, screen[0, destinationColumn]);
        var blank = screen[0, operation == "@" ? 1 : 4];
        Assert.Equal(new Cell(" ", new TerminalColor(2), new TerminalColor(5), false), blank);
        Assert.NotEqual(version, row.Version);
        Assert.Equal(otherVersion, other.Version);
    }

    [Theory]
    [InlineData("@")]
    [InlineData("P")]
    public void CharacterEditsAtRightEdgeCancelPendingWrap(string operation)
    {
        var screen = new TerminalScreen(3, 2);
        Feed(screen, "abc\x1b[999" + operation + "X");
        Assert.Equal("abX", screen.Line(0));
        Assert.Equal("   ", screen.Line(1));
        Assert.Equal((2, 0), (screen.CursorX, screen.CursorY));
    }

    [Theory]
    [InlineData("@")]
    [InlineData("P")]
    public void CharacterEditsWorkInSingleCellScreen(string operation)
    {
        var screen = new TerminalScreen(1, 1);
        Feed(screen, "a\x1b[999" + operation);
        Assert.Equal(" ", screen.Line(0));
        Assert.Equal((0, 0), (screen.CursorX, screen.CursorY));
    }

    [Fact]
    public void CharacterEditsWorkOutsideVerticalMarginsAndInAlternateScreen()
    {
        var screen = new TerminalScreen(5, 4, scrollbackLines: 10);
        Feed(screen, "abcde\x1b[2;3r\x1b[1;2H\x1b[P");
        Assert.Equal("acde ", screen.Line(0));
        Feed(screen, "\x1b[?1049h12345\x1b[1;2H\x1b[@");
        Assert.Equal("1 234", screen.Line(0));
        Feed(screen, "\x1b[?1049l");
        Assert.Equal("acde ", screen.Line(0));
        Assert.Equal(0, screen.ScrolledLines);
    }

    [Fact]
    public void CharacterEditingSequencesSurviveEveryByteSplit()
    {
        var bytes = Encoding.UTF8.GetBytes("abcdef\x1b[1;2H\x1b[2@\x1b[1;5H\x1b[P");
        for (var split = 0; split <= bytes.Length; split++)
        {
            var screen = new TerminalScreen(6, 2);
            screen.Feed(bytes.AsSpan(0, split));
            screen.Feed(bytes.AsSpan(split));
            Assert.Equal("a  bd ", screen.Line(0));
            Assert.Equal((4, 0), (screen.CursorX, screen.CursorY));
        }
    }

    [Theory]
    [InlineData("H")]
    [InlineData("f")]
    [InlineData("d")]
    public void OriginModeOffsetsAndClampsAbsoluteRows(string command)
    {
        var screen = new TerminalScreen(5, 6);
        Feed(screen, "\x1b[2;5r\x1b[?6h");
        Assert.True(screen.OriginMode);
        Assert.Equal((0, 1), (screen.CursorX, screen.CursorY));
        Feed(screen, $"\x1b[2{command}");
        Assert.Equal(2, screen.CursorY);
        Feed(screen, $"\x1b[999{command}");
        Assert.Equal(4, screen.CursorY);
        Feed(screen, $"\x1b[0{command}");
        Assert.Equal(1, screen.CursorY);
        Feed(screen, "\x1b[?6l");
        Assert.False(screen.OriginMode);
        Assert.Equal((0, 0), (screen.CursorX, screen.CursorY));
        Feed(screen, $"\x1b[6{command}");
        Assert.Equal(5, screen.CursorY);
    }

    [Theory]
    [InlineData("A", 1)]
    [InlineData("F", 1)]
    [InlineData("B", 4)]
    [InlineData("E", 4)]
    public void OriginModeConstrainsRelativeVerticalMovement(string command, int expected)
    {
        var screen = new TerminalScreen(5, 6);
        Feed(screen, $"\x1b[2;5r\x1b[?6h\x1b[2;3H\x1b[999{command}");
        Assert.Equal(expected, screen.CursorY);
        Assert.Equal(command is "E" or "F" ? 0 : 2, screen.CursorX);
        Assert.Equal(0, screen.ScrolledLines);
    }

    [Fact]
    public void OriginModeMarginChangesHomeCursorAndReportsRelativePosition()
    {
        var screen = new TerminalScreen(5, 6);
        var replies = new List<string>();
        screen.Reply += replies.Add;
        Feed(screen, "\x1b[?6h\x1b[3;5r\x1b[6n\x1b[2;4H\x1b[6n\x1b[?6l\x1b[4;4H\x1b[6n");
        Assert.Equal(["\x1b[1;1R", "\x1b[2;4R", "\x1b[4;4R"], replies);
    }

    [Theory]
    [InlineData("\x1b" + "7", "\x1b" + "8")]
    [InlineData("\x1b[s", "\x1b[u")]
    [InlineData("\x1b[?1048h", "\x1b[?1048l")]
    public void SavedCursorRestoresOriginModeWithoutApplyingOffsetTwice(string save, string restore)
    {
        var screen = new TerminalScreen(5, 6);
        Feed(screen, "\x1b[2;5r\x1b[?6h\x1b[2;3H" + save + "\x1b[?6l" + restore);
        Assert.True(screen.OriginMode);
        Assert.Equal((2, 2), (screen.CursorX, screen.CursorY));
        Feed(screen, "\x1b[?6l\x1b[6;4H" + save + "\x1b[?6h" + restore);
        Assert.False(screen.OriginMode);
        Assert.Equal((3, 5), (screen.CursorX, screen.CursorY));
    }

    [Fact]
    public void OriginModeIsPerBufferAndSurvivesResize()
    {
        var screen = new TerminalScreen(5, 6);
        Feed(screen, "\x1b[2;5r\x1b[?6h\x1b[?1049h");
        Assert.False(screen.OriginMode);
        Feed(screen, "\x1b[3;5r\x1b[?6h\x1b[?1049l");
        Assert.True(screen.OriginMode);
        Assert.Equal(1, screen.CursorY);
        screen.Resize(4, 6);
        Feed(screen, "\x1b[H");
        Assert.Equal(1, screen.CursorY);
        screen.Resize(4, 1);
        Feed(screen, "\x1b[999;999H");
        Assert.True(screen.OriginMode);
        Assert.Equal((3, 0), (screen.CursorX, screen.CursorY));
        Feed(screen, "\x1b[?47h\x1b[H");
        Assert.True(screen.OriginMode);
        Assert.Equal((0, 0), (screen.CursorX, screen.CursorY));
    }

    [Fact]
    public void OriginModeLineFeedScrollsInsideMarginsAndRestoreClampsToNewMargins()
    {
        var screen = NumberedScreen();
        Feed(screen, "\x1b[2;4r\x1b[?6h\x1b[3;1H\n");
        Assert.Equal(["AAA", "CCC", "DDD", "   ", "EEE"], Lines(screen));
        Assert.Equal(0, screen.ScrolledLines);
        Feed(screen, "\x1b[1;2H\x1b" + "7\x1b[3;4r\x1b" + "8");
        Assert.True(screen.OriginMode);
        Assert.Equal((1, 2), (screen.CursorX, screen.CursorY));
    }

    [Fact]
    public void OriginModeChangesCancelPendingWrapAndSurviveEveryByteSplit()
    {
        var bytes = Encoding.UTF8.GetBytes("\x1b[2;4r\x1b[?6h\x1b[2;2HX\x1b[6n\x1b[?6l");
        for (var split = 0; split <= bytes.Length; split++)
        {
            var screen = new TerminalScreen(3, 5);
            var replies = new List<string>();
            screen.Reply += replies.Add;
            Feed(screen, "abc");
            screen.Feed(bytes.AsSpan(0, split));
            screen.Feed(bytes.AsSpan(split));
            Assert.Equal("X", screen[2, 1].Text);
            Assert.Equal(["\x1b[2;3R"], replies);
            Assert.False(screen.OriginMode);
            Assert.Equal((0, 0), (screen.CursorX, screen.CursorY));
        }
    }

    private static TerminalScreen NumberedScreen()
    {
        var screen = new TerminalScreen(3, 5, scrollbackLines: 10);
        Feed(screen, "AAA\r\nBBB\r\nCCC\r\nDDD\r\nEEE");
        return screen;
    }

    private static string[] Lines(TerminalScreen screen) =>
        Enumerable.Range(0, screen.Rows).Select(screen.Line).ToArray();

    [Theory]
    [InlineData("\n")]
    [InlineData("\u001bD")]
    [InlineData("\u001bE")]
    [InlineData("X")]
    public void IndexAndWrapScrollOnlyInsideMargins(string control)
    {
        var screen = NumberedScreen();
        Feed(screen, "\x1b[2;4r\x1b[4;3H");
        if (control == "X") Feed(screen, "D");
        Feed(screen, control);
        Assert.Equal(["AAA", "CCC", "DDD", control == "X" ? "X  " : "   ", "EEE"], Lines(screen));
        Assert.Equal(0, screen.ScrollbackCount);
        Assert.Equal(0, screen.ScrolledLines);
        Assert.Equal(3, screen.CursorY);
        Assert.Equal(control == "\u001bE" ? 0 : control == "X" ? 1 : 2, screen.CursorX);
    }

    [Fact]
    public void IndexBelowMarginsDoesNotScrollAndReverseIndexAboveDoesNotScroll()
    {
        var screen = NumberedScreen();
        Feed(screen, "\x1b[2;4r\x1b[5;1H\n\u001bD");
        Assert.Equal(4, screen.CursorY);
        Feed(screen, "\x1b[1;1H\x1bM");
        Assert.Equal(0, screen.CursorY);
        Assert.Equal(["AAA", "BBB", "CCC", "DDD", "EEE"], Lines(screen));
    }

    [Fact]
    public void ReverseIndexScrollsDownAtTopMarginOtherwiseMovesUp()
    {
        var screen = NumberedScreen();
        Feed(screen, "\x1b[2;4r\x1b[2;2H\x1bM");
        Assert.Equal(["AAA", "   ", "BBB", "CCC", "EEE"], Lines(screen));
        Assert.Equal(1, screen.CursorX);
        Assert.Equal(1, screen.CursorY);
        Feed(screen, "\x1b[4;2H\x1bM");
        Assert.Equal(2, screen.CursorY);
        Assert.Equal(0, screen.ScrolledLines);
    }

    [Theory]
    [InlineData("\x1b[L", "   ", "CCC")]
    [InlineData("\x1b[M", "DDD", "   ")]
    [InlineData("\x1b[0L", "   ", "CCC")]
    [InlineData("\x1b[0M", "DDD", "   ")]
    [InlineData("\x1b[99999L", "   ", "   ")]
    [InlineData("\x1b[99999M", "   ", "   ")]
    public void InsertDeleteLinesClampCountsAndKeepCursor(string control, string third, string fourth)
    {
        var screen = NumberedScreen();
        Feed(screen, "\x1b[2;4r\x1b[3;2H" + control);
        Assert.Equal(["AAA", "BBB", third, fourth, "EEE"], Lines(screen));
        Assert.Equal((1, 2), (screen.CursorX, screen.CursorY));
        Assert.Equal(0, screen.ScrolledLines);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void InsertDeleteLinesOutsideRegionAreIgnored(int row)
    {
        var screen = NumberedScreen();
        Feed(screen, $"\x1b[2;4r\x1b[{row};1H\x1b[L\x1b[M");
        Assert.Equal(["AAA", "BBB", "CCC", "DDD", "EEE"], Lines(screen));
    }

    [Theory]
    [InlineData("\x1b[S", "CCC", "DDD", "   ")]
    [InlineData("\x1b[T", "   ", "BBB", "CCC")]
    [InlineData("\x1b[0S", "CCC", "DDD", "   ")]
    [InlineData("\x1b[0T", "   ", "BBB", "CCC")]
    [InlineData("\x1b[2S", "DDD", "   ", "   ")]
    [InlineData("\x1b[2T", "   ", "   ", "BBB")]
    [InlineData("\x1b[99999S", "   ", "   ", "   ")]
    [InlineData("\x1b[99999T", "   ", "   ", "   ")]
    public void ExplicitScrollingUsesMarginsRegardlessOfCursor(string control, string second, string third, string fourth)
    {
        var screen = NumberedScreen();
        Feed(screen, "\x1b[2;4r\x1b[5;2H" + control);
        Assert.Equal(["AAA", second, third, fourth, "EEE"], Lines(screen));
        Assert.Equal((1, 4), (screen.CursorX, screen.CursorY));
        Assert.Equal(0, screen.ScrolledLines);
    }

    [Theory]
    [InlineData("\x1b[4;2r")]
    [InlineData("\x1b[2;2r")]
    [InlineData("\x1b[1;6r")]
    [InlineData("\x1b[2;4;5r")]
    public void InvalidMarginsLeaveRegionAndCursorUnchanged(string control)
    {
        var screen = NumberedScreen();
        Feed(screen, "\x1b[2;4r\x1b[3;2H" + control);
        Assert.Equal((1, 2), (screen.CursorX, screen.CursorY));
        Feed(screen, "\x1b[S");
        Assert.Equal(["AAA", "CCC", "DDD", "   ", "EEE"], Lines(screen));
    }

    [Theory]
    [InlineData("\x1b[r")]
    [InlineData("\x1b[0;0r")]
    [InlineData("\x1b[1r")]
    public void DefaultMarginsRestoreFullScreenAndHomeCursor(string control)
    {
        var screen = NumberedScreen();
        Feed(screen, "\x1b[2;4r" + control);
        Assert.Equal((0, 0), (screen.CursorX, screen.CursorY));
        Feed(screen, "\x1b[S");
        Assert.Equal(["BBB", "CCC", "DDD", "EEE", "   "], Lines(screen));
        Assert.Equal("AAA", screen.GetScrollbackLine(0).Text(3));
    }

    [Theory]
    [InlineData("\x1b[L")]
    [InlineData("\x1b[M")]
    [InlineData("\x1b[T")]
    [InlineData("\x1bM")]
    public void EditingFullScreenNeverAddsScrollback(string control)
    {
        var screen = NumberedScreen();
        Feed(screen, "\x1b[1;1H" + control);
        Assert.Equal(0, screen.ScrolledLines);
        Assert.Equal(0, screen.ScrollbackCount);
    }

    [Fact]
    public void AlternateScreenScrollingDoesNotPolluteMainHistory()
    {
        var screen = NumberedScreen();
        Feed(screen, "\x1b[S\x1b[?1049h\x1b[99S\x1b[99T\x1b[5;1H\n\x1b[?1049l");
        Assert.Equal(1, screen.ScrolledLines);
        Assert.Equal(1, screen.ScrollbackCount);
        Assert.Equal("AAA", screen.GetScrollbackLine(0).Text(3));
    }

    [Fact]
    public void MarginsAreIndependentPerBufferAndHeightResizeResetsBoth()
    {
        var screen = NumberedScreen();
        Feed(screen, "\x1b[2;4r\x1b[?47h\x1b[3;5r\x1b[?47l\x1b[S");
        Assert.Equal(["AAA", "CCC", "DDD", "   ", "EEE"], Lines(screen));
        screen.Resize(4, 5);
        Feed(screen, "\x1b[S");
        Assert.Equal(["AAA ", "DDD ", "    ", "    ", "EEE "], Lines(screen));
        screen.Resize(4, 6);
        Feed(screen, "\x1b[S");
        Assert.Equal("DDD ", screen.Line(0));
        Assert.Equal(1, screen.ScrollbackCount);
        Feed(screen, "\x1b[?47h\x1b[6;1H\n");
        Assert.Equal(5, screen.CursorY);
        Assert.Equal(1, screen.ScrolledLines);
        Feed(screen, "\x1b[1;1HA\x1b[2;1HB\x1b[S");
        Assert.StartsWith("B", screen.Line(0));
        Assert.Equal(1, screen.ScrolledLines);
    }

    [Fact]
    public void ShrinkingToOneRowLeavesValidScrollingBounds()
    {
        var screen = NumberedScreen();
        Feed(screen, "\x1b[2;4r");
        screen.Resize(3, 1);
        Feed(screen, "\x1b[99L\x1b[99M\x1b[99S\x1b[99T\x1bM\n");
        Assert.Equal("   ", screen.Line(0));
        Assert.Equal((0, 0), (screen.CursorX, screen.CursorY));
    }

    [Fact]
    public void ScrolledBlanksUseCurrentBackgroundAndInvalidateRecycledLine()
    {
        var screen = NumberedScreen();
        var recycled = screen.GetLine(1);
        var version = recycled.Version;
        Feed(screen, "\x1b[2;4r\x1b[44m\x1b[S");
        Assert.Same(recycled, screen.GetLine(3));
        Assert.NotEqual(version, recycled.Version);
        for (var x = 0; x < screen.Columns; x++)
            Assert.Equal(new TerminalColor(4), screen[3, x].Background);
        Assert.Equal(TerminalColor.Default, screen[0, 0].Background);
    }

    [Fact]
    public void MarginAndEditingSequencesSurviveEveryByteSplit()
    {
        var bytes = Encoding.UTF8.GetBytes("\x1b[2;4r\x1b[3;1H\x1b[L\x1b[M\x1b[S\x1b[T\x1b[2;1H\x1bM");
        for (var split = 0; split <= bytes.Length; split++)
        {
            var screen = NumberedScreen();
            screen.Feed(bytes.AsSpan(0, split));
            screen.Feed(bytes.AsSpan(split));
            Assert.Equal(["AAA", "   ", "   ", "CCC", "EEE"], Lines(screen));
            Assert.Equal(0, screen.ScrolledLines);
        }
    }

    [Fact]
    public void EmptyAndExpandedCellsUseDefaultColors()
    {
        var screen = new TerminalScreen(3, 2);
        Assert.Equal(TerminalColor.Default, screen[0, 0].Background);
        Assert.Equal(TerminalColor.Default, screen[0, 0].Foreground);
        screen.Resize(4, 3);
        Assert.Equal(TerminalColor.Default, screen[2, 3].Background);
        Assert.Equal(TerminalColor.Default, screen[2, 3].Foreground);
    }

    [Fact]
    public void SupplementaryCodepointIsNotSplitAtRightEdge()
    {
        var screen = new TerminalScreen(3, 2);
        var bytes = Encoding.UTF8.GetBytes("ab\U0001F600X");
        foreach (var b in bytes) screen.Feed([b]);
        Assert.Equal("\U0001F600", screen[0, 2].Text);
        Assert.Equal("X", screen[1, 0].Text);
    }

    [Fact]
    public void EveryUtf8AndEscapeSplitPreservesState()
    {
        var bytes = Encoding.UTF8.GetBytes("\x1b[2;3H\x1b[38;2;12;34;56mé");
        for (var split = 0; split <= bytes.Length; split++)
        {
            var screen = new TerminalScreen(10, 4);
            screen.Feed(bytes.AsSpan(0, split));
            screen.Feed(bytes.AsSpan(split));
            Assert.Equal("é", screen[1, 2].Text);
            Assert.Equal(new TerminalColor(0x0C2238, true), screen[1, 2].Foreground);
            Assert.Equal(3, screen.CursorX);
        }
    }

    [Fact]
    public void WrapIsDeferredUntilNextPrintableCharacter()
    {
        var screen = new TerminalScreen(3, 2);
        Feed(screen, "abc\rX");
        Assert.Equal("Xbc", screen.Line(0));
        Assert.Equal(0, screen.CursorY);
        Feed(screen, "\x1b[1;3HdE");
        Assert.Equal("Xbd", screen.Line(0));
        Assert.Equal("E  ", screen.Line(1));
    }

    [Fact]
    public void EraseRespectsCurrentBackgroundAndLeavesCursor()
    {
        var screen = new TerminalScreen(5, 2);
        Feed(screen, "abc\x1b[44m\x1b[2K");
        Assert.Equal("     ", screen.Line(0));
        Assert.Equal(new TerminalColor(4), screen[0, 0].Background);
        Assert.Equal(3, screen.CursorX);
    }

    [Fact]
    public void ResizeKeepsCursorRowVisibleAndMovesRowsAboveIntoScrollback()
    {
        var screen = new TerminalScreen(3, 2, scrollbackLines: 10);
        Feed(screen, "one\r\ntwo\r\nend");
        Assert.Equal("two", screen.Line(0));
        Assert.Equal("end", screen.Line(1));
        screen.Resize(2, 1);
        Assert.Equal("d ", screen.Line(0));
        Assert.InRange(screen.CursorX, 0, 1);
        Assert.Equal(0, screen.CursorY);
        Assert.Equal(5, screen.ScrollbackCount);
        Assert.Equal(["on", "e ", "tw", "o ", "en"],
            Enumerable.Range(0, 5).Select(i => screen.GetScrollbackLine(i).Text(2)));
    }

    [Fact]
    public void AlternateScreenKeepsMainContentsAndRestoresCursorWithoutScrollback()
    {
        var screen = new TerminalScreen(4, 2, scrollbackLines: 10);
        Feed(screen, "main\x1b[1;2H\x1b[?1049h");
        Assert.True(screen.IsAlternateScreen);
        Assert.Equal("    ", screen.Line(0));
        Feed(screen, "alt\r\nnext\r\nlast");
        Assert.Equal(0, screen.ScrollbackCount);

        Feed(screen, "\x1b[?1049l");
        Assert.False(screen.IsAlternateScreen);
        Assert.Equal("main", screen.Line(0));
        Assert.Equal(1, screen.CursorX);
        Assert.Equal(0, screen.CursorY);
        Assert.Equal(0, screen.ScrollbackCount);
    }

    [Fact]
    public void AlternateScreenModesPreserveOrClearItsContentsAsSpecified()
    {
        var screen = new TerminalScreen(6, 2);
        Feed(screen, "\x1b[?47hkeep\x1b[?47l\x1b[?47h");
        Assert.Equal("keep  ", screen.Line(0));
        Feed(screen, "\x1b[?47l\x1b[?1047h");
        Assert.Equal("keep  ", screen.Line(0));
        Feed(screen, "x\x1b[?1047l\x1b[?47h");
        Assert.Equal("      ", screen.Line(0));
        Feed(screen, "\x1b[?47l");
        Assert.False(screen.IsAlternateScreen);
    }

    [Fact]
    public void ResizeWhileAlternateScreenIsActiveResizesBothBuffers()
    {
        var screen = new TerminalScreen(4, 3, scrollbackLines: 10);
        Feed(screen, "one\r\ntwo\r\nend\x1b[?1049h");
        Feed(screen, "alt");
        screen.Resize(2, 2);
        Assert.Equal(2, screen.Columns);
        Assert.Equal(2, screen.Rows);
        Assert.Equal("al", screen.Line(0));
        Assert.Equal(0, screen.ScrollbackCount);

        Feed(screen, "\x1b[?1049l");
        Assert.Equal(4, screen.ScrollbackCount);
        Assert.Equal(["on", "e ", "tw", "o "],
            Enumerable.Range(0, 4).Select(i => screen.GetScrollbackLine(i).Text(2)));
        Assert.Equal(["en", "d "], [screen.Line(0), screen.Line(1)]);
        Assert.Equal(1, screen.CursorY);
    }

    private static TerminalLine[] TranscriptLines(TerminalScreen screen) =>
        Enumerable.Range(0, screen.ScrollbackCount).Select(screen.GetScrollbackLine)
            .Concat(Enumerable.Range(0, screen.Rows).Select(screen.GetLine)).ToArray();

    private static string Transcript(TerminalScreen screen) =>
        string.Concat(TranscriptLines(screen).Select(line =>
            line.IsWrapped ? line.Text(line.WrappedLength) : line.Text(line.Length).TrimEnd() + "\n")).TrimEnd('\n');

    private static void AssertValidWideCells(TerminalScreen screen)
    {
        foreach (var line in TranscriptLines(screen))
        {
            Assert.InRange(line.WrappedLength, 0, line.Length);
            if (!line.IsWrapped) Assert.Equal(0, line.WrappedLength);
            for (var x = 0; x < line.Length; x++)
            {
                if (line[x].Width == 2)
                {
                    Assert.True(x + 1 < line.Length);
                    Assert.Equal(0, line[x + 1].Width);
                }
                if (line[x].Width == 0)
                {
                    Assert.True(x > 0);
                    Assert.Equal(2, line[x - 1].Width);
                }
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(20)]
    public void WidthRoundtripReflowsHistoryAndPreservesHardBreaks(int width)
    {
        var screen = new TerminalScreen(6, 3, 100);
        Feed(screen, "abcdefghijk\r\nhard\r\n\r\nlast-long-line");
        var transcript = Transcript(screen);
        screen.Resize(width, 3);
        Assert.Equal(transcript, Transcript(screen));
        Assert.All(TranscriptLines(screen), line => Assert.True(line.Length <= width));
        AssertValidWideCells(screen);
        screen.Resize(6, 3);
        Assert.Equal(transcript, Transcript(screen));
        AssertValidWideCells(screen);
        Feed(screen, "!");
        Assert.EndsWith("last-long-line!", Transcript(screen));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void WideCombiningAndAttributesSurviveWidthRoundtrip(int width)
    {
        var screen = new TerminalScreen(6, 3, 100);
        Feed(screen, "\x1b[1;38;2;12;34;56;44mabc\u4e2d\u0301\u6587Z\x1b[0m\r\nend");
        var original = Transcript(screen);
        var glyphs = TranscriptLines(screen).SelectMany(line => Enumerable.Range(0, line.Length)
            .Select(x => line[x])).Where(cell => cell.Width != 0 && cell.Text is not (null or " ")).ToArray();
        screen.Resize(width, 3);
        AssertValidWideCells(screen);
        screen.Resize(6, 3);
        Assert.Equal(original, Transcript(screen));
        var restored = TranscriptLines(screen).SelectMany(line => Enumerable.Range(0, line.Length)
            .Select(x => line[x])).Where(cell => cell.Width != 0 && cell.Text is not (null or " ")).ToArray();
        Assert.Equal(glyphs, restored);
        AssertValidWideCells(screen);
    }

    [Fact]
    public void SoftWrapMetadataExcludesWidePaddingAndSurvivesHistory()
    {
        var screen = new TerminalScreen(4, 1, 10);
        Feed(screen, "abc\u4e2d");
        var line = screen.GetScrollbackLine(0);
        Assert.True(line.IsWrapped);
        Assert.Equal(3, line.WrappedLength);
        Assert.Equal("abc", line.Text(line.WrappedLength));
        Assert.False(screen.GetLine(0).IsWrapped);
        screen.Resize(8, 2);
        Assert.Equal("abc\u4e2d   ", screen.Line(0));
        Assert.False(screen.GetLine(0).IsWrapped);
        Assert.Equal((5, 0), (screen.CursorX, screen.CursorY));
    }

    [Fact]
    public void ExactWidthTextIsHardUntilActualAutowrap()
    {
        var screen = new TerminalScreen(4, 3, 10);
        Feed(screen, "abcd");
        Assert.False(screen.GetLine(0).IsWrapped);
        Feed(screen, "\r\nEFGHx");
        Assert.False(screen.GetLine(0).IsWrapped);
        Assert.True(screen.GetLine(1).IsWrapped);
        Assert.Equal(4, screen.GetLine(1).WrappedLength);
        screen.Resize(9, 3);
        Assert.Equal(["abcd     ", "EFGHx    ", "         "], Lines(screen));
    }

    [Fact]
    public void CursorAndSavedCursorMapAcrossSoftRows()
    {
        var screen = new TerminalScreen(6, 4, 20);
        Feed(screen, "abcdefghijk\x1b[2;3H\u001b7\x1b[1;5H");
        screen.Resize(4, 4);
        Assert.Equal((0, 1), (screen.CursorX, screen.CursorY));
        Feed(screen, "\u001b8");
        Assert.Equal((0, 2), (screen.CursorX, screen.CursorY));
        Feed(screen, "X");
        Assert.Equal("Xjk ", screen.Line(2));
    }

    [Fact]
    public void PendingCursorRoundtripKeepsNextPrintAtLogicalEnd()
    {
        var screen = new TerminalScreen(4, 4, 10);
        Feed(screen, "abcdefgh\u001b7");
        screen.Resize(8, 4);
        Assert.Equal((7, 0), (screen.CursorX, screen.CursorY));
        screen.Resize(4, 4);
        Assert.Equal((3, 1), (screen.CursorX, screen.CursorY));
        Feed(screen, "\u001b8X");
        Assert.Equal("X   ", screen.Line(2));
        Assert.True(screen.GetLine(1).IsWrapped);
    }

    [Theory]
    [InlineData("\x1b[1;1H\x1b[2K")]
    [InlineData("\x1b[1;1H\x1b[X")]
    [InlineData("\x1b[1;1H\x1b[@")]
    [InlineData("\x1b[1;1H\x1b[P")]
    public void EraseAndCharacterEditsResetWrapMetadata(string edit)
    {
        var screen = new TerminalScreen(3, 3);
        Feed(screen, "abcdefg");
        Assert.True(screen.GetLine(0).IsWrapped);
        var version = screen.GetLine(0).Version;
        Feed(screen, edit);
        Assert.False(screen.GetLine(0).IsWrapped);
        Assert.Equal(0, screen.GetLine(0).WrappedLength);
        Assert.NotEqual(version, screen.GetLine(0).Version);
    }

    [Fact]
    public void RecyclingAndAlternateClearNeverRetainWrapMetadata()
    {
        var screen = new TerminalScreen(2, 2, 1);
        Feed(screen, "abcdefghi");
        Assert.False(screen.GetLine(1).IsWrapped);
        Assert.True(screen.GetScrollbackLine(0).IsWrapped);
        Feed(screen, "\r\n\r\n");
        Assert.All(TranscriptLines(screen), line => Assert.False(line.IsWrapped));
        Feed(screen, "\x1b[?1049habc\x1b[?1047l\x1b[?47h");
        Assert.All(TranscriptLines(screen), line => Assert.False(line.IsWrapped));
    }

    [Fact]
    public void AlternateWidthResizeCropsWithoutReflowAndMainReflowsOffscreen()
    {
        var screen = new TerminalScreen(4, 3, 20);
        Feed(screen, "abcdef\x1b[?47h123456");
        screen.Resize(2, 3);
        Assert.Equal(["12", "56", "  "], Lines(screen));
        Assert.All(TranscriptLines(screen), line => Assert.False(line.IsWrapped));
        Feed(screen, "\x1b[?47l");
        Assert.Equal("abcdef", Transcript(screen));
        Assert.Equal(["ab", "cd", "ef"], Lines(screen));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void ReflowHistoryAndCursorStayBoundedThroughRepeatedResizes(int limit)
    {
        var screen = new TerminalScreen(8, 3, limit);
        Feed(screen, string.Join("\r\n", Enumerable.Range(0, 20).Select(i => $"line-{i}-abcdef")));
        foreach (var width in new[] { 1, 16, 2, 1000, 4 })
        {
            screen.Resize(width, 2);
            Assert.InRange(screen.ScrollbackCount, 0, limit);
            Assert.InRange(screen.CursorX, 0, width - 1);
            Assert.InRange(screen.CursorY, 0, 1);
            AssertValidWideCells(screen);
            Feed(screen, "X");
        }
    }

    [Fact]
    public void ResizeVersionChangesOnlyForChangedGrid()
    {
        var screen = new TerminalScreen(4, 3);
        screen.Resize(4, 3);
        Assert.Equal(0, screen.ResizeVersion);
        screen.Resize(2, 3);
        screen.Resize(2, 1);
        Assert.Equal(2, screen.ResizeVersion);
        Feed(screen, "x");
        Assert.Equal(2, screen.ResizeVersion);
    }

    [Fact]
    public void ShrinkingWidthKeepsBottomPromptBelowForegroundAttributedErasedBlanks()
    {
        var screen = new TerminalScreen(80, 10, 100);
        Feed(screen, string.Join("\r\n", Enumerable.Range(0, 20).Select(i => $"output {i}")));
        Feed(screen, "\r\n\x1b[37mPS test> \x1b[K");
        Assert.Equal(9, screen.CursorY);
        screen.Resize(79, 10);
        Assert.Equal(9, screen.CursorY);
        Feed(screen, "dir");
        Assert.Contains("dir", screen.Line(9));
        Assert.DoesNotContain(Enumerable.Range(0, 9).Select(screen.Line), line => line.Contains("dir"));
    }

    [Fact]
    public void HostAbsoluteCursorPositionAfterPredictionOverridesBottomPromptPlacement()
    {
        var screen = new TerminalScreen(80, 10, 100);
        Feed(screen, string.Join("\r\n", Enumerable.Range(0, 20).Select(i => $"output {i}")));
        Feed(screen, "\r\nPS C:\\Users\\test> ");
        screen.Resize(79, 10);
        Assert.Equal(9, screen.CursorY);
        // PSReadLine prediction fills the final row, then explicitly redraws input on row nine.
        Feed(screen, new string('p', 79 - screen.CursorX));
        Feed(screen, "\r\n\x1b[93m\x1b[9;20Hdir" + new string(' ', 57) +
            "\x1b[m\r\n\x1b[9;23H");
        Assert.Contains("dir", screen.Line(8));
        Assert.DoesNotContain("dir", screen.Line(9));
        Assert.Equal((22, 8), (screen.CursorX, screen.CursorY));
    }

    [Fact]
    public void ApplicationCursorAndKeypadModesToggleIndependently()
    {
        var screen = new TerminalScreen(10, 2);
        Feed(screen, "\x1b[?1;25h\x1b=");
        Assert.True(screen.ApplicationCursorKeys);
        Assert.True(screen.CursorVisible);
        Assert.True(screen.ApplicationKeypad);
        Feed(screen, "\x1b[?1l\x1b[?25l\x1b>");
        Assert.False(screen.ApplicationCursorKeys);
        Assert.False(screen.CursorVisible);
        Assert.False(screen.ApplicationKeypad);
    }

    [Fact]
    public void BracketedPasteModeHandlesEveryEscapeSplit()
    {
        foreach (var enabled in new[] { true, false })
        {
            var bytes = Encoding.UTF8.GetBytes(enabled ? "\x1b[?1;2004h" : "\x1b[?1;2004l");
            for (var split = 0; split <= bytes.Length; split++)
            {
                var screen = new TerminalScreen(10, 2);
                if (!enabled) Feed(screen, "\x1b[?2004h");
                screen.Feed(bytes.AsSpan(0, split));
                screen.Feed(bytes.AsSpan(split));
                Assert.Equal(enabled, screen.BracketedPaste);
                Assert.Equal(enabled, screen.ApplicationCursorKeys);
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("a\r\nb\nc\rd")]
    [InlineData("caf\u00e9\ttext")]
    public void PasteEncodingProducesExactUtf8BytesInBothModes(string text)
    {
        var screen = new TerminalScreen(10, 2);
        var normalized = text.Replace("\r\n", "\n").Replace('\n', '\r');
        Assert.False(screen.BracketedPaste);
        Assert.Equal(Encoding.UTF8.GetBytes(normalized), Encoding.UTF8.GetBytes(screen.EncodePaste(text)));

        Feed(screen, "\x1b[?2004h");
        var expected = text.Length == 0 ? "" : "\x1b[200~" + normalized + "\x1b[201~";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(screen.EncodePaste(text)));

        Feed(screen, "\x1b[?2004l");
        Assert.Equal(normalized, screen.EncodePaste(text));
    }

    [Fact]
    public void BracketedPasteModePersistsAcrossScreenTransitionsUntilExplicitlyDisabled()
    {
        var screen = new TerminalScreen(10, 2);
        Feed(screen, "\x1b[?2004h\x1b[?1049h");
        Assert.True(screen.BracketedPaste);
        Feed(screen, "\x1b[?1049l");
        Assert.True(screen.BracketedPaste);
        Feed(screen, "\x1b[?2004l");
        Assert.False(screen.BracketedPaste);
    }

    [Fact]
    public void ScrolledLinesEnterBoundedScrollbackOldestFirst()
    {
        var screen = new TerminalScreen(4, 2, scrollbackLines: 3);
        Feed(screen, string.Join("\r\n", Enumerable.Range(0, 7).Select(i => $"L{i}")));
        Assert.Equal(3, screen.ScrollbackCount);
        Assert.Equal(5, screen.ScrolledLines);
        Assert.Equal(["L2  ", "L3  ", "L4  "], Enumerable.Range(0, 3).Select(i => screen.GetScrollbackLine(i).Text(4)));
        Assert.Equal(["L5  ", "L6  "], [screen.Line(0), screen.Line(1)]);
    }

    [Fact]
    public void ViewportOffsetsReadHistoryThenScreen()
    {
        var screen = new TerminalScreen(4, 2, scrollbackLines: 100);
        Feed(screen, "a\r\nb\r\nc\r\nd");
        Assert.Equal(2, screen.ScrollbackCount);
        Assert.Equal("c   ", screen.GetViewportLine(0, 0).Text(4));
        Assert.Equal("b   ", screen.GetViewportLine(0, 1).Text(4));
        Assert.Equal("c   ", screen.GetViewportLine(1, 1).Text(4));
        Assert.Equal("a   ", screen.GetViewportLine(0, 99).Text(4));
    }

    [Fact]
    public void ZeroScrollbackKeepsNoHistory()
    {
        var screen = new TerminalScreen(4, 2);
        Feed(screen, "a\r\nb\r\nc\r\nd");
        Assert.Equal(0, screen.ScrollbackCount);
        Assert.Equal(["c   ", "d   "], [screen.Line(0), screen.Line(1)]);
    }

    [Fact]
    public void EraseDisplayKeepsHistoryAndMode3ClearsIt()
    {
        var screen = new TerminalScreen(4, 2, scrollbackLines: 100);
        Feed(screen, "a\r\nb\r\nc\x1b[2J");
        Assert.Equal(1, screen.ScrollbackCount);
        Assert.Equal("    ", screen.Line(1));
        Feed(screen, "\x1b[3J");
        Assert.Equal(0, screen.ScrollbackCount);
    }

    [Fact]
    public void HistoryKeepsColorsAndTrimsOnlyDefaultTrailingBlanks()
    {
        var screen = new TerminalScreen(6, 1, scrollbackLines: 10);
        Feed(screen, "\x1b[41mab\x1b[0m\r\nx");
        var line = screen.GetScrollbackLine(0);
        Assert.Equal(2, line.Length);
        Assert.Equal(new TerminalColor(1), line[1].Background);
        Feed(screen, "\x1b[44m\x1b[2K\r\n");
        Assert.Equal(6, screen.GetScrollbackLine(1).Length);
    }

    [Fact]
    public void ShrinkingScrollbackLimitKeepsNewestLines()
    {
        var screen = new TerminalScreen(4, 1, scrollbackLines: 10);
        Feed(screen, string.Join("\r\n", Enumerable.Range(0, 6).Select(i => $"L{i}")));
        screen.ScrollbackLimit = 2;
        Assert.Equal(["L3  ", "L4  "], Enumerable.Range(0, 2).Select(i => screen.GetScrollbackLine(i).Text(4)));
        Feed(screen, "\r\nL6");
        Assert.Equal(["L4  ", "L5  "], Enumerable.Range(0, 2).Select(i => screen.GetScrollbackLine(i).Text(4)));
    }

    [Fact]
    public void LineVersionChangesOnlyWhenContentChanges()
    {
        var screen = new TerminalScreen(4, 2);
        var top = screen.GetLine(0);
        var bottom = screen.GetLine(1);
        var (topVersion, bottomVersion) = (top.Version, bottom.Version);
        Feed(screen, "x");
        Assert.NotEqual(topVersion, top.Version);
        Assert.Equal(bottomVersion, bottom.Version);
    }

    [Fact]
    public void LargeOutputThroughputIsBounded()
    {
        var screen = new TerminalScreen(120, 32, scrollbackLines: 10000);
        var chunk = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 1000)
            .Select(i => $"\x1b[3{i % 8}mline {i} {new string('x', 80)}\x1b[0m\r\n")));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var total = 0;
        while (total < 16 << 20) { screen.Feed(chunk); total += chunk.Length; }
        watch.Stop();
        output.WriteLine($"Parsed 16 MiB of colored lines in {watch.Elapsed.TotalMilliseconds:F0} ms ({16 / watch.Elapsed.TotalSeconds:F1} MiB/s).");
        Assert.Equal(10000, screen.ScrollbackCount);
        // Generous bound to catch pathological regressions, not to benchmark.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"16 MiB took {watch.Elapsed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void UnsupportedOscHasNoVisibleOrClipboardSideEffects()
    {
        var screen = new TerminalScreen(10, 2);
        Feed(screen, "\x1b]52;c;c2VjcmV0\aOK\x1b]0;title\x1b\\!");
        Assert.StartsWith("OK!", screen.Line(0));
    }

    [Fact]
    public void CursorQueryRepliesWithCurrentPosition()
    {
        var screen = new TerminalScreen(10, 4);
        var replies = new List<string>();
        screen.Reply += replies.Add;
        Feed(screen, "\x1b[3;4H\x1b[6n");
        Assert.Equal(["\x1b[3;4R"], replies);
    }

    [Fact]
    public void PaletteAndResetAreRetainedPerCell()
    {
        var screen = new TerminalScreen(10, 2);
        Feed(screen, "\x1b[91;48;5;123mX\x1b[0mY");
        Assert.Equal(new TerminalColor(9), screen[0, 0].Foreground);
        Assert.Equal(new TerminalColor(123), screen[0, 0].Background);
        Assert.Equal(TerminalColor.Default, screen[0, 1].Foreground);
    }

    [Fact]
    public void OversizedCsiRecoversAtFinalByte()
    {
        var screen = new TerminalScreen(10, 2);
        Feed(screen, "\x1b[" + new string('1', 1000) + "mOK");
        Assert.StartsWith("OK", screen.Line(0));
    }

    [Fact]
    public void CharsetDesignationConsumesOnlyItsDesignator()
    {
        var screen = new TerminalScreen(10, 2);
        Feed(screen, "\x1b(BOK");
        Assert.StartsWith("OK", screen.Line(0));
    }
}
