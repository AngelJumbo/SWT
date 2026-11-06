using System.IO;
using System.Text.Json;

namespace SWT.Desktop;

internal sealed class DiagnosticLog : IDisposable
{
    private readonly StreamWriter _writer;

    internal string Path { get; }

    internal DiagnosticLog()
    {
        var directory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SWT", "logs");
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
        _writer = new StreamWriter(new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    internal void Write(object entry)
    {
        if (_writer.BaseStream.Position > 10 * 1024 * 1024)
            throw new IOException("Diagnostic log reached its 10 MiB limit.");
        _writer.WriteLine(JsonSerializer.Serialize(entry));
    }

    public void Dispose() => _writer.Dispose();
}
