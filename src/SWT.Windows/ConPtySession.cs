using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace SWT.Windows;

public sealed class ConPtySession : IAsyncDisposable
{
    private readonly PseudoConsoleHandle _console;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly SafeFileHandle _job;
    private readonly Process _process;
    private readonly Channel<byte[]> _writes = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
    {
        FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false
    });
    private readonly Task _writer;
    private readonly Task _reader;
    private readonly Task _completion;
    private int _closing;

    private ConPtySession(PseudoConsoleHandle console, SafeFileHandle input, SafeFileHandle output,
        SafeFileHandle job, Process process, Func<ReadOnlyMemory<byte>, Task> onOutput)
    {
        _console = console;
        _job = job;
        _process = process;
        _input = new FileStream(input, FileAccess.Write, 4096, false);
        _output = new FileStream(output, FileAccess.Read, 4096, false);
        _reader = Task.Run(async () =>
        {
            var buffer = new byte[8192];
            int count;
            try
            {
                while ((count = await _output.ReadAsync(buffer).ConfigureAwait(false)) != 0)
                    await onOutput(buffer.AsMemory(0, count)).ConfigureAwait(false);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 109) { /* ConPTY pipe EOF. */ }
            catch
            {
                _job.Dispose();
                throw;
            }
        });
        _writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var bytes in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    await _input.WriteAsync(bytes).ConfigureAwait(false);
                    await _input.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _writes.Writer.TryComplete(ex);
                _job.Dispose();
                throw;
            }
        });
        _completion = CompleteAsync();
    }

    public int ProcessId => _process.Id;
    public int ExitCode => _process.ExitCode;
    public Task Completion => _completion;

    private async Task CompleteAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        // Close can block until final output is drained; the reader stays active.
        await Task.Run(_console.Dispose).ConfigureAwait(false);
        await _reader.ConfigureAwait(false);
        if (_writer.IsFaulted) await _writer.ConfigureAwait(false);
    }

    public static ConPtySession Start(string executable, IReadOnlyList<string> arguments, string directory,
        int columns, int rows, Func<ReadOnlyMemory<byte>, Task> onOutput)
    {
        ArgumentNullException.ThrowIfNull(onOutput);
        ValidateSize(columns, rows);
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new FileNotFoundException("Shell executable must resolve to an existing absolute path.", executable);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);

        Check(Native.CreatePipe(out var inputRead, out var inputWrite, 0, 0));
        using (inputRead)
        {
            SafeFileHandle? outputRead = null, outputWrite = null, job = null;
            PseudoConsoleHandle? console = null;
            Process? managedProcess = null;
            nint attributes = 0;
            var initialized = false;
            var pi = new Native.ProcessInformation();
            try
            {
                Check(Native.CreatePipe(out outputRead, out outputWrite, 0, 0));
                Marshal.ThrowExceptionForHR(Native.CreatePseudoConsole(new((short)columns, (short)rows), inputRead, outputWrite, 0, out console));
                nuint size = 0;
                Native.InitializeProcThreadAttributeList(0, 1, 0, ref size);
                if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                attributes = Marshal.AllocHGlobal(checked((int)size));
                Check(Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
                initialized = true;
                Check(Native.UpdateProcThreadAttribute(attributes, 0, 0x00020016,
                    console.DangerousGetHandle(), (nuint)IntPtr.Size, 0, 0));
                job = Native.CreateJobObjectW(0, null);
                if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var limits = new Native.ExtendedLimitInformation
                {
                    BasicLimitInformation = new() { LimitFlags = 0x2000 } // KILL_ON_JOB_CLOSE
                };
                Check(Native.SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<Native.ExtendedLimitInformation>()));
                var startup = new Native.StartupInfoEx
                {
                    // Null standard handles request ConPTY defaults, not the host's redirected streams.
                    StartupInfo = new() { Size = Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x100 },
                    AttributeList = attributes
                };
                var command = new StringBuilder(QuoteArgument(executable));
                foreach (var argument in arguments) command.Append(' ').Append(QuoteArgument(argument));
                // Suspended startup closes the gap between process creation and job ownership.
                Check(Native.CreateProcessW(executable, command, 0, 0, false, 0x00080004, 0, directory, ref startup, out pi));
                Check(Native.AssignProcessToJobObject(job, pi.Process));
                managedProcess = Process.GetProcessById(pi.ProcessId);
                if (Native.ResumeThread(pi.Thread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                inputRead.Dispose();
                outputWrite.Dispose();
                return new ConPtySession(console, inputWrite, outputRead, job, managedProcess, onOutput);
            }
            catch
            {
                // Cleanup only; the original failure is always rethrown.
                if (pi.Process != 0) Native.TerminateProcess(pi.Process, 1);
                job?.Dispose();
                managedProcess?.Dispose();
                inputWrite.Dispose();
                outputRead?.Dispose();
                outputWrite?.Dispose();
                console?.Dispose();
                throw;
            }
            finally
            {
                if (pi.Thread != 0) Native.CloseHandle(pi.Thread);
                if (pi.Process != 0) Native.CloseHandle(pi.Process);
                if (initialized) Native.DeleteProcThreadAttributeList(attributes);
                if (attributes != 0) Marshal.FreeHGlobal(attributes);
            }
        }
    }

    public ValueTask WriteAsync(string text, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _closing) != 0) throw new ObjectDisposedException(nameof(ConPtySession));
        return _writes.Writer.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken);
    }

    public void Resize(int columns, int rows)
    {
        ValidateSize(columns, rows);
        if (Volatile.Read(ref _closing) != 0 || _console.IsClosed) return;
        try { Marshal.ThrowExceptionForHR(Native.ResizePseudoConsole(_console, new((short)columns, (short)rows))); }
        catch (ObjectDisposedException) when (_console.IsClosed)
        {
            // Process exit can close ConPTY between the size check and the native call.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) return;
        _writes.Writer.TryComplete();
        _job.Dispose();
        try
        {
            await _completion.ConfigureAwait(false);
            await _writer.ConfigureAwait(false);
        }
        catch (IOException ex) when (_process.HasExited && (ex.HResult & 0xFFFF) is 109 or 232)
        {
            // Buffered input may hit a broken pipe after the owned process is stopped.
            await _reader.ConfigureAwait(false);
        }
        finally
        {
            _console.Dispose();
            _input.Dispose();
            _output.Dispose();
            _process.Dispose();
        }
    }

    private static void ValidateSize(int columns, int rows)
    {
        if (columns is < 1 or > 1000 || rows is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(columns));
    }

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static string QuoteArgument(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("Arguments cannot contain NUL.", nameof(value));
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
