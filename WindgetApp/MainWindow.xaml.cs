using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace WindgetApp;

public partial class MainWindow : Window
{
    private const int GraphSampleLimit = 64;
    private const double ResizeGripSize = 12;
    private const double AlignmentSnapDistance = 8;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const string StartupRegistryName = "Windget";
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly ObservableCollection<TaskItem> _tasks = [];
    private readonly ObservableCollection<LauncherItem> _launchers = [];
    private readonly ObservableCollection<string> _launcherCategories = [];
    private readonly Dictionary<string, List<CalendarEvent>> _events = [];
    private readonly Queue<double> _cpuSamples = [];
    private readonly Queue<double> _memorySamples = [];
    private readonly Queue<double> _gpuSamples = [];
    private readonly DispatcherTimer _mainTimer = new();
    private readonly ResourceSampler _resourceSampler = new();
    private readonly AudioMixerService _audioMixer = new();
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Windget",
        "state.json");
    private readonly string _legacyTasksPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Windget",
        "tasks.json");

    private Forms.NotifyIcon? _notifyIcon;
    private AppState _state = new();
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selectedDate = DateTime.Today;
    private FrameworkElement? _draggedWidget;
    private FrameworkElement? _resizedWidget;
    private ResizeModeEdge _resizeEdge = ResizeModeEdge.None;
    private WpfPoint _dragStartMouse;
    private WpfPoint _dragStartCanvas;
    private WpfPoint _resizeStartMouse;
    private WpfSize _resizeStartSize;
    private bool _isLoaded;
    private bool _isExitRequested;
    private bool _isApplyingSettings;
    private bool _isUpdatingFocusModeUi;
    private bool _isApplyingAudioUi;
    private bool _isAdjustingAudioVolume;
    private bool _focusRunning;
    private bool _focusAlarmEnabled = true;
    private FocusMode _focusMode = FocusMode.Timer;
    private int _focusMinutes = 25;
    private int _focusRemainingSeconds = 25 * 60;
    private int _focusElapsedSeconds;
    private int _audioRefreshTick;
    private string _newEventStartTime = string.Empty;
    private string _newEventEndTime = string.Empty;

    public MainWindow()
    {
        InitializeComponent();

        MouseMove += Surface_MouseMove;
        MouseLeftButtonDown += Surface_MouseLeftButtonDown;
        MouseLeftButtonUp += Surface_MouseLeftButtonUp;
        SizeChanged += (_, _) => ClampWidgetsToCanvas();

        _mainTimer.Interval = TimeSpan.FromSeconds(1);
        _mainTimer.Tick += (_, _) =>
        {
            RefreshResources();
            RefreshAudioMixer(false);
            CheckTaskResets();
            TickFocusTimer();
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FillDesktopCanvas();
        EnableCanvasClickThrough();
        LoadState();
        ApplyStateToUi();
        SetupNotifyIcon();
        RenderTasks();
        RenderLaunchers();
        RenderCalendar();
        RenderEvents();
        RefreshResources();
        RefreshAudioMixer(true);
        UpdateClock();
        _mainTimer.Start();
        _isLoaded = true;
    }

    private void EnableCanvasClickThrough()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        HideMainWindowFromAltTab(handle);
        HwndSource.FromHwnd(handle)?.AddHook(WindowHitTestHook);
    }

    private static void HideMainWindowFromAltTab(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int extendedStyle = GetWindowLong(handle, GwlExStyle);
        extendedStyle &= ~WsExAppWindow;
        extendedStyle |= WsExToolWindow;
        SetWindowLong(handle, GwlExStyle, extendedStyle);
    }

    private IntPtr WindowHitTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest)
        {
            return IntPtr.Zero;
        }

        WpfPoint screenPoint = new(GetSignedLowWord(lParam), GetSignedHighWord(lParam));
        WpfPoint windowPoint = PointFromScreen(screenPoint);
        if (IsPointOverVisibleWidget(windowPoint))
        {
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(HtTransparent);
    }

    private bool IsPointOverVisibleWidget(WpfPoint point)
    {
        foreach (FrameworkElement widget in GetMovableWidgets())
        {
            if (widget.Visibility != Visibility.Visible)
            {
                continue;
            }

            double left = Canvas.GetLeft(widget);
            double top = Canvas.GetTop(widget);
            double width = widget.ActualWidth > 0 ? widget.ActualWidth : widget.Width;
            double height = widget.ActualHeight > 0 ? widget.ActualHeight : widget.Height;
            Rect bounds = new(
                double.IsNaN(left) ? 0 : left,
                double.IsNaN(top) ? 0 : top,
                Math.Max(0, width),
                Math.Max(0, height));

            if (bounds.Contains(point))
            {
                return true;
            }
        }

        return false;
    }

    private static int GetSignedLowWord(IntPtr value)
    {
        return (short)((long)value & 0xffff);
    }

    private static int GetSignedHighWord(IntPtr value)
    {
        return (short)(((long)value >> 16) & 0xffff);
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveStateFromUi();

        if (!_isExitRequested)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _notifyIcon?.Dispose();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
    }

    private void FillDesktopCanvas()
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void SetupNotifyIcon()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Windget",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };

        _notifyIcon.ContextMenuStrip.Items.Add("Control Center", null, (_, _) => ToggleControlCenterFromTray());
        _notifyIcon.ContextMenuStrip.Items.Add("Open Widgets", null, (_, _) => ShowFromTray());
        _notifyIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) =>
        {
            _isExitRequested = true;
            Close();
        });
        _notifyIcon.MouseUp += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                ToggleControlCenterFromTray();
            }
        };
        _notifyIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return System.Drawing.Icon.ExtractAssociatedIcon(path) ?? System.Drawing.SystemIcons.Application;
            }
        }
        catch
        {
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void HideToTray()
    {
        SaveStateFromUi();
        Hide();
        WindowState = WindowState.Normal;
    }

    private void ShowFromTray()
    {
        FillDesktopCanvas();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ToggleControlCenterFromTray()
    {
        if (!IsVisible)
        {
            ShowFromTray();
        }

        ControlWidget.Visibility = ControlWidget.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (ControlWidget.Visibility == Visibility.Visible)
        {
            System.Windows.Controls.Panel.SetZIndex(ControlWidget, 500);
        }

        _state.Window.ControlCenterVisible = ControlWidget.Visibility == Visibility.Visible;
        SaveStateFromUi();
    }

    private void WidgetHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DependencyObject source)
        {
            return;
        }

        _draggedWidget = FindParentWidget(source);
        if (_draggedWidget is null)
        {
            return;
        }

        _dragStartMouse = e.GetPosition(WidgetCanvas);
        _dragStartCanvas = new WpfPoint(Canvas.GetLeft(_draggedWidget), Canvas.GetTop(_draggedWidget));
        _draggedWidget.CaptureMouse();
        e.Handled = true;
    }

    private void Surface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        FrameworkElement? widget = FindParentWidget(source);
        if (widget is null)
        {
            return;
        }

        ResizeModeEdge edge = GetResizeEdge(widget, e.GetPosition(widget));
        if (edge == ResizeModeEdge.None)
        {
            return;
        }

        _resizedWidget = widget;
        _resizeEdge = edge;
        _resizeStartMouse = e.GetPosition(WidgetCanvas);
        _resizeStartSize = new WpfSize(_resizedWidget.ActualWidth, _resizedWidget.ActualHeight);
        _resizedWidget.CaptureMouse();
        e.Handled = true;
    }

    private void Surface_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_draggedWidget is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            WpfPoint current = e.GetPosition(WidgetCanvas);
            WpfPoint snapped = SnapWidgetPosition(
                _draggedWidget,
                _dragStartCanvas.X + current.X - _dragStartMouse.X,
                _dragStartCanvas.Y + current.Y - _dragStartMouse.Y);
            SetWidgetPosition(
                _draggedWidget,
                snapped.X,
                snapped.Y);
            return;
        }

        if (_resizedWidget is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            WpfPoint current = e.GetPosition(WidgetCanvas);
            double width = _resizeStartSize.Width;
            double height = _resizeStartSize.Height;

            if (_resizeEdge is ResizeModeEdge.Right or ResizeModeEdge.Corner)
            {
                width += current.X - _resizeStartMouse.X;
            }

            if (_resizeEdge is ResizeModeEdge.Bottom or ResizeModeEdge.Corner)
            {
                height += current.Y - _resizeStartMouse.Y;
            }

            WpfSize snapped = SnapWidgetSize(_resizedWidget, width, height, _resizeEdge);
            SetWidgetSize(_resizedWidget, snapped.Width, snapped.Height);
            return;
        }

        UpdateResizeCursor(e);
    }

    private void Surface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedWidget is not null)
        {
            _draggedWidget.ReleaseMouseCapture();
            _draggedWidget = null;
        }

        if (_resizedWidget is not null)
        {
            _resizedWidget.ReleaseMouseCapture();
            _resizedWidget = null;
            _resizeEdge = ResizeModeEdge.None;
        }

        HideAlignmentGuides();
        Cursor = null;
        SaveStateFromUi();
    }

    private void UpdateResizeCursor(WpfMouseEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            Cursor = null;
            return;
        }

        FrameworkElement? widget = FindParentWidget(source);
        if (widget is null)
        {
            Cursor = null;
            return;
        }

        ResizeModeEdge edge = GetResizeEdge(widget, e.GetPosition(widget));
        Cursor = edge switch
        {
            ResizeModeEdge.Corner => WpfCursors.SizeNWSE,
            ResizeModeEdge.Right => WpfCursors.SizeWE,
            ResizeModeEdge.Bottom => WpfCursors.SizeNS,
            _ => null
        };
    }

    private static ResizeModeEdge GetResizeEdge(FrameworkElement widget, WpfPoint point)
    {
        bool nearRight = point.X >= widget.ActualWidth - ResizeGripSize;
        bool nearBottom = point.Y >= widget.ActualHeight - ResizeGripSize;

        return (nearRight, nearBottom) switch
        {
            (true, true) => ResizeModeEdge.Corner,
            (true, false) => ResizeModeEdge.Right,
            (false, true) => ResizeModeEdge.Bottom,
            _ => ResizeModeEdge.None
        };
    }

    private void SetWidgetPosition(FrameworkElement widget, double left, double top)
    {
        left = Math.Clamp(left, 0, Math.Max(0, ActualWidth - widget.ActualWidth));
        top = Math.Clamp(top, 0, Math.Max(0, ActualHeight - widget.ActualHeight));
        Canvas.SetLeft(widget, left);
        Canvas.SetTop(widget, top);
    }

    private void SetWidgetSize(FrameworkElement widget, double width, double height)
    {
        widget.Width = Math.Clamp(width, widget.MinWidth, Math.Max(widget.MinWidth, ActualWidth - Canvas.GetLeft(widget)));
        widget.Height = Math.Clamp(height, widget.MinHeight, Math.Max(widget.MinHeight, ActualHeight - Canvas.GetTop(widget)));
    }

    private WpfPoint SnapWidgetPosition(FrameworkElement widget, double left, double top)
    {
        double width = widget.ActualWidth > 0 ? widget.ActualWidth : widget.Width;
        double height = widget.ActualHeight > 0 ? widget.ActualHeight : widget.Height;
        double snappedLeft = left;
        double snappedTop = top;

        if (TryFindClosestGuide(widget, left, width, true, out double verticalGuide, out double verticalDelta))
        {
            snappedLeft += verticalDelta;
            ShowVerticalGuide(verticalGuide);
        }
        else
        {
            VerticalAlignmentGuide.Visibility = Visibility.Collapsed;
        }

        if (TryFindClosestGuide(widget, top, height, false, out double horizontalGuide, out double horizontalDelta))
        {
            snappedTop += horizontalDelta;
            ShowHorizontalGuide(horizontalGuide);
        }
        else
        {
            HorizontalAlignmentGuide.Visibility = Visibility.Collapsed;
        }

        return new WpfPoint(snappedLeft, snappedTop);
    }

    private WpfSize SnapWidgetSize(FrameworkElement widget, double width, double height, ResizeModeEdge edge)
    {
        double left = Canvas.GetLeft(widget);
        double top = Canvas.GetTop(widget);
        left = double.IsNaN(left) ? 0 : left;
        top = double.IsNaN(top) ? 0 : top;
        double snappedWidth = width;
        double snappedHeight = height;

        if (edge is ResizeModeEdge.Right or ResizeModeEdge.Corner
            && TryFindClosestGuide(widget, left, width, true, out double verticalGuide, out double verticalDelta))
        {
            snappedWidth = Math.Max(widget.MinWidth, width + verticalDelta);
            ShowVerticalGuide(verticalGuide);
        }
        else
        {
            VerticalAlignmentGuide.Visibility = Visibility.Collapsed;
        }

        if (edge is ResizeModeEdge.Bottom or ResizeModeEdge.Corner
            && TryFindClosestGuide(widget, top, height, false, out double horizontalGuide, out double horizontalDelta))
        {
            snappedHeight = Math.Max(widget.MinHeight, height + horizontalDelta);
            ShowHorizontalGuide(horizontalGuide);
        }
        else
        {
            HorizontalAlignmentGuide.Visibility = Visibility.Collapsed;
        }

        return new WpfSize(snappedWidth, snappedHeight);
    }

    private bool TryFindClosestGuide(
        FrameworkElement movingWidget,
        double start,
        double length,
        bool vertical,
        out double guide,
        out double delta)
    {
        guide = 0;
        delta = 0;
        double bestDistance = AlignmentSnapDistance + 1;
        double[] movingEdges = [start, start + length / 2, start + length];

        foreach (double candidate in GetAlignmentCandidates(movingWidget, vertical))
        {
            foreach (double movingEdge in movingEdges)
            {
                double distance = Math.Abs(candidate - movingEdge);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    guide = candidate;
                    delta = candidate - movingEdge;
                }
            }
        }

        return bestDistance <= AlignmentSnapDistance;
    }

    private IEnumerable<double> GetAlignmentCandidates(FrameworkElement movingWidget, bool vertical)
    {
        double canvasLength = vertical
            ? (ActualWidth > 0 ? ActualWidth : SystemParameters.WorkArea.Width)
            : (ActualHeight > 0 ? ActualHeight : SystemParameters.WorkArea.Height);

        yield return 24;
        yield return canvasLength / 2;
        yield return Math.Max(0, canvasLength - 24);

        foreach (FrameworkElement widget in GetMovableWidgets())
        {
            if (widget == movingWidget || widget.Visibility != Visibility.Visible)
            {
                continue;
            }

            double start = vertical ? Canvas.GetLeft(widget) : Canvas.GetTop(widget);
            double length = vertical
                ? (widget.ActualWidth > 0 ? widget.ActualWidth : widget.Width)
                : (widget.ActualHeight > 0 ? widget.ActualHeight : widget.Height);

            if (double.IsNaN(start) || double.IsNaN(length) || length <= 0)
            {
                continue;
            }

            yield return start;
            yield return start + length / 2;
            yield return start + length;
        }
    }

    private void ShowVerticalGuide(double x)
    {
        VerticalAlignmentGuide.Height = ActualHeight > 0 ? ActualHeight : SystemParameters.WorkArea.Height;
        Canvas.SetLeft(VerticalAlignmentGuide, x - VerticalAlignmentGuide.Width / 2);
        Canvas.SetTop(VerticalAlignmentGuide, 0);
        System.Windows.Controls.Panel.SetZIndex(VerticalAlignmentGuide, 1000);
        VerticalAlignmentGuide.Visibility = Visibility.Visible;
    }

    private void ShowHorizontalGuide(double y)
    {
        HorizontalAlignmentGuide.Width = ActualWidth > 0 ? ActualWidth : SystemParameters.WorkArea.Width;
        Canvas.SetLeft(HorizontalAlignmentGuide, 0);
        Canvas.SetTop(HorizontalAlignmentGuide, y - HorizontalAlignmentGuide.Height / 2);
        System.Windows.Controls.Panel.SetZIndex(HorizontalAlignmentGuide, 1000);
        HorizontalAlignmentGuide.Visibility = Visibility.Visible;
    }

    private void HideAlignmentGuides()
    {
        VerticalAlignmentGuide.Visibility = Visibility.Collapsed;
        HorizontalAlignmentGuide.Visibility = Visibility.Collapsed;
    }

    private void ClampWidgetsToCanvas()
    {
        foreach (FrameworkElement widget in GetMovableWidgets())
        {
            SetWidgetPosition(widget, Canvas.GetLeft(widget), Canvas.GetTop(widget));
        }
    }

    private IEnumerable<FrameworkElement> GetMovableWidgets()
    {
        yield return ControlWidget;
        yield return TodoWidget;
        yield return SystemWidget;
        yield return SoundWidget;
        yield return CalendarWidget;
        yield return FocusWidget;
        yield return LauncherWidget;
    }

    private static FrameworkElement? FindParentWidget(DependencyObject source)
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is FrameworkElement element
                && element.Name is "ControlWidget" or "TodoWidget" or "SystemWidget" or "SoundWidget" or "CalendarWidget" or "FocusWidget" or "LauncherWidget")
            {
                return element;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void AddTask_Click(object sender, RoutedEventArgs e)
    {
        string title = NewTaskTitleText.Text.Trim();
        string content = NewTaskContentText.Text.Trim();

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        _tasks.Add(new TaskItem(string.IsNullOrWhiteSpace(title) ? "Untitled" : title, content));
        NewTaskTitleText.Clear();
        NewTaskContentText.Clear();
        RenderTasks();
        SaveStateFromUi();
    }

    private void RenderTasks()
    {
        TasksPanel.Children.Clear();

        foreach (TaskItem task in _tasks)
        {
            Border card = new()
            {
                Background = (WpfBrush)FindResource("PanelInnerBrush"),
                BorderBrush = (WpfBrush)FindResource("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10)
            };

            Grid grid = new();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = task.IsExpanded ? GridLength.Auto : new GridLength(0) });
            grid.RowDefinitions.Add(new RowDefinition { Height = task.IsSettingsExpanded ? GridLength.Auto : new GridLength(0) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            WpfButton expandButton = new()
            {
                Content = "",
                Tag = task.IsExpanded ? "\uE70D" : "\uE76C",
                Style = (Style)FindResource("CompactIconButton"),
                Margin = new Thickness(0, 0, 8, 0)
            };
            expandButton.Click += (_, _) =>
            {
                task.IsExpanded = !task.IsExpanded;
                RenderTasks();
                SaveStateFromUi();
            };

            WpfToggleButton doneCheck = new()
            {
                Content = task.IsDone ? "Done" : "Open",
                Tag = "\uE73E",
                Style = (Style)FindResource("WidgetTileToggle"),
                IsChecked = task.IsDone,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Padding = new Thickness(6, 4, 8, 4),
                MinWidth = 86
            };
            doneCheck.Checked += (_, _) => UpdateTaskDone(task, true);
            doneCheck.Unchecked += (_, _) => UpdateTaskDone(task, false);

            WpfTextBox titleBox = new()
            {
                Text = task.Title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 30,
                BorderThickness = new Thickness(0),
                Background = WpfBrushes.Transparent,
                Padding = new Thickness(2, 3, 2, 3),
                ToolTip = "Edit Title"
            };
            titleBox.TextChanged += (_, _) =>
            {
                task.Title = string.IsNullOrWhiteSpace(titleBox.Text) ? "Untitled" : titleBox.Text.Trim();
            };
            titleBox.LostFocus += (_, _) => SaveStateFromUi();

            WpfButton settingsButton = new()
            {
                Content = "",
                Tag = "\uE713",
                Style = (Style)FindResource("CompactIconButton"),
                ToolTip = "Reset Settings",
                BorderBrush = task.IsSettingsExpanded
                    ? (WpfBrush)FindResource("AccentBrush")
                    : (WpfBrush)FindResource("LineBrush"),
                Margin = new Thickness(8, 0, 0, 0)
            };
            settingsButton.Click += (_, _) =>
            {
                task.IsSettingsExpanded = !task.IsSettingsExpanded;
                RenderTasks();
                SaveStateFromUi();
            };

            WpfButton deleteButton = new()
            {
                Content = "",
                Tag = "\uE74D",
                Style = (Style)FindResource("CompactIconButton"),
                ToolTip = "Delete Memo",
                Margin = new Thickness(6, 0, 0, 0)
            };
            deleteButton.Click += (_, _) =>
            {
                _tasks.Remove(task);
                RenderTasks();
                SaveStateFromUi();
            };

            StackPanel titleStack = new() { Orientation = WpfOrientation.Horizontal };
            titleStack.Children.Add(titleBox);

            WpfTextBox contentBox = new()
            {
                Text = task.Content,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 62,
                Margin = new Thickness(0, 10, 0, 8),
                Visibility = task.IsExpanded ? Visibility.Visible : Visibility.Collapsed
            };
            contentBox.LostFocus += (_, _) =>
            {
                task.Content = contentBox.Text;
                SaveStateFromUi();
            };

            Grid resetGrid = CreateTaskResetControls(task);
            resetGrid.Visibility = task.IsSettingsExpanded ? Visibility.Visible : Visibility.Collapsed;

            StackPanel actionStack = new() { Orientation = WpfOrientation.Horizontal };
            actionStack.Children.Add(settingsButton);
            actionStack.Children.Add(deleteButton);

            grid.Children.Add(expandButton);
            Grid.SetColumn(doneCheck, 1);
            grid.Children.Add(doneCheck);
            Grid.SetColumn(titleStack, 1);
            titleStack.Margin = new Thickness(96, 0, 0, 0);
            grid.Children.Add(titleStack);
            Grid.SetColumn(actionStack, 2);
            grid.Children.Add(actionStack);
            Grid.SetRow(contentBox, 1);
            Grid.SetColumnSpan(contentBox, 3);
            grid.Children.Add(contentBox);
            Grid.SetRow(resetGrid, 2);
            Grid.SetColumnSpan(resetGrid, 3);
            grid.Children.Add(resetGrid);

            card.Child = grid;
            TasksPanel.Children.Add(card);
        }

        int remaining = _tasks.Count(task => !task.IsDone);
        TaskCountText.Text = $"{remaining} Open / {_tasks.Count} Total";
    }

    private Grid CreateTaskResetControls(TaskItem task)
    {
        Grid grid = new()
        {
            Margin = new Thickness(0, 8, 0, 0)
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        WpfToggleButton enabledCheck = new()
        {
            Content = "Reset",
            Tag = "\uE72C",
            Style = (Style)FindResource("WidgetTileToggle"),
            IsChecked = task.Reset.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(6, 4, 8, 4)
        };

        WpfToggleButton doneModeButton = CreateSegmentButton("After Done", "\uE73E", task.Reset.Anchor == ResetAnchor.DoneTime);
        WpfToggleButton clockModeButton = CreateSegmentButton("At Time", "\uE823", task.Reset.Anchor == ResetAnchor.ClockTime);
        WpfToggleButton monthlyModeButton = CreateSegmentButton("Monthly", "\uE787", task.Reset.Anchor == ResetAnchor.MonthlyDay);
        WpfButton doneDelayButton = new()
        {
            Content = ResetDelayText(task.Reset),
            Tag = "\uE121",
            Style = (Style)FindResource("IconActionButton"),
            MinWidth = 96,
            Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Delay After Done"
        };

        WpfButton decrementButton = new()
        {
            Content = "",
            Tag = "\uE738",
            Style = (Style)FindResource("CompactIconButton"),
            Margin = new Thickness(0, 0, 4, 0)
        };
        WpfButton incrementButton = new()
        {
            Content = "",
            Tag = "\uE710",
            Style = (Style)FindResource("CompactIconButton"),
            Margin = new Thickness(4, 0, 8, 0)
        };
        TextBlock intervalValueText = new()
        {
            Text = Math.Clamp(task.Reset.Interval, 1, 60).ToString(),
            MinWidth = 38,
            TextAlignment = TextAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 6, 8, 6)
        };
        Border intervalValueTile = new()
        {
            Background = new SolidColorBrush(WpfColor.FromRgb(38, 51, 66)),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Child = intervalValueText
        };

        WpfButton monthlyDayDownButton = new()
        {
            Content = "",
            Tag = "\uE738",
            Style = (Style)FindResource("CompactIconButton"),
            Margin = new Thickness(0, 0, 4, 0)
        };
        WpfButton monthlyDayUpButton = new()
        {
            Content = "",
            Tag = "\uE710",
            Style = (Style)FindResource("CompactIconButton"),
            Margin = new Thickness(4, 0, 8, 0)
        };
        TextBlock monthlyDayValueText = new()
        {
            Text = Math.Clamp(task.Reset.MonthlyDay, 1, 31).ToString(),
            MinWidth = 38,
            TextAlignment = TextAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(8, 6, 8, 6)
        };
        Border monthlyDayTile = new()
        {
            Background = new SolidColorBrush(WpfColor.FromRgb(38, 51, 66)),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Child = monthlyDayValueText
        };
        TextBlock monthlyDaySuffix = new()
        {
            Text = "Day",
            Style = (Style)FindResource("MutedText"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        WpfToggleButton minuteButton = CreateSegmentButton("Min", "\uE121", task.Reset.Unit == ResetUnit.Minute);
        WpfToggleButton hourButton = CreateSegmentButton("Hour", "\uE121", task.Reset.Unit == ResetUnit.Hour);
        WpfToggleButton dayButton = CreateSegmentButton("Day", "\uE787", task.Reset.Unit == ResetUnit.Day);

        WpfButton clockTimeButton = new()
        {
            Content = NormalizeClockTime(task.Reset.ClockTime),
            Tag = "\uE823",
            Style = (Style)FindResource("IconActionButton"),
            MinWidth = 96,
            Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(8, 0, 8, 0),
            ToolTip = "Reset Time"
        };

        TextBlock nextText = new()
        {
            Text = TaskResetSummary(task),
            Style = (Style)FindResource("MutedText"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        StackPanel firstRow = new() { Orientation = WpfOrientation.Horizontal };
        firstRow.Children.Add(enabledCheck);
        firstRow.Children.Add(doneModeButton);
        firstRow.Children.Add(clockModeButton);
        firstRow.Children.Add(monthlyModeButton);
        firstRow.Children.Add(nextText);

        StackPanel secondRow = new()
        {
            Orientation = WpfOrientation.Horizontal,
            Margin = new Thickness(0, 7, 0, 0)
        };
        secondRow.Children.Add(doneDelayButton);
        secondRow.Children.Add(monthlyDayDownButton);
        secondRow.Children.Add(monthlyDayTile);
        secondRow.Children.Add(monthlyDayUpButton);
        secondRow.Children.Add(monthlyDaySuffix);
        secondRow.Children.Add(clockTimeButton);

        void RefreshResetUi()
        {
            bool isDoneMode = task.Reset.Anchor == ResetAnchor.DoneTime;
            bool isMonthlyMode = task.Reset.Anchor == ResetAnchor.MonthlyDay;
            bool isClockMode = task.Reset.Anchor == ResetAnchor.ClockTime;
            doneDelayButton.Visibility = isDoneMode ? Visibility.Visible : Visibility.Collapsed;
            monthlyDayDownButton.Visibility = isMonthlyMode ? Visibility.Visible : Visibility.Collapsed;
            monthlyDayTile.Visibility = isMonthlyMode ? Visibility.Visible : Visibility.Collapsed;
            monthlyDayUpButton.Visibility = isMonthlyMode ? Visibility.Visible : Visibility.Collapsed;
            monthlyDaySuffix.Visibility = isMonthlyMode ? Visibility.Visible : Visibility.Collapsed;
            clockTimeButton.Visibility = isDoneMode ? Visibility.Collapsed : Visibility.Visible;
            doneModeButton.IsChecked = isDoneMode;
            clockModeButton.IsChecked = isClockMode;
            monthlyModeButton.IsChecked = isMonthlyMode;
            doneDelayButton.Content = ResetDelayText(task.Reset);
            monthlyDayValueText.Text = Math.Clamp(task.Reset.MonthlyDay, 1, 31).ToString();
            nextText.Text = TaskResetSummary(task);
        }

        void SaveTaskReset()
        {
            task.Reset.Enabled = enabledCheck.IsChecked == true;
            task.Reset.Interval = Math.Clamp(task.Reset.Interval, 1, 60);
            task.Reset.MonthlyDay = Math.Clamp(task.Reset.MonthlyDay, 1, 31);
            ApplyResetDelay(task.Reset, doneDelayButton.Content?.ToString() ?? ResetDelayText(task.Reset));
            task.Reset.ClockTime = NormalizeClockTime(clockTimeButton.Content?.ToString() ?? task.Reset.ClockTime);
            clockTimeButton.Content = task.Reset.ClockTime;
            if (!task.IsDone)
            {
                task.Reset.LastResetAt = DateTime.Now;
            }

            RefreshResetUi();
            SaveStateFromUi();
        }

        enabledCheck.Checked += (_, _) => SaveTaskReset();
        enabledCheck.Unchecked += (_, _) => SaveTaskReset();
        doneDelayButton.Click += (_, _) => ShowDurationWheelPicker(doneDelayButton, selectedValue =>
        {
            doneDelayButton.Content = selectedValue;
            SaveTaskReset();
        });
        monthlyDayDownButton.Click += (_, _) =>
        {
            task.Reset.MonthlyDay = Math.Max(1, task.Reset.MonthlyDay - 1);
            SaveTaskReset();
        };
        monthlyDayUpButton.Click += (_, _) =>
        {
            task.Reset.MonthlyDay = Math.Min(31, task.Reset.MonthlyDay + 1);
            SaveTaskReset();
        };
        doneModeButton.Checked += (_, _) =>
        {
            task.Reset.Anchor = ResetAnchor.DoneTime;
            SaveTaskReset();
        };
        clockModeButton.Checked += (_, _) =>
        {
            task.Reset.Anchor = ResetAnchor.ClockTime;
            SaveTaskReset();
        };
        monthlyModeButton.Checked += (_, _) =>
        {
            task.Reset.Anchor = ResetAnchor.MonthlyDay;
            SaveTaskReset();
        };
        clockTimeButton.Click += (_, _) => ShowTimeTilePicker(clockTimeButton, includeNone: false, selectedValue =>
        {
            clockTimeButton.Content = selectedValue;
            SaveTaskReset();
        });

        grid.Children.Add(firstRow);
        Grid.SetRow(secondRow, 1);
        grid.Children.Add(secondRow);
        RefreshResetUi();

        return grid;
    }

    private WpfToggleButton CreateSegmentButton(string content, string icon, bool isChecked)
    {
        return new WpfToggleButton
        {
            Content = content,
            Tag = icon,
            Style = (Style)FindResource("WidgetTileToggle"),
            IsChecked = isChecked,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(6, 4, 8, 4),
            MinWidth = 64
        };
    }

    private static string TaskResetSummary(TaskItem task)
    {
        if (!task.Reset.Enabled)
        {
            return "Off";
        }

        DateTime? next = task.Reset.NextResetAt(task.IsDone);
        return next is null ? "Wait Done" : $"Next {next:MM.dd HH:mm}";
    }

    private static string NormalizeClockTime(string value)
    {
        if (TimeSpan.TryParse(value, out TimeSpan parsed))
        {
            int hours = Math.Clamp(parsed.Hours, 0, 23);
            int minutes = Math.Clamp(parsed.Minutes, 0, 59);
            return $"{hours:00}:{minutes:00}";
        }

        if (DateTime.TryParse(value, out DateTime dateTime))
        {
            return $"{dateTime.Hour:00}:{dateTime.Minute:00}";
        }

        return "09:00";
    }

    private static string ResetDelayText(ResetSettings reset)
    {
        TimeSpan delay = reset.GetInterval();
        int days = Math.Clamp(delay.Days, 0, 30);
        int hours = Math.Clamp(delay.Hours, 0, 23);
        int minutes = delay.Minutes;
        if (days == 0 && hours == 0 && minutes == 0)
        {
            minutes = 1;
        }

        return $"{days:00}:{hours:00}:{minutes:00}";
    }

    private static void ApplyResetDelay(ResetSettings reset, string value)
    {
        TimeSpan delay = ParseResetDelay(value);
        reset.DelayDays = Math.Clamp(delay.Days, 0, 30);
        reset.DelayHours = Math.Clamp(delay.Hours, 0, 23);
        reset.DelayMinutes = Math.Clamp(delay.Minutes, 0, 59);
    }

    private static TimeSpan ParseResetDelay(string value)
    {
        TimeSpan parsed;
        string[] parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length == 3
            && int.TryParse(parts[0], out int days)
            && int.TryParse(parts[1], out int hours)
            && int.TryParse(parts[2], out int minutes))
        {
            parsed = new TimeSpan(
                Math.Clamp(days, 0, 30),
                Math.Clamp(hours, 0, 23),
                Math.Clamp(minutes, 0, 59),
                0);
        }
        else if (!TimeSpan.TryParse(value, out parsed))
        {
            return TimeSpan.FromMinutes(30);
        }

        int totalMinutes = Math.Clamp((int)Math.Round(parsed.TotalMinutes), 1, 30 * 24 * 60 + 23 * 60 + 59);
        return TimeSpan.FromMinutes(totalMinutes);
    }

    private static string DurationText(int totalMinutes)
    {
        int safeMinutes = Math.Max(1, totalMinutes);
        int hours = safeMinutes / 60;
        int minutes = safeMinutes % 60;
        return $"{hours:00}:{minutes:00}";
    }

    private void UpdateTaskDone(TaskItem task, bool isDone)
    {
        task.IsDone = isDone;
        task.Reset.CompletedAt = isDone ? DateTime.Now : null;
        if (isDone && task.Reset.Anchor == ResetAnchor.ClockTime)
        {
            task.Reset.LastResetAt = DateTime.MinValue;
        }
        else if (isDone && task.Reset.Anchor == ResetAnchor.MonthlyDay)
        {
            task.Reset.LastResetAt = DateTime.Now;
        }

        SaveStateFromUi();
        RenderTasks();
    }

    private void NumberOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(character => !char.IsDigit(character));
    }

    private void CheckTaskResets()
    {
        bool changed = false;
        DateTime now = DateTime.Now;

        foreach (TaskItem task in _tasks)
        {
            DateTime? nextReset = task.Reset.NextResetAt(task.IsDone);
            if (!task.Reset.Enabled || !task.IsDone || nextReset is null || now < nextReset)
            {
                continue;
            }

            task.IsDone = false;
            task.Reset.CompletedAt = null;
            task.Reset.LastResetAt = now;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        RenderTasks();
        SaveStateFromUi();
    }

    private void AddEvent_Click(object sender, RoutedEventArgs e)
    {
        AddEvent();
    }

    private void NewEventText_KeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddEvent();
            e.Handled = true;
        }
    }

    private void AddEvent()
    {
        string title = NewEventText.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        string key = DateKey(_selectedDate);
        if (!_events.TryGetValue(key, out List<CalendarEvent>? dateEvents))
        {
            dateEvents = [];
            _events[key] = dateEvents;
        }

        string location = NewEventLocationText.Text.Trim();
        string startTime = _newEventStartTime;
        string endTime = _newEventEndTime;
        dateEvents.Add(new CalendarEvent(title, location, startTime, endTime));
        NewEventText.Clear();
        NewEventLocationText.Clear();
        SetNewEventTime(NewEventStartButton, string.Empty);
        SetNewEventTime(NewEventEndButton, string.Empty);
        RenderEvents();
        RenderCalendar();
        SaveStateFromUi();
    }

    private void RenderEvents()
    {
        EventsPanel.Children.Clear();
        SelectedDateText.Text = $"{_selectedDate:yyyy.MM.dd ddd}";

        string key = DateKey(_selectedDate);
        if (!_events.TryGetValue(key, out List<CalendarEvent>? dateEvents) || dateEvents.Count == 0)
        {
            EventsPanel.Children.Add(new TextBlock
            {
                Text = "No Events.",
                Foreground = (WpfBrush)FindResource("TextMutedBrush"),
                Margin = new Thickness(0, 4, 0, 0)
            });
            return;
        }

        foreach (CalendarEvent calendarEvent in dateEvents.OrderBy(calendarEvent => EventSortKey(calendarEvent.StartTime)))
        {
            Border eventTile = new()
            {
                Background = (WpfBrush)FindResource("PanelInnerBrush"),
                BorderBrush = (WpfBrush)FindResource("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9),
                Margin = new Thickness(0, 0, 0, 8)
            };

            Grid row = new();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border icon = new()
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(WpfColor.FromRgb(38, 116, 95)),
                Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock
                {
                    Text = "•",
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            TextBlock title = new()
            {
                Text = calendarEvent.Title,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold
            };

            StackPanel detailStack = new()
            {
                VerticalAlignment = VerticalAlignment.Center
            };
            detailStack.Children.Add(title);

            string timeText = EventTimeText(calendarEvent);
            if (!string.IsNullOrWhiteSpace(timeText))
            {
                detailStack.Children.Add(new TextBlock
                {
                    Text = timeText,
                    Style = (Style)FindResource("MutedText"),
                    FontSize = 11,
                    Margin = new Thickness(0, 3, 0, 0)
                });
            }

            if (!string.IsNullOrWhiteSpace(calendarEvent.Location))
            {
                detailStack.Children.Add(new TextBlock
                {
                    Text = calendarEvent.Location,
                    Style = (Style)FindResource("MutedText"),
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            WpfButton deleteButton = new()
            {
                Content = "Delete",
                Tag = "\uE74D",
                Style = (Style)FindResource("IconActionButton"),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(8, 0, 0, 0)
            };
            deleteButton.Click += (_, _) =>
            {
                dateEvents.Remove(calendarEvent);
                if (dateEvents.Count == 0)
                {
                    _events.Remove(key);
                }

                RenderEvents();
                RenderCalendar();
                SaveStateFromUi();
            };

            row.Children.Add(icon);
            Grid.SetColumn(detailStack, 1);
            row.Children.Add(detailStack);
            Grid.SetColumn(deleteButton, 1);
            Grid.SetColumn(deleteButton, 2);
            row.Children.Add(deleteButton);
            eventTile.Child = row;
            EventsPanel.Children.Add(eventTile);
        }
    }

    private void EventTimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton button)
        {
            return;
        }

        ShowEventTimePicker(button);
    }

    private void ShowEventTimePicker(WpfButton targetButton)
    {
        ShowTimeTilePicker(targetButton, includeNone: true, selectedValue => SetNewEventTime(targetButton, selectedValue));
    }

    private void ShowTimeTilePicker(WpfButton targetButton, bool includeNone, Action<string> onSelected)
    {
        System.Windows.Controls.Primitives.Popup popup = new()
        {
            PlacementTarget = targetButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true
        };

        Border shell = new()
        {
            Background = (WpfBrush)FindResource("PanelBrush"),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10)
        };

        Grid picker = new();
        picker.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        picker.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        picker.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });

        picker.Children.Add(new TextBlock
        {
            Text = "Hour",
            Style = (Style)FindResource("MutedText"),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6)
        });
        TextBlock minuteHeader = new()
        {
            Text = "Minute",
            Style = (Style)FindResource("MutedText"),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetColumn(minuteHeader, 1);
        picker.Children.Add(minuteHeader);

        System.Windows.Controls.ListBox hourList = CreateTimeWheelList(Enumerable.Range(0, 24).Select(value => value.ToString("00")));
        System.Windows.Controls.ListBox minuteList = CreateTimeWheelList(Enumerable.Range(0, 60).Select(value => value.ToString("00")));
        Grid.SetRow(hourList, 1);
        Grid.SetRow(minuteList, 1);
        Grid.SetColumn(minuteList, 1);
        picker.Children.Add(hourList);
        picker.Children.Add(minuteList);

        string current = targetButton.Content?.ToString() ?? "09:00";
        if (!TimeSpan.TryParse(current, out TimeSpan selected))
        {
            selected = new TimeSpan(9, 0, 0);
        }

        hourList.SelectedItem = selected.Hours.ToString("00");
        minuteList.SelectedItem = selected.Minutes.ToString("00");
        hourList.ScrollIntoView(hourList.SelectedItem);
        minuteList.ScrollIntoView(minuteList.SelectedItem);

        StackPanel actions = new()
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        if (includeNone)
        {
            WpfButton noneButton = new()
            {
                Content = "None",
                Tag = "\uE711",
                Style = (Style)FindResource("IconActionButton"),
                Margin = new Thickness(0, 0, 8, 0)
            };
            noneButton.Click += (_, _) =>
            {
                onSelected(string.Empty);
                popup.IsOpen = false;
            };
            actions.Children.Add(noneButton);
        }

        WpfButton applyButton = new()
        {
            Content = "Apply",
            Tag = "\uE73E",
            Style = (Style)FindResource("IconActionButton")
        };
        applyButton.Click += (_, _) =>
        {
            string hour = hourList.SelectedItem?.ToString() ?? "09";
            string minute = minuteList.SelectedItem?.ToString() ?? "00";
            onSelected($"{hour}:{minute}");
            popup.IsOpen = false;
        };
        actions.Children.Add(applyButton);
        Grid.SetRow(actions, 2);
        Grid.SetColumnSpan(actions, 2);
        picker.Children.Add(actions);

        shell.Child = picker;
        popup.Child = shell;
        popup.IsOpen = true;
    }

    private void ShowDurationWheelPicker(WpfButton targetButton, Action<string> onSelected)
    {
        System.Windows.Controls.Primitives.Popup popup = new()
        {
            PlacementTarget = targetButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true
        };

        Border shell = new()
        {
            Background = (WpfBrush)FindResource("PanelBrush"),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10)
        };

        Grid picker = new();
        picker.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        picker.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        picker.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        picker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });

        picker.Children.Add(new TextBlock
        {
            Text = "Day",
            Style = (Style)FindResource("MutedText"),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6)
        });
        TextBlock hourHeader = new()
        {
            Text = "Hour",
            Style = (Style)FindResource("MutedText"),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetColumn(hourHeader, 1);
        picker.Children.Add(hourHeader);
        TextBlock minuteHeader = new()
        {
            Text = "Minute",
            Style = (Style)FindResource("MutedText"),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetColumn(minuteHeader, 2);
        picker.Children.Add(minuteHeader);

        System.Windows.Controls.ListBox dayList = CreateTimeWheelList(Enumerable.Range(0, 31).Select(value => value.ToString("00")));
        System.Windows.Controls.ListBox hourList = CreateTimeWheelList(Enumerable.Range(0, 24).Select(value => value.ToString("00")));
        System.Windows.Controls.ListBox minuteList = CreateTimeWheelList(Enumerable.Range(0, 60).Select(value => value.ToString("00")));
        Grid.SetRow(dayList, 1);
        Grid.SetRow(hourList, 1);
        Grid.SetRow(minuteList, 1);
        Grid.SetColumn(hourList, 1);
        Grid.SetColumn(minuteList, 2);
        picker.Children.Add(dayList);
        picker.Children.Add(hourList);
        picker.Children.Add(minuteList);

        TimeSpan current = ParseResetDelay(targetButton.Content?.ToString() ?? "00:30");
        dayList.SelectedItem = Math.Min(30, current.Days).ToString("00");
        hourList.SelectedItem = current.Hours.ToString("00");
        minuteList.SelectedItem = current.Minutes.ToString("00");
        dayList.ScrollIntoView(dayList.SelectedItem);
        hourList.ScrollIntoView(hourList.SelectedItem);
        minuteList.ScrollIntoView(minuteList.SelectedItem);

        WpfButton applyButton = new()
        {
            Content = "Apply",
            Tag = "\uE73E",
            Style = (Style)FindResource("IconActionButton"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        applyButton.Click += (_, _) =>
        {
            int day = int.TryParse(dayList.SelectedItem?.ToString(), out int selectedDay) ? selectedDay : 0;
            int hour = int.TryParse(hourList.SelectedItem?.ToString(), out int selectedHour) ? selectedHour : 0;
            int minute = int.TryParse(minuteList.SelectedItem?.ToString(), out int selectedMinute) ? selectedMinute : 30;
            if (day == 0 && hour == 0 && minute == 0)
            {
                minute = 1;
            }

            onSelected($"{day:00}:{hour:00}:{minute:00}");
            popup.IsOpen = false;
        };
        Grid.SetRow(applyButton, 2);
        Grid.SetColumnSpan(applyButton, 3);
        picker.Children.Add(applyButton);

        shell.Child = picker;
        popup.Child = shell;
        popup.IsOpen = true;
    }

    private System.Windows.Controls.ListBox CreateTimeWheelList(IEnumerable<string> values)
    {
        System.Windows.Controls.ListBox listBox = new()
        {
            Height = 168,
            Background = new SolidColorBrush(WpfColor.FromRgb(32, 40, 50)),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            Foreground = (WpfBrush)FindResource("TextPrimaryBrush"),
            Padding = new Thickness(4)
        };

        foreach (string value in values)
        {
            listBox.Items.Add(value);
        }

        return listBox;
    }

    private void ShowNumberTilePicker(WpfButton targetButton, IEnumerable<int> choices, Action<int> onSelected)
    {
        System.Windows.Controls.Primitives.Popup popup = new()
        {
            PlacementTarget = targetButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true
        };

        Border shell = new()
        {
            Background = (WpfBrush)FindResource("PanelBrush"),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10)
        };

        Grid picker = new();
        picker.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        picker.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        picker.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        picker.Children.Add(new TextBlock
        {
            Text = "Minutes",
            Style = (Style)FindResource("MutedText"),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6)
        });

        System.Windows.Controls.ListBox minuteList = CreateTimeWheelList(choices.Select(choice => choice.ToString()));
        minuteList.Width = 96;
        Grid.SetRow(minuteList, 1);
        picker.Children.Add(minuteList);
        minuteList.SelectedItem = targetButton.Content?.ToString() ?? choices.FirstOrDefault().ToString();
        minuteList.ScrollIntoView(minuteList.SelectedItem);

        WpfButton applyButton = new()
        {
            Content = "Apply",
            Tag = "\uE73E",
            Style = (Style)FindResource("IconActionButton"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        applyButton.Click += (_, _) =>
        {
            if (int.TryParse(minuteList.SelectedItem?.ToString(), out int value))
            {
                onSelected(value);
            }

            popup.IsOpen = false;
        };
        Grid.SetRow(applyButton, 2);
        picker.Children.Add(applyButton);

        shell.Child = picker;
        popup.Child = shell;
        popup.IsOpen = true;
    }

    private void SetNewEventTime(WpfButton button, string value)
    {
        string normalized = NormalizeOptionalTime(value);
        if (button == NewEventStartButton)
        {
            _newEventStartTime = normalized;
        }
        else
        {
            _newEventEndTime = normalized;
        }

        button.Content = string.IsNullOrWhiteSpace(normalized) ? "None" : normalized;
    }

    private static string NormalizeOptionalTime(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : NormalizeClockTime(value);
    }

    private static string EventTimeText(CalendarEvent calendarEvent)
    {
        if (string.IsNullOrWhiteSpace(calendarEvent.StartTime) && string.IsNullOrWhiteSpace(calendarEvent.EndTime))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(calendarEvent.EndTime))
        {
            return calendarEvent.StartTime;
        }

        if (string.IsNullOrWhiteSpace(calendarEvent.StartTime))
        {
            return calendarEvent.EndTime;
        }

        return $"{calendarEvent.StartTime} - {calendarEvent.EndTime}";
    }

    private static TimeSpan EventSortKey(string value)
    {
        return TimeSpan.TryParse(value, out TimeSpan time) ? time : TimeSpan.MaxValue;
    }

    private void RefreshResources()
    {
        ResourceSnapshot snapshot = _resourceSampler.Sample();

        CpuBar.Value = snapshot.CpuPercent;
        CpuText.Text = $"{snapshot.CpuPercent:0}%";

        MemoryBar.Value = snapshot.MemoryPercent;
        MemoryText.Text = $"{snapshot.MemoryPercent:0}%  {snapshot.MemoryUsedGb:0.0}/{snapshot.MemoryTotalGb:0.0} GB";

        GpuBar.Value = snapshot.GpuPercent;
        GpuText.Text = snapshot.GpuAvailable ? $"{snapshot.GpuPercent:0}%" : "Unavailable";

        NetworkText.Text = $"Down {snapshot.DownloadKbps:0} KB/s   Up {snapshot.UploadKbps:0} KB/s";
        AppMemoryText.Text = $"{Process.GetCurrentProcess().WorkingSet64 / 1024d / 1024d:0.0} MB";

        AddGraphSample(_cpuSamples, snapshot.CpuPercent);
        AddGraphSample(_memorySamples, snapshot.MemoryPercent);
        AddGraphSample(_gpuSamples, snapshot.GpuPercent);
        RenderGraph(CpuGraphCanvas.ActualWidth, CpuGraphCanvas.ActualHeight, _cpuSamples, CpuGraphLine);
        RenderGraph(MemoryGraphCanvas.ActualWidth, MemoryGraphCanvas.ActualHeight, _memorySamples, MemoryGraphLine);
        RenderGraph(GpuGraphCanvas.ActualWidth, GpuGraphCanvas.ActualHeight, _gpuSamples, GpuGraphLine);

        UpdateClock();
    }

    private static void AddGraphSample(Queue<double> samples, double value)
    {
        samples.Enqueue(Math.Clamp(value, 0, 100));
        while (samples.Count > GraphSampleLimit)
        {
            samples.Dequeue();
        }
    }

    private static void RenderGraph(double width, double height, IEnumerable<double> samples, System.Windows.Shapes.Polyline line)
    {
        double safeWidth = Math.Max(1, width);
        double safeHeight = Math.Max(1, height);
        double[] values = samples.ToArray();
        PointCollection points = [];

        for (int i = 0; i < values.Length; i++)
        {
            double x = values.Length == 1 ? safeWidth : i * safeWidth / (values.Length - 1);
            double y = safeHeight - values[i] * safeHeight / 100d;
            points.Add(new WpfPoint(x, y));
        }

        line.Points = points;
    }

    private void RefreshAudioMixer(bool force)
    {
        if (SoundWidget.Visibility != Visibility.Visible)
        {
            return;
        }

        if (_isAdjustingAudioVolume && !force)
        {
            return;
        }

        _audioRefreshTick++;
        if (!force && _audioRefreshTick % 2 != 0)
        {
            return;
        }

        _isApplyingAudioUi = true;
        try
        {
            AudioMixerSnapshot snapshot = _audioMixer.GetSnapshot();
            ApplyAudioDeviceSelectors(snapshot);
            MasterVolumeSlider.Value = Math.Clamp(snapshot.MasterVolume * 100, 0, 100);
            MasterMuteButton.IsChecked = snapshot.MasterMuted;
            SoundUpdatedText.Text = $"Updated {DateTime.Now:HH:mm:ss}";
            RenderAudioSessions(snapshot.Sessions);
        }
        catch
        {
            SoundUpdatedText.Text = "Audio Mixer Unavailable";
            AudioSessionsPanel.Children.Clear();
            AudioSessionsPanel.Children.Add(new TextBlock
            {
                Text = "Could Not Read Audio Sessions.",
                Style = (Style)FindResource("MutedText"),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 14, 0, 0)
            });
        }
        finally
        {
            _isApplyingAudioUi = false;
        }
    }

    private void RenderAudioSessions(IReadOnlyList<AudioSessionInfo> sessions)
    {
        AudioSessionsPanel.Children.Clear();

        if (sessions.Count == 0)
        {
            AudioSessionsPanel.Children.Add(new TextBlock
            {
                Text = "No Active Audio Sessions.",
                Style = (Style)FindResource("MutedText"),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 14, 0, 0)
            });
            return;
        }

        foreach (AudioSessionInfo session in sessions)
        {
            Border card = new()
            {
                Background = (WpfBrush)FindResource("PanelInnerBrush"),
                BorderBrush = (WpfBrush)FindResource("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 10)
            };

            Grid grid = new();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            UIElement icon = CreateAudioSessionIconVisual(session, 28);
            Grid.SetRowSpan(icon, 2);
            grid.Children.Add(icon);

            TextBlock title = new()
            {
                Text = session.Name,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(title, 1);
            grid.Children.Add(title);
            TextBlock valueText = new()
            {
                Text = $"{Math.Round(session.Volume * 100):0}%",
                Style = (Style)FindResource("MutedText"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(valueText, 2);
            grid.Children.Add(valueText);

            Slider slider = new()
            {
                Minimum = 0,
                Maximum = 100,
                Value = Math.Clamp(session.Volume * 100, 0, 100),
                Tag = new AudioSessionSliderContext(session.Id, valueText),
                Margin = new Thickness(10, 8, 8, 0),
                IsMoveToPointEnabled = true
            };
            slider.ValueChanged += AudioSessionVolume_ValueChanged;
            slider.PreviewMouseLeftButtonDown += AudioVolumeSlider_DragStarted;
            slider.PreviewMouseLeftButtonUp += AudioVolumeSlider_DragCompleted;
            slider.LostMouseCapture += AudioVolumeSlider_DragCompleted;
            Grid.SetRow(slider, 1);
            Grid.SetColumn(slider, 1);
            grid.Children.Add(slider);

            WpfToggleButton muteButton = new()
            {
                Content = "Mute",
                Tag = "\uE198",
                Style = (Style)FindResource("WidgetTileToggle"),
                IsChecked = session.IsMuted,
                MinWidth = 82,
                Margin = new Thickness(8, 4, 0, 0)
            };
            muteButton.Checked += (_, _) => SetSessionMute(session.Id, true);
            muteButton.Unchecked += (_, _) => SetSessionMute(session.Id, false);
            Grid.SetRow(muteButton, 1);
            Grid.SetColumn(muteButton, 2);
            grid.Children.Add(muteButton);

            if (session.ProcessId > 0)
            {
                string selectedDeviceId = string.IsNullOrWhiteSpace(session.OutputDeviceId)
                    ? _audioMixer.DefaultPlaybackDeviceId
                    : session.OutputDeviceId;
                WpfButton deviceButton = CreateAudioDevicePickerButton(
                    GetAudioDeviceButtonText(_audioMixer.PlaybackDevices, selectedDeviceId, "Default Output"),
                    "\uE995",
                    selectedDeviceId,
                    _audioMixer.PlaybackDevices,
                    deviceId =>
                    {
                        _audioMixer.SetApplicationOutputDevice(session.ProcessId, deviceId);
                        RefreshAudioMixer(true);
                    });
                deviceButton.Margin = new Thickness(10, 8, 0, 0);

                Grid.SetRow(deviceButton, 2);
                Grid.SetColumn(deviceButton, 1);
                Grid.SetColumnSpan(deviceButton, 2);
                grid.Children.Add(deviceButton);
            }

            card.Child = grid;
            AudioSessionsPanel.Children.Add(card);
        }
    }

    private void ApplyAudioDeviceSelectors(AudioMixerSnapshot snapshot)
    {
        PlaybackDeviceButton.Content = GetAudioDeviceButtonText(snapshot.PlaybackDevices, snapshot.DefaultPlaybackDeviceId, "No Playback Device");
        PlaybackDeviceButton.Tag = "\uE995";
        PlaybackDeviceButton.ToolTip = PlaybackDeviceButton.Content;

        RecordingDeviceButton.Content = GetAudioDeviceButtonText(snapshot.RecordingDevices, snapshot.DefaultRecordingDeviceId, "No Recording Device");
        RecordingDeviceButton.Tag = "\uE720";
        RecordingDeviceButton.ToolTip = RecordingDeviceButton.Content;
    }

    private WpfButton CreateAudioDevicePickerButton(
        string text,
        string icon,
        string selectedDeviceId,
        IReadOnlyList<AudioDeviceInfo> devices,
        Action<string> onSelected)
    {
        WpfButton button = new()
        {
            Content = text,
            Tag = icon,
            ToolTip = text,
            Style = (Style)FindResource("IconActionButton"),
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            MinHeight = 32
        };
        button.Click += (_, _) => ShowAudioDevicePicker(button, devices, selectedDeviceId, onSelected);
        return button;
    }

    private void AudioDeviceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isApplyingAudioUi || sender is not WpfButton button)
        {
            return;
        }

        if (button == PlaybackDeviceButton)
        {
            ShowAudioDevicePicker(button, _audioMixer.PlaybackDevices, _audioMixer.DefaultPlaybackDeviceId, deviceId =>
            {
                _audioMixer.SetDefaultDevice(deviceId, EDataFlow.Render);
                RefreshAudioMixer(true);
            });
        }
        else if (button == RecordingDeviceButton)
        {
            ShowAudioDevicePicker(button, _audioMixer.RecordingDevices, _audioMixer.DefaultRecordingDeviceId, deviceId =>
            {
                _audioMixer.SetDefaultDevice(deviceId, EDataFlow.Capture);
                RefreshAudioMixer(true);
            });
        }
    }

    private void ShowAudioDevicePicker(
        WpfButton targetButton,
        IReadOnlyList<AudioDeviceInfo> devices,
        string selectedDeviceId,
        Action<string> onSelected)
    {
        System.Windows.Controls.Primitives.Popup popup = new()
        {
            PlacementTarget = targetButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true
        };

        Border shell = new()
        {
            Background = (WpfBrush)FindResource("PanelBrush"),
            BorderBrush = (WpfBrush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            MaxWidth = 360
        };

        StackPanel list = new();
        if (devices.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "No Devices Found.",
                Style = (Style)FindResource("MutedText"),
                Margin = new Thickness(8)
            });
        }
        else
        {
            foreach (AudioDeviceInfo device in devices)
            {
                WpfButton item = new()
                {
                    Content = device.Name,
                    Tag = device.Id.Equals(selectedDeviceId, StringComparison.OrdinalIgnoreCase) ? "\uE73E" : "\uE995",
                    ToolTip = device.Name,
                    Style = (Style)FindResource("IconActionButton"),
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 6),
                    MinHeight = 34
                };
                item.Click += (_, _) =>
                {
                    popup.IsOpen = false;
                    onSelected(device.Id);
                };
                list.Children.Add(item);
            }
        }

        shell.Child = list;
        popup.Child = shell;
        popup.IsOpen = true;
    }

    private static string GetAudioDeviceButtonText(IReadOnlyList<AudioDeviceInfo> devices, string selectedDeviceId, string fallback)
    {
        if (string.IsNullOrWhiteSpace(selectedDeviceId))
        {
            return devices.FirstOrDefault()?.Name ?? fallback;
        }

        return devices.FirstOrDefault(device => device.Id.Equals(selectedDeviceId, StringComparison.OrdinalIgnoreCase))?.Name
            ?? devices.FirstOrDefault()?.Name
            ?? fallback;
    }

    private UIElement CreateAudioSessionIconVisual(AudioSessionInfo session, double size)
    {
        System.Windows.Controls.Image? icon = session.ProcessId > 0
            ? CreateProcessIconImage(session.ProcessId, size)
            : null;
        if (icon is not null)
        {
            return icon;
        }

        return new TextBlock
        {
            Text = "\uE995",
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = size * 0.78,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = size,
            Height = size
        };
    }

    private void MasterVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isApplyingAudioUi)
        {
            return;
        }

        _audioMixer.SetMasterVolume((float)Math.Clamp(e.NewValue / 100d, 0, 1));
    }

    private void AudioVolumeSlider_DragStarted(object sender, MouseButtonEventArgs e)
    {
        _isAdjustingAudioVolume = true;
    }

    private void AudioVolumeSlider_DragCompleted(object sender, RoutedEventArgs e)
    {
        _isAdjustingAudioVolume = false;
    }

    private void MasterMute_Changed(object sender, RoutedEventArgs e)
    {
        if (_isApplyingAudioUi)
        {
            return;
        }

        _audioMixer.SetMasterMute(MasterMuteButton.IsChecked == true);
    }

    private void AudioSessionVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isApplyingAudioUi || sender is not Slider slider || slider.Tag is not AudioSessionSliderContext context)
        {
            return;
        }

        double value = Math.Clamp(e.NewValue, 0, 100);
        context.ValueText.Text = $"{Math.Round(value):0}%";
        _audioMixer.SetSessionVolume(context.SessionId, (float)(value / 100d));
    }

    private void SetSessionMute(string sessionId, bool isMuted)
    {
        if (_isApplyingAudioUi)
        {
            return;
        }

        _audioMixer.SetSessionMute(sessionId, isMuted);
    }

    private sealed record AudioSessionSliderContext(string SessionId, TextBlock ValueText);

    private void UpdateClock()
    {
        DateTime now = DateTime.Now;
        ClockText.Text = now.ToString("yyyy.MM.dd ddd HH:mm:ss");
        ResourceUpdatedText.Text = $"Updated {now:HH:mm:ss}";
    }

    private void RenderCalendar()
    {
        CalendarGrid.Children.Clear();
        MonthText.Text = _calendarMonth.ToString("yyyy.MM");

        int leadingBlankDays = (int)_calendarMonth.DayOfWeek;
        int daysInMonth = DateTime.DaysInMonth(_calendarMonth.Year, _calendarMonth.Month);

        for (int i = 0; i < 42; i++)
        {
            Border cell = new()
            {
                CornerRadius = new CornerRadius(9),
                Margin = new Thickness(2),
                Padding = new Thickness(4),
                Background = WpfBrushes.Transparent,
                BorderBrush = new SolidColorBrush(WpfColor.FromArgb(70, 184, 192, 204)),
                BorderThickness = new Thickness(1)
            };

            int day = i - leadingBlankDays + 1;
            if (day >= 1 && day <= daysInMonth)
            {
                DateTime date = new(_calendarMonth.Year, _calendarMonth.Month, day);
                bool isToday = date.Date == DateTime.Today;
                bool isSelected = date.Date == _selectedDate.Date;
                bool hasEvents = _events.TryGetValue(DateKey(date), out List<CalendarEvent>? dateEvents)
                    && dateEvents.Count > 0;

                cell.Tag = date;
                cell.Cursor = WpfCursors.Hand;
                cell.MouseLeftButtonDown += CalendarDay_MouseLeftButtonDown;
                cell.Background = isSelected
                    ? new SolidColorBrush(WpfColor.FromRgb(50, 121, 101))
                    : isToday
                        ? new SolidColorBrush(WpfColor.FromRgb(42, 102, 86))
                        : new SolidColorBrush(WpfColor.FromArgb(95, 43, 52, 64));
                cell.BorderBrush = hasEvents || isSelected
                    ? (WpfBrush)FindResource("AccentBrush")
                    : new SolidColorBrush(WpfColor.FromArgb(80, 184, 192, 204));

                StackPanel content = new() { VerticalAlignment = VerticalAlignment.Center };
                content.Children.Add(new TextBlock
                {
                    Text = day.ToString(),
                    TextAlignment = TextAlignment.Center,
                    FontWeight = isToday || isSelected ? FontWeights.SemiBold : FontWeights.Normal
                });

                if (hasEvents)
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = $"● {dateEvents!.Count}",
                        TextAlignment = TextAlignment.Center,
                        FontSize = 10,
                        Foreground = (WpfBrush)FindResource("AccentBrush"),
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                }

                cell.Child = content;
            }

            CalendarGrid.Children.Add(cell);
        }
    }

    private void CalendarDay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: DateTime date })
        {
            return;
        }

        _selectedDate = date;
        RenderCalendar();
        RenderEvents();
        SaveStateFromUi();
    }

    private void PreviousMonth_Click(object sender, RoutedEventArgs e)
    {
        _calendarMonth = _calendarMonth.AddMonths(-1);
        RenderCalendar();
    }

    private void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        _calendarMonth = _calendarMonth.AddMonths(1);
        RenderCalendar();
    }

    private void FocusStartPause_Click(object sender, RoutedEventArgs e)
    {
        if (_focusMode == FocusMode.Timer && !_focusRunning && _focusRemainingSeconds <= 0)
        {
            ResetFocusRemaining();
        }

        _focusRunning = !_focusRunning;
        FocusStartPauseButton.Content = _focusRunning ? "Pause" : "Start";
        SaveStateFromUi();
    }

    private void FocusReset_Click(object sender, RoutedEventArgs e)
    {
        _focusRunning = false;
        if (_focusMode == FocusMode.Timer)
        {
            ResetFocusRemaining();
        }
        else
        {
            _focusElapsedSeconds = 0;
            UpdateFocusText();
        }

        FocusStartPauseButton.Content = "Start";
        SaveStateFromUi();
    }

    private void TickFocusTimer()
    {
        if (!_focusRunning)
        {
            return;
        }

        if (_focusMode == FocusMode.Stopwatch)
        {
            _focusElapsedSeconds++;
            UpdateFocusText();
            return;
        }

        _focusRemainingSeconds = Math.Max(0, _focusRemainingSeconds - 1);
        UpdateFocusText();

        if (_focusRemainingSeconds == 0)
        {
            _focusRunning = false;
            FocusStartPauseButton.Content = "Start";
            SendFocusAlarm();
        }
    }

    private void ResetFocusRemaining()
    {
        _focusRemainingSeconds = Math.Max(1, _focusMinutes) * 60;
        UpdateFocusText();
    }

    private void UpdateFocusText()
    {
        int seconds = _focusMode == FocusMode.Stopwatch ? _focusElapsedSeconds : _focusRemainingSeconds;
        TimeSpan value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        FocusTimeText.Text = $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";
    }

    private void FocusMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingFocusModeUi)
        {
            return;
        }

        _focusRunning = false;
        _focusMode = sender == StopwatchModeButton ? FocusMode.Stopwatch : FocusMode.Timer;
        ApplyFocusModeUi();
        SaveStateFromUi();
    }

    private void ApplyFocusModeUi()
    {
        _isUpdatingFocusModeUi = true;
        TimerModeButton.IsChecked = _focusMode == FocusMode.Timer;
        StopwatchModeButton.IsChecked = _focusMode == FocusMode.Stopwatch;
        _isUpdatingFocusModeUi = false;

        FocusTitleText.Text = _focusMode == FocusMode.Timer ? "Timer" : "Stopwatch";
        FocusMinutesPanel.Visibility = _focusMode == FocusMode.Timer ? Visibility.Visible : Visibility.Collapsed;
        FocusAlarmToggle.Visibility = _focusMode == FocusMode.Timer ? Visibility.Visible : Visibility.Collapsed;
        FocusStartPauseButton.Content = _focusRunning ? "Pause" : "Start";
        UpdateFocusText();
    }

    private void FocusAlarm_Changed(object sender, RoutedEventArgs e)
    {
        _focusAlarmEnabled = FocusAlarmToggle.IsChecked == true;
        if (_isLoaded)
        {
            SaveStateFromUi();
        }
    }

    private void SendFocusAlarm()
    {
        if (!_focusAlarmEnabled)
        {
            return;
        }

        _notifyIcon?.ShowBalloonTip(3000, "Windget", "Focus Timer Finished.", Forms.ToolTipIcon.Info);
    }

    private void FocusDurationButton_Click(object sender, RoutedEventArgs e)
    {
        ShowDurationWheelPicker(FocusDurationButton, selectedValue =>
        {
            TimeSpan duration = ParseResetDelay(selectedValue);
            _focusMinutes = Math.Max(1, (int)Math.Round(duration.TotalMinutes));
            FocusDurationButton.Content = DurationText(_focusMinutes);
            if (!_focusRunning)
            {
                ResetFocusRemaining();
            }

            SaveStateFromUi();
        });
    }

    private void Launcher_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void Launcher_Drop(object sender, System.Windows.DragEventArgs e)
    {
        AddDroppedLaunchers(e, "General");
    }

    private void LauncherCategory_Drop(object sender, System.Windows.DragEventArgs e)
    {
        string category = sender is FrameworkElement { Tag: string tag } ? tag : "General";
        AddDroppedLaunchers(e, category);
    }

    private void AddDroppedLaunchers(System.Windows.DragEventArgs e, string category)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            || e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        EnsureLauncherCategory(category);
        foreach (string path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            AddLauncherFromDrop(path, category);
        }

        RenderLaunchers();
        SaveStateFromUi();
        e.Handled = true;
    }

    private void AddLauncherFromDrop(string path, string category)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = path;
        }

        _launchers.Add(new LauncherItem(name, path, string.Empty, category));
    }

    private void AddLauncherCategory_Click(object sender, RoutedEventArgs e)
    {
        string category = NewLauncherCategoryText.Text.Trim();
        if (string.IsNullOrWhiteSpace(category))
        {
            return;
        }

        EnsureLauncherCategory(category);
        NewLauncherCategoryText.Clear();
        RenderLaunchers();
        SaveStateFromUi();
    }

    private void EnsureLauncherCategory(string category)
    {
        string normalized = LauncherCategory(category);
        if (!_launcherCategories.Any(existing => existing.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            _launcherCategories.Add(normalized);
        }
    }

    private void RenderLaunchers()
    {
        LauncherPanel.Children.Clear();

        List<string> categories = _launcherCategories
            .Concat(_launchers.Select(item => LauncherCategory(item.Category)))
            .DefaultIfEmpty("General")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category)
            .ToList();

        foreach (string category in categories)
        {
            Border section = new()
            {
                Background = (WpfBrush)FindResource("PanelInnerBrush"),
                BorderBrush = (WpfBrush)FindResource("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(9),
                Margin = new Thickness(0, 0, 0, 12),
                AllowDrop = true,
                Tag = category
            };
            section.DragOver += Launcher_DragOver;
            section.Drop += LauncherCategory_Drop;

            StackPanel sectionPanel = new();
            Grid categoryHeader = new() { Margin = new Thickness(2, 0, 0, 8) };
            categoryHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            categoryHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            categoryHeader.Children.Add(new TextBlock
            {
                Text = category,
                Style = (Style)FindResource("MutedText"),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (!category.Equals("General", StringComparison.OrdinalIgnoreCase))
            {
                WpfButton deleteCategoryButton = CreateLauncherCategoryDeleteButton(category);
                Grid.SetColumn(deleteCategoryButton, 1);
                categoryHeader.Children.Add(deleteCategoryButton);
            }

            sectionPanel.Children.Add(categoryHeader);

            List<LauncherItem> categoryItems = _launchers
                .Where(item => LauncherCategory(item.Category).Equals(category, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (categoryItems.Count == 0)
            {
                sectionPanel.Children.Add(new TextBlock
                {
                    Text = "Drop Here.",
                    Style = (Style)FindResource("MutedText"),
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 2)
                });
            }

            if (_state.LauncherIconOnly)
            {
                WrapPanel iconPanel = new()
                {
                    Margin = new Thickness(0, 0, -6, -6)
                };

                foreach (LauncherItem item in categoryItems)
                {
                    iconPanel.Children.Add(CreateLauncherIconOnlyTile(item));
                }

                sectionPanel.Children.Add(iconPanel);
            }
            else
            {
                foreach (LauncherItem item in categoryItems)
                {
                    Grid row = new() { Margin = new Thickness(0, 0, 0, 10) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    WpfButton launchButton = new()
                    {
                        Padding = new Thickness(9),
                        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                        Content = CreateLauncherTileContent(item)
                    };
                    launchButton.Click += (_, _) => Launch(item.Target);

                    WpfButton deleteButton = CreateLauncherDeleteButton(item);

                    row.Children.Add(launchButton);
                    Grid.SetColumn(deleteButton, 1);
                    row.Children.Add(deleteButton);
                    sectionPanel.Children.Add(row);
                }
            }

            section.Child = sectionPanel;
            LauncherPanel.Children.Add(section);
        }
    }

    private Grid CreateLauncherIconOnlyTile(LauncherItem item)
    {
        Grid tile = new()
        {
            Width = 74,
            Height = 74,
            Margin = new Thickness(0, 0, 10, 10),
            ToolTip = $"{item.Name}\n{item.Target}"
        };

        Border launchButton = new()
        {
            Background = WpfBrushes.Transparent,
            Child = CreateLauncherIconVisual(item, 46),
            Cursor = WpfCursors.Hand
        };
        launchButton.MouseLeftButtonUp += (_, _) => Launch(item.Target);

        WpfButton deleteButton = CreateLauncherDeleteButton(item);
        deleteButton.Width = 24;
        deleteButton.Height = 24;
        deleteButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        deleteButton.VerticalAlignment = VerticalAlignment.Top;
        deleteButton.Margin = new Thickness(0, -4, -4, 0);

        tile.Children.Add(launchButton);
        tile.Children.Add(deleteButton);
        return tile;
    }

    private WpfButton CreateLauncherDeleteButton(LauncherItem item)
    {
        WpfButton deleteButton = new()
        {
            Content = "",
            Tag = "\uE74D",
            Style = (Style)FindResource("CompactIconButton"),
            ToolTip = "Delete Launcher",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        deleteButton.Click += (_, _) =>
        {
            _launchers.Remove(item);
            RenderLaunchers();
            SaveStateFromUi();
        };

        return deleteButton;
    }

    private WpfButton CreateLauncherCategoryDeleteButton(string category)
    {
        WpfButton deleteButton = new()
        {
            Content = "",
            Tag = "\uE74D",
            Style = (Style)FindResource("CompactIconButton"),
            ToolTip = "Delete Category",
            Width = 28,
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0)
        };
        deleteButton.Click += (_, _) =>
        {
            DeleteLauncherCategory(category);
        };

        return deleteButton;
    }

    private void DeleteLauncherCategory(string category)
    {
        string normalized = LauncherCategory(category);
        if (normalized.Equals("General", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        for (int i = 0; i < _launchers.Count; i++)
        {
            if (LauncherCategory(_launchers[i].Category).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                _launchers[i].Category = "General";
            }
        }

        for (int i = _launcherCategories.Count - 1; i >= 0; i--)
        {
            if (LauncherCategory(_launcherCategories[i]).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                _launcherCategories.RemoveAt(i);
            }
        }

        EnsureLauncherCategory("General");
        RenderLaunchers();
        SaveStateFromUi();
    }

    private UIElement CreateLauncherTileContent(LauncherItem item)
    {
        Grid grid = new();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Border icon = new()
        {
            Width = 30,
            Height = 30,
            Background = WpfBrushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center,
            Child = CreateLauncherIconVisual(item, 26)
        };

        StackPanel text = new();
        text.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = item.Target,
            Style = (Style)FindResource("MutedText"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        });

        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private UIElement CreateLauncherIconVisual(LauncherItem item, double size = 22)
    {
        string icon = item.Icon.Trim();
        if (IsImagePath(icon))
        {
            try
            {
                return new System.Windows.Controls.Image
                {
                    Source = new BitmapImage(new Uri(icon, UriKind.Absolute)),
                    Width = size,
                    Height = size,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            catch
            {
                // Fall back to a text icon below if the image cannot be loaded.
            }
        }

        System.Windows.Controls.Image? fileIcon = CreateAssociatedIconImage(item.Target, size);
        if (fileIcon is not null)
        {
            return fileIcon;
        }

        bool hasCustomIcon = !string.IsNullOrWhiteSpace(icon);
        return new TextBlock
        {
            Text = hasCustomIcon ? icon : LauncherIcon(item.Target),
            FontFamily = new System.Windows.Media.FontFamily(hasCustomIcon ? "Pretendard, Segoe UI Emoji, Segoe UI Symbol, Segoe MDL2 Assets" : "Segoe MDL2 Assets"),
            FontSize = hasCustomIcon ? Math.Max(16, size * 0.72) : Math.Max(15, size * 0.68),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static System.Windows.Controls.Image? CreateAssociatedIconImage(string target, double size = 22)
    {
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            return null;
        }

        try
        {
            using System.Drawing.Icon? icon = File.Exists(target)
                ? System.Drawing.Icon.ExtractAssociatedIcon(target)
                : System.Drawing.SystemIcons.WinLogo;
            if (icon is null)
            {
                return null;
            }

            ImageSource source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight((int)Math.Ceiling(size), (int)Math.Ceiling(size)));
            source.Freeze();

            return new System.Windows.Controls.Image
            {
                Source = source,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }
        catch
        {
            return null;
        }
    }

    private static System.Windows.Controls.Image? CreateProcessIconImage(uint processId, double size)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            string? path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return CreateAssociatedIconImage(path, size);
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool IsImagePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || !File.Exists(value))
        {
            return false;
        }

        string extension = Path.GetExtension(value);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ico", StringComparison.OrdinalIgnoreCase);
    }

    private static string LauncherCategory(string category)
    {
        return string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();
    }

    private static string LauncherIcon(string target)
    {
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "\uE774";
        }

        if (Directory.Exists(target))
        {
            return "\uE8B7";
        }

        return "\uE8A5";
    }

    private void Launch(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch
        {
            _notifyIcon?.ShowBalloonTip(1500, "Windget", "Could Not Open Launcher Target.", Forms.ToolTipIcon.Warning);
        }
    }

    private void ControlSetting_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostCheck.IsChecked == true;
        if (!_isApplyingSettings && sender == StartupCheck)
        {
            SetStartupEnabled(StartupCheck.IsChecked == true);
        }

        if (_isLoaded)
        {
            SaveStateFromUi();
        }
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: false);
            return key?.GetValue(StartupRegistryName) is string value
                && !string.IsNullOrWhiteSpace(Environment.ProcessPath)
                && value.Contains(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void SetStartupEnabled(bool isEnabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true);
            if (key is null)
            {
                return;
            }

            if (!isEnabled)
            {
                key.DeleteValue(StartupRegistryName, throwOnMissingValue: false);
                return;
            }

            string? path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                key.SetValue(StartupRegistryName, $"\"{path}\"");
            }
        }
        catch
        {
        }
    }

    private void WidgetVisibility_Changed(object sender, RoutedEventArgs e)
    {
        ApplyWidgetVisibility();
        if (_isLoaded)
        {
            SaveStateFromUi();
        }
    }

    private void LauncherViewMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        _state.LauncherIconOnly = LauncherIconOnlyToggle.IsChecked == true;
        RenderLaunchers();
        if (_isLoaded)
        {
            SaveStateFromUi();
        }
    }

    private void ToggleWidgetSettings_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        FrameworkElement? widget = sender is DependencyObject source ? FindParentWidget(source) : null;
        FrameworkElement? panel = widget?.Name switch
        {
            "TodoWidget" => TodoSettingsPanel,
            "SystemWidget" => SystemSettingsPanel,
            "SoundWidget" => SoundSettingsPanel,
            "CalendarWidget" => CalendarSettingsPanel,
            "FocusWidget" => FocusSettingsPanel,
            "LauncherWidget" => LauncherSettingsPanel,
            _ => null
        };

        if (panel is null)
        {
            return;
        }

        panel.Visibility = panel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void GlobalOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isLoaded && !_isApplyingSettings)
        {
            return;
        }

        Opacity = e.NewValue;
        if (_isLoaded)
        {
            SaveStateFromUi();
        }
    }

    private void WidgetOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isLoaded && !_isApplyingSettings)
        {
            return;
        }

        TodoWidget.Opacity = TodoOpacitySlider.Value;
        SystemWidget.Opacity = SystemOpacitySlider.Value;
        SoundWidget.Opacity = SoundOpacitySlider.Value;
        CalendarWidget.Opacity = CalendarOpacitySlider.Value;
        FocusWidget.Opacity = FocusOpacitySlider.Value;
        LauncherWidget.Opacity = LauncherOpacitySlider.Value;

        if (_isLoaded)
        {
            SaveStateFromUi();
        }
    }

    private void SaveLayout_Click(object sender, RoutedEventArgs e)
    {
        SaveStateFromUi();
    }

    private void AutoLayout_Click(object sender, RoutedEventArgs e)
    {
        AutoArrangeWidgets();
        SaveStateFromUi();
    }

    private void AutoArrangeWidgets()
    {
        double canvasWidth = ActualWidth > 0 ? ActualWidth : SystemParameters.WorkArea.Width;
        double canvasHeight = ActualHeight > 0 ? ActualHeight : SystemParameters.WorkArea.Height;
        double margin = canvasWidth >= 2400 ? 24 : 12;
        double gap = canvasWidth >= 2400 ? 18 : 10;
        double screenRatio = Math.Min(canvasWidth / 1920, canvasHeight / 1080);
        double wideScale = Math.Clamp(screenRatio, 1.0, 1.16);
        double fhdScale = Math.Clamp(screenRatio, 0.9, 1.0);
        double compactScale = Math.Clamp(screenRatio, 0.78, 0.92);

        static double Scale(double value, double scale) => Math.Round(value * scale);

        bool IsVisible(FrameworkElement widget) => widget.Visibility == Visibility.Visible;
        bool AnyVisible(params FrameworkElement[] widgets)
        {
            foreach (FrameworkElement widget in widgets)
            {
                if (IsVisible(widget))
                {
                    return true;
                }
            }

            return false;
        }

        void PlaceAt(FrameworkElement widget, double left, double top, double width, double height)
        {
            if (!IsVisible(widget))
            {
                return;
            }

            double safeWidth = Math.Max(widget.MinWidth, width);
            double safeHeight = Math.Max(widget.MinHeight, height);
            double maxHeight = Math.Max(widget.MinHeight, canvasHeight - margin - top);

            SetWidgetSize(widget, safeWidth, Math.Min(safeHeight, maxHeight));
            SetWidgetPosition(widget, Math.Max(margin, left), Math.Max(margin, top));
        }

        double PlannedWidth(FrameworkElement widget, double width) => Math.Max(widget.MinWidth, width);

        if (canvasWidth >= 2560)
        {
            double x = margin;
            double controlWidth = Scale(330, wideScale);
            PlaceAt(ControlWidget, x, margin, controlWidth, Scale(350, wideScale));
            if (IsVisible(ControlWidget))
            {
                x += PlannedWidth(ControlWidget, controlWidth) + gap;
            }

            double systemWidth = Scale(410, wideScale);
            PlaceAt(SystemWidget, x, margin, systemWidth, Scale(600, wideScale));
            if (IsVisible(SystemWidget))
            {
                x += PlannedWidth(SystemWidget, systemWidth) + gap;
            }

            double soundWidth = Scale(390, wideScale);
            PlaceAt(SoundWidget, x, margin, soundWidth, Scale(420, wideScale));
            if (IsVisible(SoundWidget))
            {
                x += PlannedWidth(SoundWidget, soundWidth) + gap;
            }

            double calendarWidth = Scale(430, wideScale);
            PlaceAt(CalendarWidget, x, margin, calendarWidth, Scale(650, wideScale));
            if (IsVisible(CalendarWidget))
            {
                x += PlannedWidth(CalendarWidget, calendarWidth) + gap;
            }

            double todoWidth = Scale(460, wideScale);
            PlaceAt(TodoWidget, x, margin, todoWidth, Scale(650, wideScale));
            if (IsVisible(TodoWidget))
            {
                x += PlannedWidth(TodoWidget, todoWidth) + gap;
            }

            double stackY = margin;
            PlaceAt(FocusWidget, x, stackY, Scale(360, wideScale), Scale(390, wideScale));
            if (IsVisible(FocusWidget))
            {
                stackY += FocusWidget.Height + gap;
            }

            PlaceAt(LauncherWidget, x, stackY, Scale(400, wideScale), Scale(430, wideScale));
            return;
        }

        if (canvasWidth >= 1800 && canvasHeight >= 900)
        {
            double x = margin;
            double y = margin;

            if (AnyVisible(ControlWidget, LauncherWidget))
            {
                double sideWidth = Scale(330, fhdScale);
                double controlHeight = Scale(350, fhdScale);
                PlaceAt(ControlWidget, x, y, sideWidth, controlHeight);
                PlaceAt(LauncherWidget, x, y + Math.Max(ControlWidget.MinHeight, controlHeight) + gap, sideWidth, Scale(320, fhdScale));
                x += PlannedWidth(ControlWidget, sideWidth) + gap;
            }

            double systemWidth = Scale(350, fhdScale);
            PlaceAt(SystemWidget, x, y, systemWidth, Scale(560, fhdScale));
            if (IsVisible(SystemWidget))
            {
                x += PlannedWidth(SystemWidget, systemWidth) + gap;
            }

            double soundWidth = Scale(350, fhdScale);
            PlaceAt(SoundWidget, x, y, soundWidth, Scale(430, fhdScale));
            if (IsVisible(SoundWidget))
            {
                x += PlannedWidth(SoundWidget, soundWidth) + gap;
            }

            double calendarWidth = Scale(380, fhdScale);
            PlaceAt(CalendarWidget, x, y, calendarWidth, Scale(600, fhdScale));
            if (IsVisible(CalendarWidget))
            {
                x += PlannedWidth(CalendarWidget, calendarWidth) + gap;
            }

            double todoWidth = Scale(420, fhdScale);
            PlaceAt(TodoWidget, x, y, todoWidth, Scale(600, fhdScale));
            if (IsVisible(TodoWidget))
            {
                x += PlannedWidth(TodoWidget, todoWidth) + gap;
            }

            PlaceAt(FocusWidget, x, y, Scale(330, fhdScale), Scale(390, fhdScale));
            return;
        }

        double flowX = margin;
        double flowY = margin;
        double rowHeight = 0;

        void Place(FrameworkElement widget, double width, double height)
        {
            if (!IsVisible(widget))
            {
                return;
            }

            double safeWidth = Math.Max(widget.MinWidth, width);
            double safeHeight = Math.Max(widget.MinHeight, height);

            if (flowX + safeWidth > canvasWidth - margin && flowX > margin)
            {
                flowX = margin;
                flowY += rowHeight + gap;
                rowHeight = 0;
            }

            double maxHeight = Math.Max(widget.MinHeight, canvasHeight - margin - flowY);
            safeHeight = Math.Min(safeHeight, maxHeight);
            SetWidgetSize(widget, safeWidth, safeHeight);
            SetWidgetPosition(widget, flowX, flowY);
            flowX += safeWidth + gap;
            rowHeight = Math.Max(rowHeight, safeHeight);
        }

        Place(ControlWidget, Scale(330, compactScale), Scale(350, compactScale));
        Place(SystemWidget, Scale(410, compactScale), Scale(600, compactScale));
        Place(SoundWidget, Scale(390, compactScale), Scale(420, compactScale));
        Place(CalendarWidget, Scale(430, compactScale), Scale(650, compactScale));
        Place(TodoWidget, Scale(460, compactScale), Scale(650, compactScale));
        Place(FocusWidget, Scale(360, compactScale), Scale(390, compactScale));
        Place(LauncherWidget, Scale(400, compactScale), Scale(430, compactScale));
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _isExitRequested = true;
        Close();
    }

    private void LoadState()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                string json = File.ReadAllText(_statePath);
                _state = JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
            }
            else
            {
                LoadLegacyTasks();
            }
        }
        catch
        {
            _state = new AppState();
        }

        _tasks.Clear();
        foreach (TaskItem task in _state.Tasks)
        {
            _tasks.Add(task);
        }

        _launchers.Clear();
        foreach (LauncherItem item in _state.Launchers)
        {
            _launchers.Add(item);
        }

        _launcherCategories.Clear();
        foreach (string category in _state.LauncherCategories)
        {
            EnsureLauncherCategory(category);
        }
        foreach (string category in _launchers.Select(item => LauncherCategory(item.Category)))
        {
            EnsureLauncherCategory(category);
        }

        _events.Clear();
        foreach (KeyValuePair<string, List<CalendarEvent>> pair in _state.Events)
        {
            _events[pair.Key] = pair.Value;
        }
    }

    private void LoadLegacyTasks()
    {
        if (!File.Exists(_legacyTasksPath))
        {
            _state.Tasks.Add(new TaskItem("First Memo", "Try The New Title/Content Memo Card."));
            _state.Tasks.Add(new TaskItem("Layout", "Move and resize each widget, then save the layout."));
            return;
        }

        try
        {
            string json = File.ReadAllText(_legacyTasksPath);
            List<TaskItem>? tasks = JsonSerializer.Deserialize<List<TaskItem>>(json);
            if (tasks is not null)
            {
                _state.Tasks = tasks;
            }
        }
        catch
        {
            _state.Tasks.Clear();
        }
    }

    private void ApplyStateToUi()
    {
        _isApplyingSettings = true;

        Opacity = _state.Window.GlobalOpacity;
        GlobalOpacitySlider.Value = _state.Window.GlobalOpacity;
        Topmost = _state.Window.Topmost;
        TopmostCheck.IsChecked = _state.Window.Topmost;
        _state.Window.StartWithWindows = IsStartupEnabled();
        StartupCheck.IsChecked = _state.Window.StartWithWindows;

        ApplyWidgetPlacement(ControlWidget, _state.Widgets.Control);
        ApplyWidgetPlacement(TodoWidget, _state.Widgets.Todo);
        ApplyWidgetPlacement(SystemWidget, _state.Widgets.System);
        ApplyWidgetPlacement(SoundWidget, _state.Widgets.Sound);
        ApplyWidgetPlacement(CalendarWidget, _state.Widgets.Calendar);
        ApplyWidgetPlacement(FocusWidget, _state.Widgets.Focus);
        ApplyWidgetPlacement(LauncherWidget, _state.Widgets.Launcher);
        ControlWidget.Visibility = _state.Window.ControlCenterVisible ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Controls.Panel.SetZIndex(ControlWidget, 500);

        TodoOpacitySlider.Value = _state.Widgets.Todo.Opacity;
        SystemOpacitySlider.Value = _state.Widgets.System.Opacity;
        SoundOpacitySlider.Value = _state.Widgets.Sound.Opacity;
        CalendarOpacitySlider.Value = _state.Widgets.Calendar.Opacity;
        FocusOpacitySlider.Value = _state.Widgets.Focus.Opacity;
        LauncherOpacitySlider.Value = _state.Widgets.Launcher.Opacity;
        WidgetOpacity_ValueChanged(this, new RoutedPropertyChangedEventArgs<double>(0, 0));

        ShowTodoCheck.IsChecked = _state.Widgets.Todo.IsVisible;
        ShowSystemCheck.IsChecked = _state.Widgets.System.IsVisible;
        ShowSoundCheck.IsChecked = _state.Widgets.Sound.IsVisible;
        ShowCalendarCheck.IsChecked = _state.Widgets.Calendar.IsVisible;
        ShowFocusCheck.IsChecked = _state.Widgets.Focus.IsVisible;
        ShowLauncherCheck.IsChecked = _state.Widgets.Launcher.IsVisible;
        ApplyWidgetVisibility();

        _selectedDate = _state.SelectedDate.Date;
        _calendarMonth = new DateTime(_selectedDate.Year, _selectedDate.Month, 1);

        _focusMinutes = Math.Max(1, _state.Focus.Minutes);
        FocusDurationButton.Content = DurationText(_focusMinutes);
        _focusMode = Enum.TryParse(_state.Focus.Mode, out FocusMode savedFocusMode)
            ? savedFocusMode
            : FocusMode.Timer;
        _focusRemainingSeconds = Math.Max(0, _state.Focus.RemainingSeconds);
        _focusElapsedSeconds = Math.Max(0, _state.Focus.ElapsedSeconds);
        _focusRunning = _state.Focus.IsRunning;
        _focusAlarmEnabled = _state.Focus.AlarmEnabled;
        FocusAlarmToggle.IsChecked = _focusAlarmEnabled;
        ApplyFocusModeUi();
        LauncherIconOnlyToggle.IsChecked = _state.LauncherIconOnly;

        _isApplyingSettings = false;
    }

    private void ApplyWidgetPlacement(FrameworkElement widget, WidgetPlacement placement)
    {
        Canvas.SetLeft(widget, placement.Left);
        Canvas.SetTop(widget, placement.Top);
        widget.Width = Math.Max(widget.MinWidth, placement.Width);
        widget.Height = Math.Max(widget.MinHeight, placement.Height);
    }

    private void ApplyWidgetVisibility()
    {
        TodoWidget.Visibility = ShowTodoCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SystemWidget.Visibility = ShowSystemCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SoundWidget.Visibility = ShowSoundCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CalendarWidget.Visibility = ShowCalendarCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FocusWidget.Visibility = ShowFocusCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LauncherWidget.Visibility = ShowLauncherCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveStateFromUi()
    {
        if (!_isLoaded && !_isApplyingSettings)
        {
            return;
        }

        _state.Tasks = _tasks.ToList();
        _state.Launchers = _launchers.ToList();
        _state.LauncherCategories = _launcherCategories.Select(LauncherCategory).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _state.LauncherIconOnly = LauncherIconOnlyToggle.IsChecked == true;
        _state.Events = _events.ToDictionary(pair => pair.Key, pair => pair.Value);
        _state.SelectedDate = _selectedDate.Date;
        _state.Window.Topmost = TopmostCheck.IsChecked == true;
        _state.Window.GlobalOpacity = GlobalOpacitySlider.Value;
        _state.Window.StartWithWindows = StartupCheck.IsChecked == true;
        _state.Window.ControlCenterVisible = ControlWidget.Visibility == Visibility.Visible;
        _state.Widgets.Control = WidgetPlacement.FromElement(ControlWidget);
        _state.Widgets.Todo = WidgetPlacement.FromElement(TodoWidget, TodoOpacitySlider.Value, ShowTodoCheck.IsChecked == true);
        _state.Widgets.System = WidgetPlacement.FromElement(SystemWidget, SystemOpacitySlider.Value, ShowSystemCheck.IsChecked == true);
        _state.Widgets.Sound = WidgetPlacement.FromElement(SoundWidget, SoundOpacitySlider.Value, ShowSoundCheck.IsChecked == true);
        _state.Widgets.Calendar = WidgetPlacement.FromElement(CalendarWidget, CalendarOpacitySlider.Value, ShowCalendarCheck.IsChecked == true);
        _state.Widgets.Focus = WidgetPlacement.FromElement(FocusWidget, FocusOpacitySlider.Value, ShowFocusCheck.IsChecked == true);
        _state.Widgets.Launcher = WidgetPlacement.FromElement(LauncherWidget, LauncherOpacitySlider.Value, ShowLauncherCheck.IsChecked == true);
        _state.Focus.Minutes = Math.Max(1, _focusMinutes);
        _state.Focus.Mode = _focusMode.ToString();
        _state.Focus.RemainingSeconds = _focusRemainingSeconds;
        _state.Focus.ElapsedSeconds = _focusElapsedSeconds;
        _state.Focus.IsRunning = _focusRunning;
        _state.Focus.AlarmEnabled = _focusAlarmEnabled;

        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        string json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_statePath, json);
    }

    private static string DateKey(DateTime date)
    {
        return date.ToString("yyyy-MM-dd");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

public sealed class AppState
{
    public List<TaskItem> Tasks { get; set; } = [];
    public List<LauncherItem> Launchers { get; set; } = [];
    public List<string> LauncherCategories { get; set; } = ["General"];
    public bool LauncherIconOnly { get; set; }
    public Dictionary<string, List<CalendarEvent>> Events { get; set; } = [];
    public WindowSettings Window { get; set; } = new();
    public WidgetSettings Widgets { get; set; } = new();
    public FocusSettings Focus { get; set; } = new();
    public DateTime SelectedDate { get; set; } = DateTime.Today;
}

public sealed class TaskItem
{
    public TaskItem()
    {
    }

    public TaskItem(string title, string content = "")
    {
        Title = title;
        Content = content;
    }

    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsExpanded { get; set; } = true;
    public bool IsSettingsExpanded { get; set; }
    public bool IsDone { get; set; }
    public ResetSettings Reset { get; set; } = new();
}

public sealed class LauncherItem
{
    public LauncherItem()
    {
    }

    public LauncherItem(string name, string target, string icon = "", string category = "")
    {
        Name = name;
        Target = target;
        Icon = icon;
        Category = category;
    }

    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}

public sealed class CalendarEvent
{
    public CalendarEvent()
    {
    }

    public CalendarEvent(string title, string location = "", string startTime = "", string endTime = "")
    {
        Title = title;
        Location = location;
        StartTime = startTime;
        EndTime = endTime;
    }

    public string Title { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string StartTime { get; set; } = string.Empty;
    public string EndTime { get; set; } = string.Empty;
}

public sealed class ResetSettings
{
    public bool Enabled { get; set; }
    public int Interval { get; set; } = 1;
    public ResetUnit Unit { get; set; } = ResetUnit.Day;
    public int DelayDays { get; set; }
    public int DelayHours { get; set; }
    public int DelayMinutes { get; set; }
    public ResetAnchor Anchor { get; set; } = ResetAnchor.DoneTime;
    public string ClockTime { get; set; } = "09:00";
    public int MonthlyDay { get; set; } = 1;
    public DateTime? CompletedAt { get; set; }
    public DateTime LastResetAt { get; set; } = DateTime.Now;

    public TimeSpan GetInterval()
    {
        int delayMinutes = DelayDays * 24 * 60 + DelayHours * 60 + DelayMinutes;
        if (delayMinutes > 0)
        {
            return TimeSpan.FromMinutes(delayMinutes);
        }

        int interval = Math.Max(1, Interval);
        return Unit switch
        {
            ResetUnit.Minute => TimeSpan.FromMinutes(interval),
            ResetUnit.Hour => TimeSpan.FromHours(interval),
            _ => TimeSpan.FromDays(interval)
        };
    }

    public DateTime? NextResetAt(bool isDone = true)
    {
        if (!Enabled || !isDone)
        {
            return null;
        }

        if (Anchor == ResetAnchor.ClockTime)
        {
            TimeSpan clockTime = ParseClockTime(ClockTime);
            DateTime todayReset = DateTime.Today.Add(clockTime);
            return LastResetAt >= todayReset ? todayReset.AddDays(1) : todayReset;
        }

        if (Anchor == ResetAnchor.MonthlyDay)
        {
            TimeSpan clockTime = ParseClockTime(ClockTime);
            DateTime thisMonthReset = MonthlyResetDate(DateTime.Today.Year, DateTime.Today.Month, clockTime);
            return LastResetAt >= thisMonthReset ? MonthlyResetDate(DateTime.Today.AddMonths(1).Year, DateTime.Today.AddMonths(1).Month, clockTime) : thisMonthReset;
        }

        DateTime basis = CompletedAt ?? LastResetAt;
        return basis.Add(GetInterval());
    }

    private DateTime MonthlyResetDate(int year, int month, TimeSpan clockTime)
    {
        int day = Math.Clamp(MonthlyDay, 1, DateTime.DaysInMonth(year, month));
        return new DateTime(year, month, day, clockTime.Hours, clockTime.Minutes, 0);
    }

    private static TimeSpan ParseClockTime(string value)
    {
        return TimeSpan.TryParse(value, out TimeSpan parsed)
            ? parsed
            : new TimeSpan(9, 0, 0);
    }
}

public enum ResetAnchor
{
    DoneTime,
    ClockTime,
    MonthlyDay
}

public enum ResetUnit
{
    Minute,
    Hour,
    Day
}

internal enum ResizeModeEdge
{
    None,
    Right,
    Bottom,
    Corner
}

public sealed class WindowSettings
{
    public bool Topmost { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool ControlCenterVisible { get; set; } = true;
    public double GlobalOpacity { get; set; } = 1;
}

public sealed class FocusSettings
{
    public string Mode { get; set; } = FocusMode.Timer.ToString();
    public int Minutes { get; set; } = 25;
    public int RemainingSeconds { get; set; } = 25 * 60;
    public int ElapsedSeconds { get; set; }
    public bool IsRunning { get; set; }
    public bool AlarmEnabled { get; set; } = true;
}

public enum FocusMode
{
    Timer,
    Stopwatch
}

public sealed class WidgetSettings
{
    public WidgetPlacement Control { get; set; } = new(24, 24, 330, 350, 0.94, true);
    public WidgetPlacement Todo { get; set; } = new(1300, 24, 460, 560, 0.94, true);
    public WidgetPlacement System { get; set; } = new(372, 24, 410, 600, 0.94, true);
    public WidgetPlacement Sound { get; set; } = new(800, 24, 390, 420, 0.94, true);
    public WidgetPlacement Calendar { get; set; } = new(800, 24, 430, 650, 0.94, true);
    public WidgetPlacement Focus { get; set; } = new(24, 342, 360, 390, 0.94, true);
    public WidgetPlacement Launcher { get; set; } = new(24, 620, 400, 340, 0.94, true);
}

public sealed class WidgetPlacement
{
    public WidgetPlacement()
    {
    }

    public WidgetPlacement(double left, double top, double width, double height, double opacity = 0.94, bool isVisible = true)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
        Opacity = opacity;
        IsVisible = isVisible;
    }

    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 300;
    public double Opacity { get; set; } = 0.94;
    public bool IsVisible { get; set; } = true;

    public static WidgetPlacement FromElement(FrameworkElement element, double opacity = 0.94, bool isVisible = true)
    {
        double left = Canvas.GetLeft(element);
        double top = Canvas.GetTop(element);
        return new WidgetPlacement(
            double.IsNaN(left) ? 0 : left,
            double.IsNaN(top) ? 0 : top,
            Math.Max(element.MinWidth, element.ActualWidth > 0 ? element.ActualWidth : element.Width),
            Math.Max(element.MinHeight, element.ActualHeight > 0 ? element.ActualHeight : element.Height),
            Math.Clamp(opacity, 0.3, 1),
            isVisible);
    }
}

internal sealed class ResourceSampler
{
    private CpuTimes? _previousCpu;
    private NetworkBytes? _previousNetwork;
    private readonly GpuSampler _gpuSampler = new();
    private DateTime _previousNetworkAt = DateTime.UtcNow;

    public ResourceSnapshot Sample()
    {
        double cpuPercent = GetCpuPercent();
        MemoryStatus memory = GetMemoryStatus();
        GpuStatus gpu = _gpuSampler.Sample();
        (double downloadKbps, double uploadKbps) = GetNetworkKbps();

        return new ResourceSnapshot(
            cpuPercent,
            memory.UsedPercent,
            memory.UsedGb,
            memory.TotalGb,
            gpu.Percent,
            gpu.IsAvailable,
            downloadKbps,
            uploadKbps);
    }

    private double GetCpuPercent()
    {
        CpuTimes current = CpuTimes.Read();
        if (_previousCpu is null)
        {
            _previousCpu = current;
            return 0;
        }

        CpuTimes previous = _previousCpu.Value;
        _previousCpu = current;

        ulong idle = current.Idle - previous.Idle;
        ulong total = current.Total - previous.Total;
        if (total == 0)
        {
            return 0;
        }

        double used = 100d * (total - idle) / total;
        return Math.Clamp(used, 0, 100);
    }

    private static MemoryStatus GetMemoryStatus()
    {
        MemoryStatusEx status = new()
        {
            dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (!GlobalMemoryStatusEx(ref status))
        {
            return new MemoryStatus(0, 0, 0);
        }

        double totalGb = status.ullTotalPhys / 1024d / 1024d / 1024d;
        double availableGb = status.ullAvailPhys / 1024d / 1024d / 1024d;
        double usedGb = totalGb - availableGb;
        return new MemoryStatus(status.dwMemoryLoad, usedGb, totalGb);
    }

    private (double DownloadKbps, double UploadKbps) GetNetworkKbps()
    {
        NetworkBytes current = NetworkBytes.Read();
        DateTime now = DateTime.UtcNow;
        if (_previousNetwork is null)
        {
            _previousNetwork = current;
            _previousNetworkAt = now;
            return (0, 0);
        }

        double seconds = Math.Max(0.2, (now - _previousNetworkAt).TotalSeconds);
        NetworkBytes previous = _previousNetwork.Value;
        _previousNetwork = current;
        _previousNetworkAt = now;

        double received = Math.Max(0, current.Received - previous.Received) / 1024d / seconds;
        double sent = Math.Max(0, current.Sent - previous.Sent) / 1024d / seconds;
        return (received, sent);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}

internal readonly record struct ResourceSnapshot(
    double CpuPercent,
    double MemoryPercent,
    double MemoryUsedGb,
    double MemoryTotalGb,
    double GpuPercent,
    bool GpuAvailable,
    double DownloadKbps,
    double UploadKbps);

internal readonly record struct MemoryStatus(double UsedPercent, double UsedGb, double TotalGb);
internal readonly record struct GpuStatus(double Percent, bool IsAvailable);
internal sealed class AudioMixerService
{
    private static readonly Guid IAudioEndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid IAudioSessionManager2Id = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid EventContext = Guid.Empty;
    private const uint DeviceStateActive = 0x00000001;
    private const uint DeviceStateAll = 0x0000000F;
    private readonly Dictionary<string, ISimpleAudioVolume> _sessionVolumes = new(StringComparer.OrdinalIgnoreCase);
    private IAudioEndpointVolume? _masterEndpoint;

    public IReadOnlyList<AudioDeviceInfo> PlaybackDevices { get; private set; } = [];
    public IReadOnlyList<AudioDeviceInfo> RecordingDevices { get; private set; } = [];
    public string DefaultPlaybackDeviceId { get; private set; } = string.Empty;
    public string DefaultRecordingDeviceId { get; private set; } = string.Empty;

    public AudioMixerSnapshot GetSnapshot()
    {
        PlaybackDevices = TryGetDevices(EDataFlow.Render);
        RecordingDevices = TryGetDevices(EDataFlow.Capture);
        DefaultPlaybackDeviceId = TryGetDefaultDeviceId(EDataFlow.Render);
        DefaultRecordingDeviceId = TryGetDefaultDeviceId(EDataFlow.Capture);

        IMMDevice device = GetDefaultDevice(EDataFlow.Render);
        IAudioEndpointVolume endpoint = Activate<IAudioEndpointVolume>(device, IAudioEndpointVolumeId);
        _masterEndpoint = endpoint;
        endpoint.GetMasterVolumeLevelScalar(out float masterVolume);
        endpoint.GetMute(out bool masterMuted);

        List<AudioSessionInfo> sessions = GetAudioSessions(device);

        return new AudioMixerSnapshot(
            Math.Clamp(masterVolume, 0, 1),
            masterMuted,
            DefaultPlaybackDeviceId,
            DefaultRecordingDeviceId,
            PlaybackDevices,
            RecordingDevices,
            sessions.OrderBy(session => session.Name).ToList());
    }

    private List<AudioSessionInfo> GetAudioSessions(IMMDevice device)
    {
        List<AudioSessionInfo> sessions = [];
        _sessionVolumes.Clear();
        try
        {
            IAudioSessionManager2 manager = Activate<IAudioSessionManager2>(device, IAudioSessionManager2Id);
            manager.GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
            enumerator.GetCount(out int count);

            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    enumerator.GetSession(i, out IAudioSessionControl control);
                    IAudioSessionControl2 control2 = (IAudioSessionControl2)control;
                    ISimpleAudioVolume volume = (ISimpleAudioVolume)control;
                    control2.GetSessionInstanceIdentifier(out string id);
                    control2.GetProcessId(out uint processId);
                    volume.GetMasterVolume(out float sessionVolume);
                    volume.GetMute(out bool muted);

                    if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                    {
                        continue;
                    }

                    string name = GetSessionName(control2, processId);
                    string outputDeviceId = processId > 0 ? GetApplicationOutputDevice(processId) : string.Empty;
                    _sessionVolumes[id] = volume;
                    sessions.Add(new AudioSessionInfo(id, name, processId, outputDeviceId, Math.Clamp(sessionVolume, 0, 1), muted));
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return sessions;
    }

    public void SetMasterVolume(float volume)
    {
        if (TryUseMasterEndpoint(endpoint =>
        {
            Guid context = EventContext;
            endpoint.SetMasterVolumeLevelScalar(Math.Clamp(volume, 0, 1), ref context);
        }))
        {
            return;
        }

        IMMDevice device = GetDefaultDevice(EDataFlow.Render);
        IAudioEndpointVolume endpoint = Activate<IAudioEndpointVolume>(device, IAudioEndpointVolumeId);
        _masterEndpoint = endpoint;
        Guid context = EventContext;
        endpoint.SetMasterVolumeLevelScalar(Math.Clamp(volume, 0, 1), ref context);
    }

    public void SetMasterMute(bool isMuted)
    {
        if (TryUseMasterEndpoint(endpoint =>
        {
            Guid context = EventContext;
            endpoint.SetMute(isMuted, ref context);
        }))
        {
            return;
        }

        IMMDevice device = GetDefaultDevice(EDataFlow.Render);
        IAudioEndpointVolume endpoint = Activate<IAudioEndpointVolume>(device, IAudioEndpointVolumeId);
        _masterEndpoint = endpoint;
        Guid context = EventContext;
        endpoint.SetMute(isMuted, ref context);
    }

    public void SetSessionVolume(string sessionId, float volume)
    {
        if (TryUseCachedSession(sessionId, simpleVolume =>
        {
            Guid context = EventContext;
            simpleVolume.SetMasterVolume(Math.Clamp(volume, 0, 1), ref context);
        }))
        {
            return;
        }

        WithSession(sessionId, simpleVolume =>
        {
            Guid context = EventContext;
            simpleVolume.SetMasterVolume(Math.Clamp(volume, 0, 1), ref context);
        });
    }

    public void SetSessionMute(string sessionId, bool isMuted)
    {
        if (TryUseCachedSession(sessionId, simpleVolume =>
        {
            Guid context = EventContext;
            simpleVolume.SetMute(isMuted, ref context);
        }))
        {
            return;
        }

        WithSession(sessionId, simpleVolume =>
        {
            Guid context = EventContext;
            simpleVolume.SetMute(isMuted, ref context);
        });
    }

    private bool TryUseMasterEndpoint(Action<IAudioEndpointVolume> action)
    {
        if (_masterEndpoint is null)
        {
            return false;
        }

        try
        {
            action(_masterEndpoint);
            return true;
        }
        catch
        {
            _masterEndpoint = null;
            return false;
        }
    }

    private bool TryUseCachedSession(string sessionId, Action<ISimpleAudioVolume> action)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessionVolumes.TryGetValue(sessionId, out ISimpleAudioVolume? simpleVolume))
        {
            return false;
        }

        try
        {
            action(simpleVolume);
            return true;
        }
        catch
        {
            _sessionVolumes.Remove(sessionId);
            return false;
        }
    }

    private void WithSession(string sessionId, Action<ISimpleAudioVolume> action)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        IMMDevice device = GetDefaultDevice(EDataFlow.Render);
        IAudioSessionManager2 manager = Activate<IAudioSessionManager2>(device, IAudioSessionManager2Id);
        manager.GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        enumerator.GetCount(out int count);
        for (int i = 0; i < count; i++)
        {
            enumerator.GetSession(i, out IAudioSessionControl control);
            IAudioSessionControl2 control2 = (IAudioSessionControl2)control;
            control2.GetSessionInstanceIdentifier(out string currentId);
            if (sessionId.Equals(currentId, StringComparison.OrdinalIgnoreCase))
            {
                ISimpleAudioVolume simpleVolume = (ISimpleAudioVolume)control;
                _sessionVolumes[sessionId] = simpleVolume;
                action(simpleVolume);
                return;
            }
        }
    }

    private static string GetSessionName(IAudioSessionControl2 control, uint processId)
    {
        try
        {
            control.GetDisplayName(out string displayName);
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }
        }
        catch
        {
        }

        try
        {
            if (processId > 0)
            {
                using Process process = Process.GetProcessById((int)processId);
                return string.IsNullOrWhiteSpace(process.MainWindowTitle)
                    ? process.ProcessName
                    : process.MainWindowTitle;
            }
        }
        catch
        {
        }

        return processId == 0 ? "System Sounds" : $"Process {processId}";
    }

    public void SetDefaultDevice(string deviceId, EDataFlow flow)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        try
        {
            IPolicyConfig policyConfig = (IPolicyConfig)(object)new PolicyConfigClient();
            policyConfig.SetDefaultEndpoint(deviceId, ERole.Console);
            policyConfig.SetDefaultEndpoint(deviceId, ERole.Multimedia);
            policyConfig.SetDefaultEndpoint(deviceId, ERole.Communications);
        }
        catch
        {
        }
    }

    public void SetApplicationOutputDevice(uint processId, string deviceId)
    {
        if (processId == 0 || string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        try
        {
            IAudioPolicyConfigFactory factory = (IAudioPolicyConfigFactory)(object)new AudioPolicyConfigFactoryClient();
            factory.SetPersistedDefaultAudioEndpoint(processId, EDataFlow.Render, ERole.Multimedia, deviceId);
        }
        catch
        {
        }
    }

    private static string GetApplicationOutputDevice(uint processId)
    {
        if (processId == 0)
        {
            return string.Empty;
        }

        try
        {
            IAudioPolicyConfigFactory factory = (IAudioPolicyConfigFactory)(object)new AudioPolicyConfigFactoryClient();
            factory.GetPersistedDefaultAudioEndpoint(processId, EDataFlow.Render, ERole.Multimedia, out string deviceId);
            return deviceId ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static List<AudioDeviceInfo> GetDevices(EDataFlow flow)
    {
        List<AudioDeviceInfo> devices = [];
        try
        {
            devices.AddRange(EnumerateDevices(flow, DeviceStateActive));
        }
        catch
        {
        }

        try
        {
            devices.AddRange(EnumerateDevices(flow, DeviceStateAll));
        }
        catch
        {
        }

        devices.AddRange(GetRegistryAudioDevices(flow));

        AudioDeviceInfo? defaultDevice = TryGetDefaultDeviceInfo(flow);
        if (defaultDevice is not null)
        {
            devices.Insert(0, defaultDevice);
        }

        return DeduplicateAudioDevices(devices).OrderBy(device => device.Name).ToList();
    }

    private static List<AudioDeviceInfo> DeduplicateAudioDevices(IEnumerable<AudioDeviceInfo> devices)
    {
        Dictionary<string, AudioDeviceInfo> unique = new(StringComparer.OrdinalIgnoreCase);
        foreach (AudioDeviceInfo device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.Id))
            {
                continue;
            }

            if (!unique.ContainsKey(device.Id)
                || unique[device.Id].Name.Equals("Audio Device", StringComparison.OrdinalIgnoreCase))
            {
                unique[device.Id] = device;
            }
        }

        return unique.Values.ToList();
    }

    private static List<AudioDeviceInfo> EnumerateDevices(EDataFlow flow, uint stateMask)
    {
        IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
        enumerator.EnumAudioEndpoints(flow, stateMask, out IMMDeviceCollection collection);
        collection.GetCount(out uint count);

        List<AudioDeviceInfo> devices = [];
        for (uint i = 0; i < count; i++)
        {
            collection.Item(i, out IMMDevice device);
            device.GetId(out string id);
            devices.Add(new AudioDeviceInfo(id, GetDeviceFriendlyName(device)));
        }

        return devices;
    }

    private static List<AudioDeviceInfo> TryGetDevices(EDataFlow flow)
    {
        try
        {
            return GetDevices(flow);
        }
        catch
        {
            List<AudioDeviceInfo> devices = GetRegistryAudioDevices(flow);
            AudioDeviceInfo? defaultDevice = TryGetDefaultDeviceInfo(flow);
            if (defaultDevice is not null)
            {
                devices.Insert(0, defaultDevice);
            }

            return DeduplicateAudioDevices(devices).OrderBy(device => device.Name).ToList();
        }
    }

    private static List<AudioDeviceInfo> GetRegistryAudioDevices(EDataFlow flow)
    {
        string? kind = flow switch
        {
            EDataFlow.Render => "Render",
            EDataFlow.Capture => "Capture",
            _ => null
        };
        if (kind is null)
        {
            return [];
        }

        string endpointPrefix = flow == EDataFlow.Render
            ? "{0.0.0.00000000}."
            : "{0.0.1.00000000}.";
        string path = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\{kind}";
        using RegistryKey? audioKey = Registry.LocalMachine.OpenSubKey(path);
        if (audioKey is null)
        {
            return [];
        }

        List<AudioDeviceInfo> devices = [];
        foreach (string deviceKeyName in audioKey.GetSubKeyNames())
        {
            if (!Guid.TryParse(deviceKeyName.Trim('{', '}'), out _))
            {
                continue;
            }

            using RegistryKey? deviceKey = audioKey.OpenSubKey(deviceKeyName);
            using RegistryKey? propertiesKey = deviceKey?.OpenSubKey("Properties");
            string name = GetRegistryAudioDeviceName(propertiesKey, deviceKeyName);
            devices.Add(new AudioDeviceInfo(endpointPrefix + deviceKeyName, name));
        }

        return devices;
    }

    private static string GetRegistryAudioDeviceName(RegistryKey? propertiesKey, string fallback)
    {
        if (propertiesKey is null)
        {
            return fallback;
        }

        string description = GetRegistryString(propertiesKey, "{a45c254e-df1c-4efd-8020-67d146a850e0},2");
        string friendlyName = GetRegistryString(propertiesKey, "{a45c254e-df1c-4efd-8020-67d146a850e0},14");
        string interfaceName = GetRegistryString(propertiesKey, "{b3f8fa53-0004-438e-9003-51a46e139bfc},6");

        string primary = string.IsNullOrWhiteSpace(friendlyName) ? description : friendlyName;
        if (string.IsNullOrWhiteSpace(primary))
        {
            primary = interfaceName;
        }

        if (!string.IsNullOrWhiteSpace(primary)
            && !string.IsNullOrWhiteSpace(interfaceName)
            && !primary.Equals(interfaceName, StringComparison.OrdinalIgnoreCase))
        {
            return $"{primary} - {interfaceName}";
        }

        return string.IsNullOrWhiteSpace(primary) ? fallback : primary;
    }

    private static string GetRegistryString(RegistryKey key, string valueName)
    {
        return key.GetValue(valueName) is string value ? value : string.Empty;
    }

    private static string GetDefaultDeviceId(EDataFlow flow)
    {
        IMMDevice device = GetDefaultDevice(flow);
        device.GetId(out string id);
        return id;
    }

    private static string TryGetDefaultDeviceId(EDataFlow flow)
    {
        try
        {
            return GetDefaultDeviceId(flow);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static AudioDeviceInfo? TryGetDefaultDeviceInfo(EDataFlow flow)
    {
        try
        {
            IMMDevice device = GetDefaultDevice(flow);
            device.GetId(out string id);
            return string.IsNullOrWhiteSpace(id)
                ? null
                : new AudioDeviceInfo(id, GetDeviceFriendlyName(device));
        }
        catch
        {
            return null;
        }
    }

    private static IMMDevice GetDefaultDevice(EDataFlow flow)
    {
        IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
        enumerator.GetDefaultAudioEndpoint(flow, ERole.Multimedia, out IMMDevice device);
        return device;
    }

    private static string GetDeviceFriendlyName(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(StorageAccessMode.Read, out IPropertyStore store);
            PropertyKey key = PropertyKeys.DeviceFriendlyName;
            store.GetValue(ref key, out PropVariant value);
            try
            {
                return value.Value ?? "Audio Device";
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        catch
        {
            return "Audio Device";
        }
    }

    private static T Activate<T>(IMMDevice device, Guid interfaceId)
    {
        device.Activate(ref interfaceId, ClsCtx.All, IntPtr.Zero, out object instance);
        return (T)instance;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant propVariant);
}

internal sealed record AudioMixerSnapshot(
    float MasterVolume,
    bool MasterMuted,
    string DefaultPlaybackDeviceId,
    string DefaultRecordingDeviceId,
    IReadOnlyList<AudioDeviceInfo> PlaybackDevices,
    IReadOnlyList<AudioDeviceInfo> RecordingDevices,
    IReadOnlyList<AudioSessionInfo> Sessions);
internal sealed record AudioSessionInfo(string Id, string Name, uint ProcessId, string OutputDeviceId, float Volume, bool IsMuted);
internal sealed record AudioDeviceInfo(string Id, string Name);

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class MMDeviceEnumerator
{
}

internal enum EDataFlow
{
    Render,
    Capture,
    All
}

internal enum ERole
{
    Console,
    Multimedia,
    Communications
}

[Flags]
internal enum ClsCtx
{
    All = 23
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
    void GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-C0F7E9B7332D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    void GetCount(out uint deviceCount);
    void Item(uint deviceIndex, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    void Activate(ref Guid iid, ClsCtx dwClsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    void OpenPropertyStore(StorageAccessMode accessMode, out IPropertyStore properties);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out uint state);
}

internal enum StorageAccessMode
{
    Read = 0,
    Write = 1,
    ReadWrite = 2
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    void GetCount(out uint propertyCount);
    void GetAt(uint propertyIndex, out PropertyKey key);
    void GetValue(ref PropertyKey key, out PropVariant value);
    void SetValue(ref PropertyKey key, ref PropVariant value);
    void Commit();
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;

    public PropertyKey(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

internal static class PropertyKeys
{
    public static readonly PropertyKey DeviceFriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    private ushort valueType;
    private ushort reserved1;
    private ushort reserved2;
    private ushort reserved3;
    private IntPtr pointerValue;
    private int intValue;

    public string? Value => valueType == 31 && pointerValue != IntPtr.Zero
        ? Marshal.PtrToStringUni(pointerValue)
        : null;
}

[ComImport]
[Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
internal sealed class PolicyConfigClient
{
}

[ComImport]
[Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    void GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, out IntPtr mixFormat);
    void GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool defaultFormat, out IntPtr deviceFormat);
    void SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr endpointFormat, IntPtr mixFormat);
    void GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool defaultPeriod, out long defaultPeriodValue, out long minimumPeriodValue);
    void SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceName, ref long period);
    void GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceName, out IntPtr mode);
    void SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr mode);
    void GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceName, ref PropertyKey key, out PropVariant value);
    void SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceName, ref PropertyKey key, ref PropVariant value);
    void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceName, ERole role);
    void SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool isVisible);
}

[ComImport]
[Guid("2A59116E-2434-11E4-A311-40F2E9B84D6B")]
internal sealed class AudioPolicyConfigFactoryClient
{
}

[ComImport]
[Guid("AB3D4648-E242-459F-B02F-541C70306324")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioPolicyConfigFactory
{
    void SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    void GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] out string deviceId);
    void ClearAllPersistedApplicationDefaultEndpoints();
    void ClearPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role);
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IntPtr client);
    void UnregisterControlChangeNotify(IntPtr client);
    void GetChannelCount(out uint channelCount);
    void SetMasterVolumeLevel(float level, ref Guid eventContext);
    void SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
    void GetMasterVolumeLevel(out float level);
    void GetMasterVolumeLevelScalar(out float level);
    void SetChannelVolumeLevel(uint channelNumber, float level, ref Guid eventContext);
    void SetChannelVolumeLevelScalar(uint channelNumber, float level, ref Guid eventContext);
    void GetChannelVolumeLevel(uint channelNumber, out float level);
    void GetChannelVolumeLevelScalar(uint channelNumber, out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);
}

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    void GetAudioSessionControl(ref Guid audioSessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);
    void GetSimpleAudioVolume(ref Guid audioSessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);
    void GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    void GetCount(out int sessionCount);
    void GetSession(int sessionCount, out IAudioSessionControl session);
}

[ComImport]
[Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
}

[ComImport]
[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    void GetState(out int state);
    void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);
    void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);
    void GetGroupingParam(out Guid groupingId);
    void SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
    void RegisterAudioSessionNotification(IntPtr client);
    void UnregisterAudioSessionNotification(IntPtr client);
    void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
    void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
    void GetProcessId(out uint processId);
}

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    void SetMasterVolume(float level, ref Guid eventContext);
    void GetMasterVolume(out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);
}

internal sealed class GpuSampler : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint ErrorSuccess = 0;
    private const uint PdhMoreData = 0x800007D2;

    private IntPtr _query;
    private IntPtr _counter;
    private bool _isAvailable;
    private bool _disposed;

    public GpuSampler()
    {
        Initialize();
    }

    public GpuStatus Sample()
    {
        if (!_isAvailable || _disposed)
        {
            return new GpuStatus(0, false);
        }

        if (PdhCollectQueryData(_query) != ErrorSuccess)
        {
            return new GpuStatus(0, false);
        }

        uint bufferSize = 0;
        uint itemCount = 0;
        uint status = PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref bufferSize, ref itemCount, IntPtr.Zero);
        if (status != PdhMoreData || bufferSize == 0 || itemCount == 0)
        {
            return new GpuStatus(0, false);
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref bufferSize, ref itemCount, buffer);
            if (status != ErrorSuccess)
            {
                return new GpuStatus(0, false);
            }

            double threeDTotal = 0;
            double allEngineTotal = 0;
            int itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();

            for (int i = 0; i < itemCount; i++)
            {
                IntPtr itemPointer = IntPtr.Add(buffer, i * itemSize);
                PdhFmtCounterValueItem item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(itemPointer);
                double value = Math.Max(0, item.FmtValue.DoubleValue);
                allEngineTotal += value;

                if (item.Name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                {
                    threeDTotal += value;
                }
            }

            double percent = threeDTotal > 0 ? threeDTotal : allEngineTotal;
            return new GpuStatus(Math.Clamp(percent, 0, 100), true);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
        }

        _disposed = true;
    }

    private void Initialize()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != ErrorSuccess)
        {
            return;
        }

        if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != ErrorSuccess)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
            return;
        }

        PdhCollectQueryData(_query);
        _isAvailable = true;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        ref uint itemCount,
        IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct PdhFmtCounterValueItem
{
    [MarshalAs(UnmanagedType.LPWStr)]
    public string Name;
    public PdhFmtCounterValue FmtValue;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PdhFmtCounterValue
{
    public uint CStatus;
    public double DoubleValue;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal struct MemoryStatusEx
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;
}

internal readonly record struct NetworkBytes(long Received, long Sent)
{
    public static NetworkBytes Read()
    {
        long received = 0;
        long sent = 0;

        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            IPInterfaceStatistics statistics = networkInterface.GetIPStatistics();
            received += statistics.BytesReceived;
            sent += statistics.BytesSent;
        }

        return new NetworkBytes(received, sent);
    }
}

internal readonly record struct CpuTimes(ulong Idle, ulong Total)
{
    public static CpuTimes Read()
    {
        if (!GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime))
        {
            return new CpuTimes(0, 0);
        }

        ulong idle = idleTime.ToUInt64();
        ulong kernel = kernelTime.ToUInt64();
        ulong user = userTime.ToUInt64();
        return new CpuTimes(idle, kernel + user);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime lpIdleTime, out FileTime lpKernelTime, out FileTime lpUserTime);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct FileTime
{
    private readonly uint _lowDateTime;
    private readonly uint _highDateTime;

    public ulong ToUInt64()
    {
        return ((ulong)_highDateTime << 32) | _lowDateTime;
    }
}
