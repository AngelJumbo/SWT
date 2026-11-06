using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using SWT.Core;
using SWT.Windows;

namespace SWT.Desktop;

public sealed class MainWindow : Window
{
    private const double ResizeBorderSize = 6;
    private Configuration _configuration;
    private readonly string _configPath;
    private readonly bool _smoke;
    private readonly TerminalScreen _screen;
    private readonly TerminalView _view;
    private readonly Border _title;
    private readonly Border _windowFrame;
    private readonly Border _windowBorder;
    private readonly Grid _windowContent;
    private readonly Grid _frameContent;
    private readonly WindowChrome _windowChrome;
    private readonly Button _maximizeButton;
    private readonly Border _searchBar;
    private readonly TextBox _searchBox;
    private readonly TextBlock _searchStatus;
    private readonly CheckBox _regexSearch;
    private readonly DispatcherTimer _reloadTimer;
    private readonly FileSystemWatcher? _watcher;
    private readonly List<string> _replies = [];
    private ConPtySession? _session;
    private bool _closing, _closed, _exited;
    private bool _rightClickHandledLocally;
    private string? _fontWarning;
    private readonly DispatcherTimer _smokeTimeout;
    // ConPTY output is coalesced so the UI thread parses large batches once instead of one dispatch per read.
    private const int MaxPendingOutput = 1 << 20;
    private readonly Lock _outputLock = new();
    private byte[] _incoming = new byte[65536], _spare = new byte[65536];
    private int _incomingLength;
    private bool _drainQueued;
    private TaskCompletionSource? _drainWaiter;
    private DiagnosticLog? _diagnostics;
    private int? _ptyColumns, _ptyRows;
    private (double Width, double Height, double ViewWidth, double ViewHeight)? _lastLayout;

    public MainWindow(Configuration configuration, string configPath, bool smoke)
        : this(configuration, configPath, smoke, null) { }

    internal MainWindow(Configuration configuration, string configPath, bool smoke, DiagnosticLog? diagnostics)
    {
        _diagnostics = diagnostics;
        _configuration = configuration;
        _configPath = configPath;
        _smoke = smoke;
        Title = "SWT";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        MinWidth = 320;
        MinHeight = 180;
        _windowChrome = new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(ResizeBorderSize),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(configuration.Appearance.CornerRadius)
        };
        WindowChrome.SetWindowChrome(this, _windowChrome);
        _screen = new TerminalScreen(configuration.Appearance.Columns, configuration.Appearance.Rows,
            configuration.Terminal.ScrollbackLines);
        _screen.Reply += _replies.Add;
        var appearance = CheckFont(configuration.Appearance);
        _view = new TerminalView(_screen, appearance);
        Width = configuration.Appearance.Columns * _view.CellWidth + 28;
        Height = configuration.Appearance.Rows * _view.CellHeight + 52 + ResizeBorderSize * 2;
        _windowContent = new Grid();
        _frameContent = new Grid();
        _frameContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _frameContent.RowDefinitions.Add(new RowDefinition());
        var chrome = new Grid();
        chrome.ColumnDefinitions.Add(new ColumnDefinition());
        chrome.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleContent = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0), IsHitTestVisible = false
        };
        titleContent.Children.Add(new TextBlock
        {
            Text = ">_", Foreground = new SolidColorBrush(Color.FromRgb(129, 161, 193)),
            FontFamily = new FontFamily("Cascadia Mono"), FontSize = 12, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
        });
        titleContent.Children.Add(new TextBlock
        {
            Text = "SWT", Foreground = new SolidColorBrush(Color.FromRgb(216, 222, 233)),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 12, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        chrome.Children.Add(titleContent);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Button("M 1,9 H 9", () => WindowState = WindowState.Minimized));
        _maximizeButton = Button("M 1,1 H 9 V 9 H 1 Z", ToggleMaximize);
        buttons.Children.Add(_maximizeButton);
        buttons.Children.Add(Button("M 2,2 L 8,8 M 8,2 L 2,8", Close, isClose: true));
        Grid.SetColumn(buttons, 1);
        chrome.Children.Add(buttons);
        _title = new Border
        {
            Child = chrome, Height = 36,
            Background = new SolidColorBrush(Color.FromRgb(32, 36, 44)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(54, 61, 73)),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        _title.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else DragMove();
        };
        StateChanged += (_, _) => _maximizeButton.Content = WindowIcon(
            WindowState == WindowState.Maximized ? "M 3,1 H 9 V 7 H 7 M 7,3 H 1 V 9 H 7 Z" : "M 1,1 H 9 V 9 H 1 Z");
        Grid.SetRow(_view, 1);
        _frameContent.Children.Add(_title);
        _frameContent.Children.Add(_view);
        _windowFrame = new Border
        {
            Background = WindowBackgroundBrush(appearance),
            BorderBrush = WindowBackgroundBrush(appearance),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(appearance.CornerRadius),
            Child = _frameContent
        };
        _windowContent.Children.Add(_windowFrame);
        _windowBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(101, 112, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(appearance.CornerRadius),
            IsHitTestVisible = false
        };
        _windowContent.Children.Add(_windowBorder);
        _searchBox = new TextBox
        {
            MaxLength = 4096, ToolTip = "Search text (case-insensitive)",
            Background = new SolidColorBrush(Color.FromRgb(25, 29, 36)),
            Foreground = Brushes.WhiteSmoke, CaretBrush = Brushes.WhiteSmoke,
            BorderThickness = new Thickness(0, 0, 0, 2),
            Padding = new Thickness(8, 5, 8, 5), FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center, MinWidth = 0
        };
        System.Windows.Automation.AutomationProperties.SetName(_searchBox, "Search terminal history");
        var inputStyle = new Style(typeof(TextBox));
        inputStyle.Setters.Add(new Setter(Control.BorderBrushProperty,
            new SolidColorBrush(Color.FromRgb(70, 80, 95))));
        var inputFocus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        inputFocus.Setters.Add(new Setter(Control.BorderBrushProperty,
            new SolidColorBrush(Color.FromRgb(129, 161, 193))));
        inputStyle.Triggers.Add(inputFocus);
        _searchBox.Style = inputStyle;
        _searchStatus = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(235, 174, 145)),
            FontSize = 12, Margin = new Thickness(8, 5, 0, 0), Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };
        var searchControls = new Grid();
        searchControls.ColumnDefinitions.Add(new ColumnDefinition());
        searchControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchControls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        searchControls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        searchControls.Children.Add(_searchBox);
        var searchActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        searchActions.Children.Add(SearchButton("M 2,7 L 6,3 L 10,7 M 6,3 V 11",
            "Previous match (Shift+Enter)", () => FindText(previous: true)));
        searchActions.Children.Add(SearchButton("M 2,7 L 6,11 L 10,7 M 6,3 V 11",
            "Next match (Enter)", () => FindText(previous: false)));
        _regexSearch = new CheckBox
        {
            Content = ".*", Foreground = Brushes.WhiteSmoke, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0), ToolTip = "Use regular expressions"
        };
        System.Windows.Automation.AutomationProperties.SetName(_regexSearch, "Use regular expressions");
        _regexSearch.Checked += (_, _) => ResetSearch();
        _regexSearch.Unchecked += (_, _) => ResetSearch();
        searchActions.Children.Add(_regexSearch);
        searchActions.Children.Add(SearchButton("M 3,3 L 11,11 M 11,3 L 3,11",
            "Close search (Escape)", CloseSearch));
        Grid.SetColumn(searchActions, 1);
        searchControls.Children.Add(searchActions);
        Grid.SetRow(_searchStatus, 1);
        Grid.SetColumnSpan(_searchStatus, 2);
        searchControls.Children.Add(_searchStatus);
        _searchBox.TextChanged += (_, _) => ResetSearch();
        _searchBar = new Border
        {
            Child = searchControls, Background = new SolidColorBrush(Color.FromRgb(37, 43, 53)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(62, 72, 87)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8),
            MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed
        };
        var searchBarStyle = new Style(typeof(Border));
        searchBarStyle.Setters.Add(new Setter(MarginProperty, new Thickness(12)));
        var titleVisible = new DataTrigger
        {
            Binding = new System.Windows.Data.Binding(nameof(Visibility)) { Source = _title },
            Value = Visibility.Visible
        };
        titleVisible.Setters.Add(new Setter(MarginProperty, new Thickness(12, _title.Height + 12, 12, 12)));
        searchBarStyle.Triggers.Add(titleVisible);
        _searchBar.Style = searchBarStyle;
        _windowContent.Children.Add(_searchBar);
        _windowContent.SizeChanged += (_, _) => ClipWindowContent(_configuration.Appearance.CornerRadius);
        _frameContent.SizeChanged += (_, _) =>
            ClipFrameContent(Math.Max(0, _configuration.Appearance.CornerRadius - ResizeBorderSize));
        Content = _windowContent;
        _title.Visibility = appearance.Decorations ? Visibility.Visible : Visibility.Collapsed;
        _view.SizeChanged += (_, _) =>
        {
            LogState("view-size-changed");
            ResizeTerminal();
        };
        if (diagnostics is not null)
        {
            SourceInitialized += (_, _) => LogState("source-initialized");
            LayoutUpdated += (_, _) =>
            {
                var layout = (ActualWidth, ActualHeight, _view.ActualWidth, _view.ActualHeight);
                if (_lastLayout == layout) return;
                _lastLayout = layout;
                LogState("layout-updated");
            };
        }
        _view.GotKeyboardFocus += (_, _) => _view.MarkDirty();
        _view.LostKeyboardFocus += (_, _) => _view.MarkDirty();
        Activated += async (_, _) => await SendFocusEvent(true);
        Deactivated += async (_, _) => await SendFocusEvent(false);
        _view.MouseLeftButtonDown += (_, e) =>
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) DragMove();
            else
            {
                _view.Focus();
                _view.BeginSelection(e.GetPosition(_view));
            }
            e.Handled = true;
        };
        _view.MouseMove += (_, e) =>
        {
            if (_view.IsSelecting) _view.UpdateSelection(e.GetPosition(_view));
        };
        _view.MouseLeftButtonUp += (_, e) =>
        {
            _view.EndSelection(e.GetPosition(_view));
            e.Handled = true;
        };
        _view.PreviewMouseDown += MouseDownTerminal;
        _view.PreviewMouseUp += MouseUpTerminal;
        _view.PreviewMouseMove += MouseMoveTerminal;
        _view.PreviewMouseWheel += MouseWheelTerminal;
        PreviewKeyDown += KeyDownTerminal;
        _view.TextInput += async (_, e) =>
        {
            e.Handled = true;
            await Send(e.Text);
        };
        Loaded += async (_, _) => await StartSession();
        LogState("constructed");
        Closing += CloseWindow;
        _reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _reloadTimer.Tick += (_, _) => { _reloadTimer.Stop(); Reload(); };
        _smokeTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _smokeTimeout.Tick += (_, _) => { Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown; Close(); };
        if (!smoke)
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(configPath)!, Path.GetFileName(configPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            _watcher.Changed += (_, _) => ScheduleReload();
            _watcher.Created += (_, _) => ScheduleReload();
            _watcher.Renamed += (_, _) => ScheduleReload();
            _watcher.Deleted += (_, _) => ScheduleReload();
            _watcher.Error += (_, e) => Dispatcher.BeginInvoke(() => Status($"Config watcher failed: {e.GetException().Message}; use Ctrl+Shift+R."));
            _watcher.EnableRaisingEvents = true;
        }
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private static Button Button(string geometry, Action action, bool isClose = false)
    {
        var button = new Button
        {
            Content = WindowIcon(geometry), Width = 46, Height = 35,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            Focusable = false, Cursor = Cursors.Arrow
        };
        var hover = new SolidColorBrush(isClose ? Color.FromRgb(196, 43, 28) : Color.FromRgb(54, 61, 73));
        var pressed = new SolidColorBrush(isClose ? Color.FromRgb(161, 31, 22) : Color.FromRgb(70, 78, 91));
        button.MouseEnter += (_, _) => button.Background = hover;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.PreviewMouseDown += (_, _) => button.Background = pressed;
        button.PreviewMouseUp += (_, _) => button.Background = button.IsMouseOver ? hover : Brushes.Transparent;
        button.Click += (_, e) => { e.Handled = true; action(); };
        return button;
    }

    private static System.Windows.Shapes.Path WindowIcon(string geometry) => new()
    {
        Data = Geometry.Parse(geometry), Width = 10, Height = 10, Stretch = Stretch.Fill,
        Stroke = new SolidColorBrush(Color.FromRgb(216, 222, 233)), StrokeThickness = 1.2,
        StrokeStartLineCap = PenLineCap.Square, StrokeEndLineCap = PenLineCap.Square
    };

    private static Button SearchButton(string geometry, string label, Action action)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(57, 67, 82))));
        style.Triggers.Add(hover);
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(57, 67, 82))));
        style.Triggers.Add(focus);
        var pressed = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(75, 90, 110))));
        style.Triggers.Add(pressed);
        var button = new Button
        {
            Content = WindowIcon(geometry), Width = 30, Height = 32, Margin = new Thickness(1, 0, 1, 0),
            Template = template, Style = style, ToolTip = label, Cursor = Cursors.Hand
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, e) => { e.Handled = true; action(); };
        return button;
    }

    private Appearance CheckFont(Appearance appearance)
    {
        if (Fonts.SystemFontFamilies.Any(f => f.Source.Equals(appearance.FontFamily, StringComparison.OrdinalIgnoreCase)))
        { _fontWarning = null; return appearance; }
        _fontWarning = $"Font '{appearance.FontFamily}' missing; using Consolas.";
        return appearance with { FontFamily = "Consolas" };
    }

    private async Task StartSession()
    {
        try
        {
            LogState("loaded");
            ResizeTerminal();
            var executable = ShellResolver.Resolve(_smoke ? "" : _configuration.Shell.Executable);
            IReadOnlyList<string> arguments = _smoke
                ? ["-NoLogo", "-NoProfile", "-Command", "Write-Output 'SWT_SMOKE'; exit 0"]
                : _configuration.Shell.Arguments;
            var directory = string.IsNullOrEmpty(_configuration.Shell.WorkingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : _configuration.Shell.WorkingDirectory;
            var columns = _screen.Columns;
            var rows = _screen.Rows;
            LogState("conpty-starting");
            _session = ConPtySession.Start(executable, arguments, directory, columns, rows, QueueOutput);
            _ptyColumns = columns;
            _ptyRows = rows;
            LogState("conpty-started");
            // Native startup can pump window messages while the tiling manager changes the layout.
            ResizeTerminal();
            _view.Focus();
            if (_fontWarning is not null) Status(_fontWarning);
            if (_smoke) _smokeTimeout.Start();
            await _session.Completion;
            if (_closing) return;
            DrainOutput();
            _exited = true;
            Title = $"SWT - Session exited ({_session.ExitCode})";
            _view.ToolTip = "Session exited. Close this window to exit.";
            if (_smoke)
            {
                _smokeTimeout.Stop();
                var found = Enumerable.Range(0, _screen.ScrollbackCount + _screen.Rows)
                    .Any(y => _screen.GetViewportLine(y, _screen.ScrollbackCount).Text(_screen.Columns)
                        .Contains("SWT_SMOKE", StringComparison.Ordinal));
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Tag = found && _session.ExitCode == 0 ? 0 : 1;
                Close();
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or ArgumentException or ExternalException or InvalidOperationException)
        {
            _exited = true;
            Status($"Session failed: {ex.Message}");
            if (_smoke) { Tag = 1; Close(); }
        }
    }

    private Task QueueOutput(ReadOnlyMemory<byte> bytes)
    {
        var wait = Task.CompletedTask;
        var schedule = false;
        lock (_outputLock)
        {
            if (_incomingLength + bytes.Length > _incoming.Length)
                Array.Resize(ref _incoming, Math.Max(_incoming.Length * 2, _incomingLength + bytes.Length));
            bytes.Span.CopyTo(_incoming.AsSpan(_incomingLength));
            _incomingLength += bytes.Length;
            if (!_drainQueued) _drainQueued = schedule = true;
            // Backpressure: the reader pauses until the UI catches up.
            if (_incomingLength >= MaxPendingOutput)
                wait = (_drainWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        if (schedule) Dispatcher.BeginInvoke(DrainOutput, DispatcherPriority.Background);
        return wait;
    }

    private void DrainOutput()
    {
        byte[] data;
        int length;
        TaskCompletionSource? waiter;
        lock (_outputLock)
        {
            data = _incoming;
            length = _incomingLength;
            _incoming = _spare;
            _incomingLength = 0;
            _drainQueued = false;
            waiter = _drainWaiter;
            _drainWaiter = null;
        }
        if (length > 0)
        {
            LogState("output-before", length, ControlSequences(data.AsSpan(0, length)));
            _screen.Feed(data.AsSpan(0, length));
            _view.ScreenChanged();
            LogState("output-after", length);
        }
        _spare = data.Length > 4 * 65536 ? new byte[65536] : data;
        waiter?.TrySetResult();
        if (_replies.Count == 0) return;
        string[] replies = [.. _replies];
        _replies.Clear();
        _ = SendReplies(replies);
    }

    private async Task SendReplies(string[] replies)
    {
        try
        {
            foreach (var reply in replies)
                if (_session is not null && !_closing && !_exited) await _session.WriteAsync(reply);
        }
        catch (Exception ex) when (ex is IOException or System.Threading.Channels.ChannelClosedException or ObjectDisposedException)
        { Status($"Reply failed: {ex.Message}"); }
    }

    internal void ResizeTerminal()
    {
        if (_closing || _exited) return;
        var columns = Math.Clamp((int)((_view.ActualWidth - 16) / _view.CellWidth), 1, 1000);
        var rows = Math.Clamp((int)((_view.ActualHeight - 8) / _view.CellHeight), 1, 500);
        LogState("resize-check");
        var screenMatches = columns == _screen.Columns && rows == _screen.Rows;
        var conptyMatches = _session is null || columns == _ptyColumns && rows == _ptyRows;
        if (screenMatches && conptyMatches) return;
        try
        {
            if (!conptyMatches) _session!.Resize(columns, rows);
            if (_session is not null) { _ptyColumns = columns; _ptyRows = rows; }
            if (!screenMatches)
            {
                _view.ClearSelection();
                _screen.Resize(columns, rows);
            }
            _view.MarkDirty();
            LogState("resize-applied");
        }
        catch (ExternalException ex) { Status($"Resize failed: {ex.Message}"); }
    }

    private void Status(string text)
    {
        if (_smoke) System.Diagnostics.Trace.TraceError(text);
        else MessageBox.Show(this, text, "SWT", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private string[]? ControlSequences(ReadOnlySpan<byte> bytes)
    {
        if (_diagnostics is null) return null;
        // Only numeric VT controls are retained; printable shell output is never logged.
        return Regex.Matches(Encoding.ASCII.GetString(bytes), @"\x1b\[[?]?[0-9;]*[@-~]|\x1b[78DE]")
            .Select(match => match.Value.Replace("\x1b", "ESC", StringComparison.Ordinal)).Take(128).ToArray();
    }

    private void LogState(string reason, int? bytes = null, string[]? controls = null)
    {
        if (_diagnostics is null) return;
        try
        {
            _diagnostics.Write(new
            {
                timestamp = DateTimeOffset.UtcNow, reason, pid = Environment.ProcessId,
                window = new { width = ActualWidth, height = ActualHeight },
                view = new { width = _view.ActualWidth, height = _view.ActualHeight },
                cell = new { width = _view.CellWidth, height = _view.CellHeight },
                screen = new { columns = _screen.Columns, rows = _screen.Rows },
                conpty = new { columns = _ptyColumns, rows = _ptyRows },
                cursor = new { x = _screen.CursorX, y = _screen.CursorY },
                scrollOffset = _view.ScrollOffset, scrollback = _screen.ScrollbackCount,
                bytes, controls
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _diagnostics = null;
            Status($"Diagnostics disabled: {ex.Message}");
        }
    }

    private void ScheduleReload() => Dispatcher.BeginInvoke(() =>
    {
        if (_closing) return;
        _reloadTimer.Stop();
        _reloadTimer.Start();
    });

    private void Reload()
    {
        try
        {
            var next = Configuration.Parse(File.ReadAllText(_configPath));
            var appearance = CheckFont(next.Appearance);
            _view.Apply(appearance);
            _windowFrame.BorderBrush = WindowBackgroundBrush(appearance);
            _windowFrame.CornerRadius = new CornerRadius(appearance.CornerRadius);
            _windowChrome.CornerRadius = new CornerRadius(appearance.CornerRadius);
            _windowBorder.CornerRadius = new CornerRadius(appearance.CornerRadius);
            ClipWindowContent(appearance.CornerRadius);
            ClipFrameContent(Math.Max(0, appearance.CornerRadius - ResizeBorderSize));
            _screen.ScrollbackLimit = next.Terminal.ScrollbackLines;
            _view.ScrollBy(0);
            _title.Visibility = appearance.Decorations ? Visibility.Visible : Visibility.Collapsed;
            _configuration = next;
            ResizeTerminal();
            if (_fontWarning is not null) Status(_fontWarning);
        }
        catch (Exception ex) when (ex is ConfigurationException or IOException or UnauthorizedAccessException)
        { Status($"Config unchanged: {_configPath}: {ex.Message}"); }
    }

    private void ClipWindowContent(double radius)
    {
        _windowContent.Clip = new RectangleGeometry(
            new Rect(new Point(), _windowContent.RenderSize), radius, radius);
    }

    private void ClipFrameContent(double radius)
    {
        _frameContent.Clip = new RectangleGeometry(
            new Rect(new Point(), _frameContent.RenderSize), radius, radius);
    }

    private static Brush WindowBackgroundBrush(Appearance appearance)
    {
        var color = (Color)ColorConverter.ConvertFromString(appearance.Background);
        return new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(appearance.BackgroundOpacity * 255), color.R, color.G, color.B));
    }

    private async Task Send(string text, bool followOutput = true)
    {
        if (_session is null || _exited || _closing || text.Length == 0) return;
        ResizeTerminal();
        if (followOutput) _view.ScrollToBottom();
        LogState("input-send", text.Length);
        try { await _session.WriteAsync(text); }
        catch (Exception ex) when (ex is IOException or System.Threading.Channels.ChannelClosedException or ObjectDisposedException)
        { Status($"Input failed: {ex.Message}"); }
    }

    private async Task SendFocusEvent(bool focused)
    {
        var sequence = _screen.EncodeFocusEvent(focused);
        if (sequence is not null) await Send(sequence, followOutput: false);
    }

    private async void MouseDownTerminal(object sender, MouseButtonEventArgs e)
    {
        var mode = _screen.MouseMode;
        if (e.ChangedButton == MouseButton.Right)
        {
            if (_view.SelectedText is { } selectedText)
            {
                _rightClickHandledLocally = true;
                e.Handled = true;
                try
                {
                    Clipboard.SetText(selectedText);
                    _view.ClearSelection();
                }
                catch (ExternalException ex) { Status($"Copy failed: {ex.Message}"); }
                return;
            }
            if (mode == TerminalMouseMode.None || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                _rightClickHandledLocally = true;
                e.Handled = true;
                _view.Focus();
                await PasteClipboard();
                return;
            }
        }
        if (mode == TerminalMouseMode.None) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        _view.Focus();
        if (TryMouseButton(e.ChangedButton, out var button))
        {
            var (column, row) = _view.MouseCellAt(e.GetPosition(_view));
            await SendMouse(button, column, row, release: false, motion: false, Keyboard.Modifiers);
        }
        e.Handled = true;
    }

    private async void MouseUpTerminal(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right && _rightClickHandledLocally)
        {
            _rightClickHandledLocally = false;
            e.Handled = true;
            return;
        }
        if (_view.IsSelecting) return;
        var mode = _screen.MouseMode;
        if (mode == TerminalMouseMode.None) return;
        if (mode != TerminalMouseMode.X10 && TryMouseButton(e.ChangedButton, out var button))
        {
            var (column, row) = _view.MouseCellAt(e.GetPosition(_view));
            await SendMouse(button, column, row, release: true, motion: false, Keyboard.Modifiers);
        }
        e.Handled = true;
    }

    private async void MouseMoveTerminal(object sender, MouseEventArgs e)
    {
        if (_rightClickHandledLocally || _view.IsSelecting || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        var mode = _screen.MouseMode;
        var button = PressedMouseButton();
        if (mode == TerminalMouseMode.ButtonEvent && button < 0 ||
            mode is not (TerminalMouseMode.ButtonEvent or TerminalMouseMode.AnyEvent))
            return;
        if (button < 0) button = 3;
        var (column, row) = _view.MouseCellAt(e.GetPosition(_view));
        await SendMouse(button, column, row, release: false, motion: true, Keyboard.Modifiers);
        e.Handled = true;
    }

    private async void MouseWheelTerminal(object sender, MouseWheelEventArgs e)
    {
        if (_screen.MouseMode == TerminalMouseMode.None) return;
        var (column, row) = _view.MouseCellAt(e.GetPosition(_view));
        await SendMouse(e.Delta > 0 ? 64 : 65, column, row, release: false, motion: false, Keyboard.Modifiers);
        e.Handled = true;
    }

    private async Task SendMouse(int button, int column, int row, bool release, bool motion, ModifierKeys modifiers)
    {
        var flags = TerminalMouseModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Shift)) flags |= TerminalMouseModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Alt)) flags |= TerminalMouseModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Control)) flags |= TerminalMouseModifiers.Control;
        var sequence = TerminalMouseInput.Encode(button, column + 1, row + 1, release, motion, flags, _screen.SgrMouse);
        if (sequence is not null) await Send(sequence, followOutput: false);
    }

    private int PressedMouseButton()
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed) return 0;
        if (Mouse.MiddleButton == MouseButtonState.Pressed) return 1;
        if (Mouse.RightButton == MouseButtonState.Pressed) return 2;
        return -1;
    }

    private static bool TryMouseButton(MouseButton button, out int encoded)
    {
        encoded = button switch
        {
            MouseButton.Left => 0,
            MouseButton.Middle => 1,
            MouseButton.Right => 2,
            _ => -1
        };
        return encoded >= 0;
    }

    private async void KeyDownTerminal(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.F)
        {
            e.Handled = true;
            _searchBar.Visibility = Visibility.Visible;
            _searchBox.Focus();
            _searchBox.SelectAll();
            return;
        }
        if (_searchBar.IsKeyboardFocusWithin)
        {
            if (key == Key.Escape)
            {
                e.Handled = true;
                CloseSearch();
            }
            else if (key == Key.Enter)
            {
                e.Handled = true;
                FindText(modifiers.HasFlag(ModifierKeys.Shift));
            }
            return;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (key)
            {
                case Key.R: e.Handled = true; Reload(); return;
                case Key.D: e.Handled = true; _title.Visibility = _title.IsVisible ? Visibility.Collapsed : Visibility.Visible; return;
                case Key.C:
                    e.Handled = true;
                    try { Clipboard.SetText(_view.CopyText); }
                    catch (ExternalException ex) { Status($"Copy failed: {ex.Message}"); }
                    return;
                case Key.V:
                    e.Handled = true;
                    await PasteClipboard();
                    return;
            }
        }

        if (modifiers == ModifierKeys.Shift && key is Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            e.Handled = true;
            switch (key)
            {
                case Key.PageUp: _view.ScrollBy(_view.PageLines); break;
                case Key.PageDown: _view.ScrollBy(-_view.PageLines); break;
                case Key.Home: _view.ScrollToTop(); break;
                default: _view.ScrollToBottom(); break;
            }
            return;
        }
        if (modifiers == ModifierKeys.Control && key is >= Key.A and <= Key.Z)
        {
            e.Handled = true;
            await Send(((char)(key - Key.A + 1)).ToString());
            return;
        }
        var keypadSequence = _screen.ApplicationKeypad ? key switch
        {
            Key.NumPad0 => "\x1bOp", Key.NumPad1 => "\x1bOq", Key.NumPad2 => "\x1bOr",
            Key.NumPad3 => "\x1bOs", Key.NumPad4 => "\x1bOt", Key.NumPad5 => "\x1bOu",
            Key.NumPad6 => "\x1bOv", Key.NumPad7 => "\x1bOw", Key.NumPad8 => "\x1bOx",
            Key.NumPad9 => "\x1bOy", Key.Decimal => "\x1bOn", Key.Separator => "\x1bOl",
            Key.Add => "\x1bOk", Key.Subtract => "\x1bOm", Key.Multiply => "\x1bOj",
            Key.Divide => "\x1bOo", _ => null
        } : null;
        var terminalKey = key switch
        {
            Key.Up => TerminalKey.Up,
            Key.Down => TerminalKey.Down,
            Key.Right => TerminalKey.Right,
            Key.Left => TerminalKey.Left,
            Key.Home => TerminalKey.Home,
            Key.End => TerminalKey.End,
            Key.Insert => TerminalKey.Insert,
            Key.Delete => TerminalKey.Delete,
            Key.PageUp => TerminalKey.PageUp,
            Key.PageDown => TerminalKey.PageDown,
            Key.F1 => TerminalKey.F1,
            Key.F2 => TerminalKey.F2,
            Key.F3 => TerminalKey.F3,
            Key.F4 => TerminalKey.F4,
            Key.F5 => TerminalKey.F5,
            Key.F6 => TerminalKey.F6,
            Key.F7 => TerminalKey.F7,
            Key.F8 => TerminalKey.F8,
            Key.F9 => TerminalKey.F9,
            Key.F10 => TerminalKey.F10,
            Key.F11 => TerminalKey.F11,
            Key.F12 => TerminalKey.F12,
            _ => (TerminalKey?)null
        };
        var keyModifiers = TerminalKeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Shift)) keyModifiers |= TerminalKeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Alt)) keyModifiers |= TerminalKeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Control)) keyModifiers |= TerminalKeyModifiers.Control;
        var sequence = keypadSequence ?? (terminalKey is { } encodedKey
            ? TerminalKeyInput.Encode(encodedKey, keyModifiers, _screen.ApplicationCursorKeys)
            : key switch
            {
                Key.Enter => "\r",
                Key.Back => "\x7f",
                Key.Tab => modifiers.HasFlag(ModifierKeys.Shift) ? "\x1b[Z" : "\t",
                Key.Escape => "\x1b",
                _ => null
            });
        if (sequence is not null) { e.Handled = true; await Send(sequence); }
    }

    private async Task PasteClipboard()
    {
        try
        {
            var text = Clipboard.GetText();
            if (text.Length > 65536) { Status("Paste rejected: prototype limit is 65,536 characters."); return; }
            if ((text.Contains('\n') || text.Contains('\r') || text.Any(c => char.IsControl(c) && c != '\t')) &&
                MessageBox.Show("Paste includes newlines or control characters and may execute commands. Continue?",
                    "Confirm paste", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await Send(_screen.EncodePaste(text));
        }
        catch (ExternalException ex) { Status($"Paste failed: {ex.Message}"); }
    }

    private void FindText(bool previous)
    {
        _searchStatus.Text = _view.FindText(_searchBox.Text, previous, _regexSearch.IsChecked == true)
            ? "" : _view.SearchError ?? (_searchBox.Text.Length == 0 ? "" : "No matches");
        _searchStatus.Visibility = _searchStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ResetSearch()
    {
        _view.ClearSearch();
        _searchStatus.Text = "";
        _searchStatus.Visibility = Visibility.Collapsed;
    }

    private void CloseSearch()
    {
        _searchBar.Visibility = Visibility.Collapsed;
        _view.ClearSearch();
        _view.Focus();
    }

    private async void CloseWindow(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _reloadTimer.Stop();
        _smokeTimeout.Stop();
        _watcher?.Dispose();
        try { if (_session is not null) await _session.DisposeAsync(); }
        catch (Exception ex) when (ex is IOException or Win32Exception or ExternalException)
        {
            MessageBox.Show(ex.Message, "SWT shutdown failed", MessageBoxButton.OK, MessageBoxImage.Error);
            Tag = 1;
        }
        _closed = true;
        Close();
        if (_smoke) Application.Current.Shutdown(Tag is int exitCode ? exitCode : 1);
    }
}
