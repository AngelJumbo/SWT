using System.ComponentModel;
using System.Globalization;
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
    private TerminalScreen _screen;
    private TerminalView _view;
    private readonly Border _title;
    private readonly StackPanel _tabButtons;
    private readonly ScrollViewer _tabScroller;
    private readonly DockPanel _tabsArea;
    private readonly Button _newTabButton;
    private readonly StackPanel _captionButtons;
    private readonly List<TerminalTab> _tabs = [];
    private readonly List<Task> _tabClosures = [];
    private TerminalTab _activeTab;
    private bool _decorationsVisible;
    private readonly Border _windowFrame;
    private readonly Border _windowBorder;
    private readonly Grid _windowContent;
    private readonly Grid _frameContent;
    private readonly WindowChrome _windowChrome;
    private readonly Button _maximizeButton;
    private Border? _searchBar;
    private TextBox _searchBox = null!;
    private TextBlock _searchStatus = null!;
    private CheckBox _regexSearch = null!;
    private readonly DispatcherTimer _reloadTimer;
    private readonly FileSystemWatcher? _watcher;
    private ConPtySession? _session => _activeTab.Session;
    private bool _exited => _activeTab.Exited;
    private int? _ptyColumns => _activeTab.PtyColumns;
    private int? _ptyRows => _activeTab.PtyRows;
    private bool _closing, _closed;
    private bool _rightClickHandledLocally;
    private string? _fontWarning;
    private readonly DispatcherTimer _smokeTimeout;
    private const int MaxPendingOutput = 1 << 20;
    private DiagnosticLog? _diagnostics;
    private (double Width, double Height, double ViewWidth, double ViewHeight)? _lastLayout;

    private sealed class TerminalTab(TerminalScreen screen, TerminalView view)
    {
        public TerminalScreen Screen { get; } = screen;
        public TerminalView View { get; } = view;
        public ConPtySession? Session { get; set; }
        public int? PtyColumns { get; set; }
        public int? PtyRows { get; set; }
        public bool Exited { get; set; }
        public bool Closing { get; set; }
        public TaskCompletionSource? StartPending { get; set; }
        public string Title { get; set; } = "SWT";
        public Border Header { get; set; } = null!;
        public TextBlock Label { get; set; } = null!;
        public TextBlock Icon { get; set; } = null!;
        public Button CloseButton { get; set; } = null!;
        public List<string> Replies { get; } = [];
        public Lock OutputLock { get; } = new();
        public byte[] Incoming = new byte[65536], Spare = new byte[65536];
        public int IncomingLength;
        public bool DrainQueued;
        public TaskCompletionSource? DrainWaiter;
    }

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
        diagnostics?.WriteStartup("font-check-starting");
        var appearance = CheckFont(configuration.Appearance);
        diagnostics?.WriteStartup("font-checked");
        _view = new TerminalView(_screen, appearance);
        _activeTab = new TerminalTab(_screen, _view);
        _tabs.Add(_activeTab);
        diagnostics?.WriteStartup("view-created");
        Width = configuration.Appearance.Columns * _view.CellWidth + 28;
        Height = configuration.Appearance.Rows * _view.CellHeight + 52 + ResizeBorderSize * 2;
        _windowContent = new Grid();
        _frameContent = new Grid();
        _frameContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _frameContent.RowDefinitions.Add(new RowDefinition());
        var chrome = new Grid();
        chrome.ColumnDefinitions.Add(new ColumnDefinition());
        chrome.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _tabsArea = new DockPanel { LastChildFill = true, Background = Brushes.Transparent };
        _newTabButton = CaptionButton("M 1,5 H 9 M 5,1 V 9", () => AddTab());
        _newTabButton.ToolTip = "New tab (Ctrl+Shift+T)";
        System.Windows.Automation.AutomationProperties.SetName(_newTabButton, "New tab");
        DockPanel.SetDock(_newTabButton, Dock.Right);
        _tabsArea.Children.Add(_newTabButton);
        _tabButtons = new StackPanel { Orientation = Orientation.Horizontal };
        _tabScroller = new ScrollViewer
        {
            Content = _tabButtons, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _tabScroller.PreviewMouseWheel += (_, e) =>
        {
            _tabScroller.ScrollToHorizontalOffset(_tabScroller.HorizontalOffset - e.Delta);
            e.Handled = true;
        };
        _tabsArea.Children.Add(_tabScroller);
        _tabsArea.SizeChanged += (_, _) => UpdateTabLayout();
        chrome.Children.Add(_tabsArea);
        _captionButtons = new StackPanel { Orientation = Orientation.Horizontal };
        _captionButtons.Children.Add(CaptionButton("M 1,9 H 9", () => WindowState = WindowState.Minimized));
        _maximizeButton = CaptionButton("M 1,1 H 9 V 9 H 1 Z", ToggleMaximize);
        _captionButtons.Children.Add(_maximizeButton);
        _captionButtons.Children.Add(CaptionButton("M 2,2 L 8,8 M 8,2 L 2,8", Close, isClose: true));
        Grid.SetColumn(_captionButtons, 1);
        chrome.Children.Add(_captionButtons);
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
        _windowContent.SizeChanged += (_, _) => ClipWindowContent(_configuration.Appearance.CornerRadius);
        _frameContent.SizeChanged += (_, _) =>
            ClipFrameContent(Math.Max(0, _configuration.Appearance.CornerRadius - ResizeBorderSize));
        Content = _windowContent;
        _decorationsVisible = appearance.Decorations;
        InitializeTab(_activeTab);
        UpdateTabBar();
        if (diagnostics is not null)
        {
            SourceInitialized += (_, _) => LogState("source-initialized");
            ContentRendered += (_, _) => LogState("content-rendered");
            LayoutUpdated += (_, _) =>
            {
                var layout = (ActualWidth, ActualHeight, _view.ActualWidth, _view.ActualHeight);
                if (_lastLayout == layout) return;
                _lastLayout = layout;
                LogState("layout-updated");
            };
        }
        Activated += async (_, _) => await SendFocusEvent(true);
        Deactivated += async (_, _) => await SendFocusEvent(false);
        PreviewKeyDown += KeyDownTerminal;
        Loaded += async (_, _) => await StartSession(_activeTab);
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

    private void InitializeTab(TerminalTab tab)
    {
        tab.Screen.Reply += tab.Replies.Add;
        tab.Screen.TitleChanged += title =>
        {
            if (_closing || tab.Closing || tab.Exited) return;
            tab.Title = string.IsNullOrEmpty(title) ? "SWT" : title;
            tab.Label.Text = tab.Title;
            tab.Header.ToolTip = tab.Title;
            if (tab == _activeTab) Title = tab.Title;
        };
        var view = tab.View;
        view.SizeChanged += (_, _) =>
        {
            if (tab != _activeTab) return;
            LogState("view-size-changed");
            ResizeTerminal();
        };
        view.GotKeyboardFocus += (_, _) => view.MarkDirty();
        view.LostKeyboardFocus += (_, _) => view.MarkDirty();
        view.MouseLeftButtonDown += (_, e) =>
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) DragMove();
            else
            {
                view.Focus();
                view.BeginSelection(e.GetPosition(view));
            }
            e.Handled = true;
        };
        view.MouseMove += (_, e) =>
        {
            if (view.IsSelecting) view.UpdateSelection(e.GetPosition(view));
        };
        view.MouseLeftButtonUp += (_, e) =>
        {
            view.EndSelection(e.GetPosition(view));
            e.Handled = true;
        };
        view.PreviewMouseDown += MouseDownTerminal;
        view.PreviewMouseUp += MouseUpTerminal;
        view.PreviewMouseMove += MouseMoveTerminal;
        view.PreviewMouseWheel += MouseWheelTerminal;
        view.TextInput += async (_, e) =>
        {
            e.Handled = true;
            await Send(e.Text);
        };

        tab.Label = new TextBlock
        {
            Text = tab.Title, Foreground = new SolidColorBrush(Color.FromRgb(216, 222, 233)),
            FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 0, 0, 0)
        };
        var headerContent = new Grid();
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerContent.ColumnDefinitions.Add(new ColumnDefinition());
        headerContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tab.Icon = new TextBlock
        {
            Text = ">_", Foreground = new SolidColorBrush(Color.FromRgb(129, 161, 193)),
            FontFamily = new FontFamily("Cascadia Mono"), FontSize = 12, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0)
        };
        headerContent.Children.Add(tab.Icon);
        Grid.SetColumn(tab.Label, 1);
        headerContent.Children.Add(tab.Label);
        var close = CaptionButton("M 2,2 L 8,8 M 8,2 L 2,8", () => _ = CloseTabAsync(tab));
        close.Width = 28;
        close.Height = 30;
        tab.CloseButton = close;
        close.ToolTip = "Close tab (Ctrl+Shift+W)";
        System.Windows.Automation.AutomationProperties.SetName(close, "Close tab");
        Grid.SetColumn(close, 2);
        headerContent.Children.Add(close);
        tab.Header = new Border
        {
            Child = headerContent, Width = 160, Height = 32, Margin = new Thickness(3, 3, 0, 0),
            CornerRadius = new CornerRadius(5, 5, 0, 0), ToolTip = tab.Title
        };
        var headerStyle = new Style(typeof(Border));
        headerStyle.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent));
        var inactiveHover = new Trigger { Property = IsMouseOverProperty, Value = true };
        inactiveHover.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(45, 51, 61))));
        headerStyle.Triggers.Add(inactiveHover);
        var selected = new Trigger { Property = TagProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(54, 61, 73))));
        headerStyle.Triggers.Add(selected);
        tab.Header.Style = headerStyle;
        var closeStyle = new Style(typeof(Button));
        closeStyle.Setters.Add(new Setter(VisibilityProperty, Visibility.Hidden));
        var active = new DataTrigger
        {
            Binding = new System.Windows.Data.Binding(nameof(Tag)) { Source = tab.Header },
            Value = true
        };
        active.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible));
        closeStyle.Triggers.Add(active);
        var headerHover = new DataTrigger
        {
            Binding = new System.Windows.Data.Binding(nameof(IsMouseOver)) { Source = tab.Header },
            Value = true
        };
        headerHover.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible));
        closeStyle.Triggers.Add(headerHover);
        close.Style = closeStyle;
        tab.Header.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            SelectTab(tab);
        };
        _tabButtons.Children.Add(tab.Header);
    }

    internal void AddTab()
    {
        if (_closing) return;
        var screen = new TerminalScreen(_screen.Columns, _screen.Rows, _configuration.Terminal.ScrollbackLines);
        var view = new TerminalView(screen, CheckFont(_configuration.Appearance));
        var tab = new TerminalTab(screen, view);
        _tabs.Add(tab);
        InitializeTab(tab);
        SelectTab(tab);
        UpdateTabBar();
        if (IsLoaded)
            Dispatcher.BeginInvoke(() =>
            {
                if (!_closing && !tab.Closing) _ = StartSession(tab);
            }, DispatcherPriority.Loaded);
    }

    private void SelectTab(TerminalTab tab)
    {
        if (_closing || tab.Closing || tab == _activeTab) return;
        var previous = _activeTab;
        if (IsActive) _ = SendFocusEvent(previous, false);
        if (_searchBar?.Visibility == Visibility.Visible)
        {
            previous.View.ClearSearch();
            _searchBar.Visibility = Visibility.Collapsed;
            _searchStatus.Text = "";
            _searchStatus.Visibility = Visibility.Collapsed;
        }
        _frameContent.Children.Remove(previous.View);
        _activeTab = tab;
        _screen = tab.Screen;
        _view = tab.View;
        Grid.SetRow(_view, 1);
        _frameContent.Children.Add(_view);
        _rightClickHandledLocally = false;
        Title = tab.Title;
        UpdateTabBar();
        _view.Focus();
        if (IsActive) _ = SendFocusEvent(tab, true);
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closing && tab == _activeTab)
            {
                ResizeTerminal();
                tab.Header.BringIntoView();
            }
        }, DispatcherPriority.Loaded);
    }

    internal void SelectRelativeTab(int direction)
    {
        if (_tabs.Count < 2) return;
        var index = (_tabs.IndexOf(_activeTab) + direction + _tabs.Count) % _tabs.Count;
        SelectTab(_tabs[index]);
    }

    internal void ToggleDecorations()
    {
        _decorationsVisible = !_decorationsVisible;
        UpdateTabBar();
    }

    private async Task CloseTabAsync(TerminalTab tab)
    {
        if (_closing || tab.Closing) return;
        if (_tabs.Count == 1) { Close(); return; }
        tab.Closing = true;
        var index = _tabs.IndexOf(tab);
        if (tab == _activeTab)
            SelectTab(_tabs[index == _tabs.Count - 1 ? index - 1 : index + 1]);
        _tabs.Remove(tab);
        _tabButtons.Children.Remove(tab.Header);
        UpdateTabBar();
        var disposal = DisposeTabAsync(tab);
        _tabClosures.Add(disposal);
        try { await disposal; }
        finally { _tabClosures.Remove(disposal); }
    }

    private async Task DisposeTabAsync(TerminalTab tab)
    {
        try
        {
            if (tab.StartPending is not null) await tab.StartPending.Task;
            if (tab.Session is not null) await tab.Session.DisposeAsync();
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or ExternalException)
        {
            Status($"Tab shutdown failed: {ex.Message}");
            Tag = 1;
        }
    }

    private void UpdateTabBar()
    {
        _title.Visibility = _decorationsVisible || _tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _captionButtons.Visibility = _decorationsVisible ? Visibility.Visible : Visibility.Collapsed;
        _title.Height = _decorationsVisible ? 36 : 26;
        _newTabButton.Height = _decorationsVisible ? 35 : 25;
        foreach (Button button in _captionButtons.Children)
            button.Height = 35;
        foreach (var tab in _tabs)
        {
            tab.Header.Tag = tab == _activeTab;
            tab.Header.Height = _decorationsVisible ? 32 : 22;
            tab.CloseButton.Height = _decorationsVisible ? 30 : 20;
            tab.Icon.Visibility = _decorationsVisible ? Visibility.Visible : Visibility.Collapsed;
            tab.Label.Margin = new Thickness(_decorationsVisible ? 6 : 10, 0, 0, 0);
        }
        UpdateTabLayout();
    }

    private void UpdateTabLayout()
    {
        if (_tabsArea.ActualWidth <= 0 || _tabs.Count == 0) return;
        const double tabMargin = 3;
        const double dragSpace = 48;
        var available = Math.Max(0, _tabsArea.ActualWidth - _newTabButton.Width
            - (_decorationsVisible ? dragSpace : 0));
        var tabWidth = Math.Clamp(available / _tabs.Count - tabMargin, 72, 160);
        var scrollerWidth = Math.Min(available, _tabs.Count * (tabWidth + tabMargin));
        var changed = _tabScroller.Width != scrollerWidth ||
            _tabs.Any(tab => tab.Header.Width != tabWidth);
        if (!changed) return;
        foreach (var tab in _tabs) tab.Header.Width = tabWidth;
        _tabScroller.Width = scrollerWidth;
        if (IsLoaded)
            Dispatcher.BeginInvoke(() =>
            {
                if (!_closing && _title.Visibility == Visibility.Visible) _activeTab.Header.BringIntoView();
            }, DispatcherPriority.Loaded);
    }

    private static Button CaptionButton(string geometry, Action action, bool isClose = false)
    {
        var hitArea = new FrameworkElementFactory(typeof(Border));
        hitArea.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var highlight = new FrameworkElementFactory(typeof(Border), "Highlight");
        highlight.SetValue(MarginProperty, new Thickness(4, 3, 4, 3));
        highlight.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        highlight.AppendChild(content);
        hitArea.AppendChild(highlight);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = hitArea };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(isClose ? Color.FromRgb(175, 43, 35) : Color.FromRgb(54, 61, 73)),
            highlight.Name));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(isClose ? Color.FromRgb(139, 34, 28) : Color.FromRgb(70, 78, 91)),
            highlight.Name));
        template.Triggers.Add(pressed);

        var button = new Button
        {
            Content = WindowIcon(geometry), Width = 46, Height = 35,
            Template = template, Focusable = false, Cursor = Cursors.Arrow
        };
        button.Click += (_, e) => { e.Handled = true; action(); };
        return button;
    }

    private static System.Windows.Shapes.Path WindowIcon(string geometry) => new()
    {
        Data = Geometry.Parse(geometry), Width = 10, Height = 10, Stretch = Stretch.Fill,
        Stroke = new SolidColorBrush(Color.FromRgb(216, 222, 233)), StrokeThickness = 1.2,
        StrokeStartLineCap = PenLineCap.Square, StrokeEndLineCap = PenLineCap.Square
    };

    private Border CreateSearchBar()
    {
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
        var searchBar = new Border
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
        titleVisible.Setters.Add(new Setter(MarginProperty,
            new System.Windows.Data.Binding(nameof(Height)) { Source = _title, Converter = new TitleSearchMarginConverter() }));
        searchBarStyle.Triggers.Add(titleVisible);
        searchBar.Style = searchBarStyle;
        _windowContent.Children.Add(searchBar);
        return searchBar;
    }

    internal void OpenSearch()
    {
        _searchBar ??= CreateSearchBar();
        _searchBar.Visibility = Visibility.Visible;
        _searchBox.Focus();
        _searchBox.SelectAll();
    }

    private sealed class TitleSearchMarginConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            new Thickness(12, (double)value + 12, 12, 12);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

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

    private async Task StartSession(TerminalTab tab)
    {
        tab.StartPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (tab == _activeTab)
            {
                LogState("loaded");
                ResizeTerminal();
            }
            var executable = ShellResolver.Resolve(_smoke ? "" : _configuration.Shell.Executable);
            IReadOnlyList<string> arguments = _smoke
                ? ["-NoLogo", "-NoProfile", "-Command", "Write-Output 'SWT_SMOKE'; exit 0"]
                : _configuration.Shell.Arguments;
            var directory = string.IsNullOrEmpty(_configuration.Shell.WorkingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : _configuration.Shell.WorkingDirectory;
            var columns = tab.Screen.Columns;
            var rows = tab.Screen.Rows;
            LogState("conpty-starting");
            var session = ConPtySession.Start(executable, arguments, directory, columns, rows,
                bytes => QueueOutput(tab, bytes));
            tab.Session = session;
            tab.StartPending.TrySetResult();
            if (_closing || tab.Closing)
                return;
            tab.PtyColumns = columns;
            tab.PtyRows = rows;
            LogState("conpty-started");
            // Native startup can pump window messages while the tiling manager changes the layout.
            if (tab == _activeTab) { ResizeTerminal(); _view.Focus(); }
            if (_fontWarning is not null) Status(_fontWarning);
            if (_smoke) _smokeTimeout.Start();
            await tab.Session.Completion;
            if (_closing || tab.Closing) return;
            DrainOutput(tab);
            tab.Exited = true;
            tab.Title = $"SWT - Session exited ({tab.Session.ExitCode})";
            tab.Label.Text = tab.Title;
            tab.Header.ToolTip = tab.Title;
            if (tab == _activeTab) Title = tab.Title;
            tab.View.ToolTip = "Session exited. Close this tab to exit.";
            if (_smoke)
            {
                _smokeTimeout.Stop();
                var found = Enumerable.Range(0, tab.Screen.ScrollbackCount + tab.Screen.Rows)
                    .Any(y => tab.Screen.GetViewportLine(y, tab.Screen.ScrollbackCount).Text(tab.Screen.Columns)
                        .Contains("SWT_SMOKE", StringComparison.Ordinal));
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Tag = found && tab.Session.ExitCode == 0 ? 0 : 1;
                Close();
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or ArgumentException or ExternalException or InvalidOperationException)
        {
            if (_closing || tab.Closing) return;
            tab.Exited = true;
            tab.Title = "SWT - Session failed";
            tab.Label.Text = tab.Title;
            tab.Header.ToolTip = tab.Title;
            if (tab == _activeTab) Title = tab.Title;
            Status($"Session failed: {ex.Message}");
            if (_smoke) { Tag = 1; Close(); }
        }
        finally
        {
            tab.StartPending.TrySetResult();
        }
    }

    private Task QueueOutput(TerminalTab tab, ReadOnlyMemory<byte> bytes)
    {
        var wait = Task.CompletedTask;
        var schedule = false;
        lock (tab.OutputLock)
        {
            if (tab.IncomingLength + bytes.Length > tab.Incoming.Length)
                Array.Resize(ref tab.Incoming, Math.Max(tab.Incoming.Length * 2, tab.IncomingLength + bytes.Length));
            bytes.Span.CopyTo(tab.Incoming.AsSpan(tab.IncomingLength));
            tab.IncomingLength += bytes.Length;
            if (!tab.DrainQueued) tab.DrainQueued = schedule = true;
            // Backpressure: the reader pauses until the UI catches up.
            if (tab.IncomingLength >= MaxPendingOutput)
                wait = (tab.DrainWaiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        if (schedule) Dispatcher.BeginInvoke(() => DrainOutput(tab), DispatcherPriority.Background);
        return wait;
    }

    private void DrainOutput(TerminalTab tab)
    {
        byte[] data;
        int length;
        TaskCompletionSource? waiter;
        lock (tab.OutputLock)
        {
            data = tab.Incoming;
            length = tab.IncomingLength;
            tab.Incoming = tab.Spare;
            tab.IncomingLength = 0;
            tab.DrainQueued = false;
            waiter = tab.DrainWaiter;
            tab.DrainWaiter = null;
        }
        if (length > 0 && !tab.Closing && !_closing)
        {
            if (tab == _activeTab) LogState("output-before", length, ControlSequences(data.AsSpan(0, length)));
            tab.Screen.Feed(data.AsSpan(0, length));
            tab.View.ScreenChanged();
            if (tab == _activeTab) LogState("output-after", length);
        }
        tab.Spare = data.Length > 4 * 65536 ? new byte[65536] : data;
        waiter?.TrySetResult();
        if (tab.Replies.Count == 0) return;
        string[] replies = [.. tab.Replies];
        tab.Replies.Clear();
        _ = SendReplies(tab, replies);
    }

    private async Task SendReplies(TerminalTab tab, string[] replies)
    {
        try
        {
            foreach (var reply in replies)
                if (tab.Session is not null && !_closing && !tab.Closing && !tab.Exited)
                    await tab.Session.WriteAsync(reply);
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
            if (_session is not null) { _activeTab.PtyColumns = columns; _activeTab.PtyRows = rows; }
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
            foreach (var tab in _tabs)
            {
                tab.View.Apply(appearance);
                tab.Screen.ScrollbackLimit = next.Terminal.ScrollbackLines;
                tab.View.ScrollBy(0);
            }
            _windowFrame.BorderBrush = WindowBackgroundBrush(appearance);
            _windowFrame.CornerRadius = new CornerRadius(appearance.CornerRadius);
            _windowChrome.CornerRadius = new CornerRadius(appearance.CornerRadius);
            _windowBorder.CornerRadius = new CornerRadius(appearance.CornerRadius);
            ClipWindowContent(appearance.CornerRadius);
            ClipFrameContent(Math.Max(0, appearance.CornerRadius - ResizeBorderSize));
            _decorationsVisible = appearance.Decorations;
            UpdateTabBar();
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

    private Task SendFocusEvent(bool focused) => SendFocusEvent(_activeTab, focused);

    private async Task SendFocusEvent(TerminalTab tab, bool focused)
    {
        var sequence = tab.Screen.EncodeFocusEvent(focused);
        if (sequence is null || tab.Session is null || tab.Exited || tab.Closing || _closing) return;
        try { await tab.Session.WriteAsync(sequence); }
        catch (Exception ex) when (ex is IOException or System.Threading.Channels.ChannelClosedException or ObjectDisposedException)
        { Status($"Input failed: {ex.Message}"); }
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
	if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.T)
        {
            e.Handled = true;
            AddTab();
            return;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.W)
        {
            e.Handled = true;
            await CloseTabAsync(_activeTab);
            return;
        }
	if (key == Key.Tab && modifiers is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            SelectRelativeTab(modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            return;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.F)
        {
            e.Handled = true;
            OpenSearch();
            return;
        }
        if (_searchBar?.IsKeyboardFocusWithin == true)
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
        if (modifiers is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift))
        {
            double? size = key switch
            {
                Key.OemPlus or Key.Add => _view.FontSize + 1,
                Key.OemMinus or Key.Subtract => _view.FontSize - 1,
                Key.D0 or Key.NumPad0 => _configuration.Appearance.FontSize,
                _ => null
            };
            if (size is not null)
            {
                e.Handled = true;
                _view.SetFontSize(size.Value);
                ResizeTerminal();
                return;
            }
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (key)
            {
                case Key.R: e.Handled = true; Reload(); return;
                case Key.D:
                    e.Handled = true;
                    ToggleDecorations();
                    return;
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
        if (_searchBar is not null) _searchBar.Visibility = Visibility.Collapsed;
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
        foreach (var tab in _tabs) tab.Closing = true;
        await Task.WhenAll(_tabs.Select(DisposeTabAsync).Concat(_tabClosures.ToArray()));
        _closed = true;
        Close();
        if (_smoke) Application.Current.Shutdown(Tag is int exitCode ? exitCode : 1);
    }
}
