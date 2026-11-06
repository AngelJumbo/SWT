using System.Diagnostics;
using System.IO;
using System.Text;
using SWT.Core;
using SWT.Windows;
using Xunit;

namespace SWT.Tests;

[CollectionDefinition("ConPTY", DisableParallelization = true)]
public sealed class ConPtyCollection;

[Collection("ConPTY")]
public sealed class ConPtyTests
{
    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static string DirectoryPath => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("abc", "abc")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\", "a\\")]
    public void WindowsCommandLineQuoting(string input, string expected) =>
        Assert.Equal(expected, ConPtySession.QuoteArgument(input));

    [Fact]
    public async Task ShellReadsInputResizesAndExits()
    {
        var output = new StringBuilder();
        await using var session = ConPtySession.Start(Cmd, ["/d", "/q"], DirectoryPath, 80, 24, bytes =>
        {
            lock (output) output.Append(Encoding.UTF8.GetString(bytes.Span));
            return Task.CompletedTask;
        });
        session.Resize(100, 30);
        await session.WriteAsync("echo SWT_INTEGRATION\r");
        await session.WriteAsync("exit 7\r");
        var completed = await Task.WhenAny(session.Completion, Task.Delay(TimeSpan.FromSeconds(15)));
        lock (output) Assert.True(completed == session.Completion, $"Shell did not exit. Output: {output}");
        await session.Completion;
        Assert.Equal(7, session.ExitCode);
        lock (output) Assert.Contains("SWT_INTEGRATION", output.ToString());
    }

    [Fact]
    public async Task ClosingLiveSessionTerminatesOwnedShell()
    {
        var session = ConPtySession.Start(Cmd, ["/d", "/q"], DirectoryPath, 80, 24, _ => Task.CompletedTask);
        using var process = Process.GetProcessById(session.ProcessId);
        await session.WriteAsync("\x03");
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(process.HasExited);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task RepeatedStartStopDoesNotLeaveShells()
    {
        using var host = Process.GetCurrentProcess();
        int? baseline = null;
        for (var i = 0; i < 110; i++)
        {
            var output = new StringBuilder();
            var session = ConPtySession.Start(Cmd, ["/d", "/c", "echo cycle"], DirectoryPath, 40, 10, bytes =>
            {
                output.Append(Encoding.UTF8.GetString(bytes.Span));
                return Task.CompletedTask;
            });
            using var process = Process.GetProcessById(session.ProcessId);
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, session.ExitCode);
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(process.HasExited);
            Assert.Contains("cycle", output.ToString());
            host.Refresh();
            if (i == 9)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                host.Refresh();
                baseline = host.HandleCount;
            }
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        host.Refresh();
        Assert.True(host.HandleCount <= baseline + 16, $"Native handle count grew from {baseline} to {host.HandleCount}.");
    }

    [Fact]
    public async Task PowerShellRunsWithNoProfile()
    {
        var output = new StringBuilder();
        await using var session = ConPtySession.Start(ShellResolver.Resolve(""),
            ["-NoLogo", "-NoProfile", "-Command", "Write-Output 'POWERSHELL_OK'; exit 9"],
            DirectoryPath, 80, 24, bytes =>
            {
                output.Append(Encoding.UTF8.GetString(bytes.Span));
                return Task.CompletedTask;
            });
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(9, session.ExitCode);
        Assert.Contains("POWERSHELL_OK", output.ToString());
    }

    [Fact]
    public async Task CtrlCInterruptsRunningPowerShellCommand()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        await using var session = ConPtySession.Start(ShellResolver.Resolve(""),
            ["-NoLogo", "-NoProfile", "-NoExit", "-Command", "Write-Output 'INTERRUPT_READY'; Start-Sleep -Seconds 120"],
            DirectoryPath, 80, 24, bytes =>
            {
                lock (output)
                {
                    output.Append(Encoding.UTF8.GetString(bytes.Span));
                    var text = output.ToString();
                    if (text.Contains("INTERRUPT_READY", StringComparison.Ordinal)) ready.TrySetResult();
                    if (text.Contains("PS ", StringComparison.Ordinal) && text.Contains('>')) prompt.TrySetResult();
                }
                return Task.CompletedTask;
            });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await session.WriteAsync("\x03");
        await prompt.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await session.WriteAsync("exit 4\r");
        var completed = await Task.WhenAny(session.Completion, Task.Delay(TimeSpan.FromSeconds(20)));
        lock (output) Assert.True(completed == session.Completion, $"Ctrl+C did not restore prompt. Output: {output}");
        await session.Completion;
        Assert.Equal(4, session.ExitCode);
    }

    [Fact]
    public async Task PowerShellInputAfterScrollingDoesNotOverwritePreviousOutput()
    {
        var initialPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var typedCommand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        var screen = new TerminalScreen(80, 10, 100);
        await using var session = ConPtySession.Start(ShellResolver.Resolve(""),
            ["-NoLogo", "-NoProfile", "-NoExit"], DirectoryPath, 80, 10, bytes =>
            {
                lock (output)
                {
                    output.Append(Encoding.UTF8.GetString(bytes.Span));
                    screen.Feed(bytes.Span);
                    var text = output.ToString();
                    if (text.Contains("PS ", StringComparison.Ordinal) && text.Contains('>'))
                        initialPrompt.TrySetResult();
                    var rows = Enumerable.Range(0, screen.Rows).Select(screen.Line).ToArray();
                    var marker = Array.FindIndex(rows, row => row.TrimEnd() == "SWT_READY");
                    if (marker >= 0 && rows.Skip(marker + 1).Any(row =>
                        row.StartsWith("PS ", StringComparison.Ordinal) && row.Contains('>')))
                        commandPrompt.TrySetResult();
                    if (marker >= 0 && screen.Line(screen.CursorY).Contains("> dir", StringComparison.Ordinal))
                        typedCommand.TrySetResult();
                }
                return Task.CompletedTask;
            });

        await initialPrompt.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await session.WriteAsync("1..20 | ForEach-Object { \"SWT_LINE_$_\" }; Write-Output 'SWT_READY'\r");
        await commandPrompt.Task.WaitAsync(TimeSpan.FromSeconds(20));
        lock (output)
        {
            session.Resize(79, 10);
            screen.Resize(79, 10);
        }
        await Task.Delay(250);
        await session.WriteAsync("dir");
        await typedCommand.Task.WaitAsync(TimeSpan.FromSeconds(10));

        lock (output)
        {
            var rows = Enumerable.Range(0, screen.Rows).Select(screen.Line).ToArray();
            var occurrences = rows.Sum(row => CountOccurrences(row, "dir"));
            Assert.Equal(1, occurrences);
            Assert.Contains("> dir", rows[screen.CursorY]);
            Assert.DoesNotContain(rows.Where((_, row) => row != screen.CursorY),
                row => row.Contains("dir", StringComparison.Ordinal));
            Assert.Contains(rows, row => row.TrimEnd() == "SWT_LINE_20");
            Assert.Contains(rows, row => row.TrimEnd() == "SWT_READY");
        }
    }

    [Fact]
    public async Task PowerShellClearHostErasesOldOutputFromVisibleScreen()
    {
        var prompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new StringBuilder();
        var screen = new TerminalScreen(80, 10, 100);
        await using var session = ConPtySession.Start(ShellResolver.Resolve(""),
            ["-NoLogo", "-NoProfile", "-NoExit"], DirectoryPath, 80, 10, bytes =>
            {
                lock (output)
                {
                    output.Append(Encoding.UTF8.GetString(bytes.Span));
                    screen.Feed(bytes.Span);
                    var text = output.ToString();
                    if (text.Contains("PS ", StringComparison.Ordinal)) prompt.TrySetResult();
                    if (text.Contains("SWT_LIST_DONE", StringComparison.Ordinal)) listed.TrySetResult();
                    if (text.Contains("SWT_CLEAR_DONE", StringComparison.Ordinal)) cleared.TrySetResult();
                }
                return Task.CompletedTask;
            });

        await prompt.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await session.WriteAsync("1..20 | ForEach-Object { \"SWT_OLD$_\" }; Write-Output ('SWT_' + 'LIST_DONE')\r");
        await listed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var beforeClear = string.Join(Environment.NewLine, Enumerable.Range(0, screen.Rows).Select(screen.Line));
        Assert.True(beforeClear.Contains("SWT_LIST_DONE", StringComparison.Ordinal),
            $"Before clear: {beforeClear}; scrollback={screen.ScrollbackCount}, cursor={screen.CursorX},{screen.CursorY}");
        await session.WriteAsync("Clear-Host; Write-Output ('SWT_' + 'CLEAR_DONE')\r");
        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var visible = string.Join(Environment.NewLine, Enumerable.Range(0, screen.Rows).Select(screen.Line));
        lock (output) Assert.Contains("\x1b[2J", output.ToString());
        Assert.Contains("SWT_CLEAR_DONE", visible);
        Assert.DoesNotContain("SWT_LIST_DONE", visible);
        Assert.DoesNotContain("SWT_OLD", visible);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
            count++;
        return count;
    }

    [Fact]
    public void MissingShellIsExplicitError() =>
        Assert.Throws<FileNotFoundException>(() =>
            ConPtySession.Start(@"C:\not-a-winterm-shell.exe", [], DirectoryPath, 80, 24, _ => Task.CompletedTask));
}
