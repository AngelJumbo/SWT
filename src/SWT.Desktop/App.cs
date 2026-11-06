using System.IO;
using System.Windows;
using SWT.Core;

namespace SWT.Desktop;

public sealed class App : Application
{
    [STAThread]
    public static int Main(string[] args)
    {
        var app = new App();
        try
        {
            var smoke = args.Contains("--smoke-test", StringComparer.Ordinal);
            using var diagnostics = args.Contains("--diagnostics", StringComparer.Ordinal) ? new DiagnosticLog() : null;
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SWT", "config.toml");
            Configuration configuration;
            if (smoke) configuration = Configuration.Parse(Configuration.DefaultToml);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path))
                {
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                    using var writer = new StreamWriter(file);
                    writer.Write(Configuration.DefaultToml);
                }
                try { configuration = Configuration.Parse(File.ReadAllText(path)); }
                catch (ConfigurationException ex)
                {
                    if (MessageBox.Show($"{path}\n\n{ex.Message}\n\nStart with defaults without changing the file?",
                        "SWT configuration", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return 1;
                    configuration = Configuration.Parse(Configuration.DefaultToml);
                }
            }
            return app.Run(new MainWindow(configuration, path, smoke, diagnostics));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigurationException)
        {
            MessageBox.Show(ex.Message, "SWT startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
