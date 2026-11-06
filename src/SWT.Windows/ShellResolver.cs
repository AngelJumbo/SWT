namespace SWT.Windows;

public static class ShellResolver
{
    public static string Resolve(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Find(configured) ?? throw new FileNotFoundException("Configured shell was not found.", configured);
        return Find("pwsh.exe") ??
            Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) ??
            throw new FileNotFoundException("Neither PowerShell 7 nor Windows PowerShell was found.");
    }

    private static string? Find(string executable)
    {
        if (Path.IsPathFullyQualified(executable)) return Existing(executable);
        if (executable.IndexOfAny(['\\', '/']) >= 0)
            throw new ArgumentException("Use an absolute shell path or an executable name on PATH.", nameof(executable));
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Existing(Path.Combine(directory.Trim('"'), executable));
            if (candidate is not null) return candidate;
        }
        return null;
    }

    private static string? Existing(string path) => File.Exists(path) ? Path.GetFullPath(path) : null;
}
