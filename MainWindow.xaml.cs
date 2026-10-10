using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using MultiAI.Controls;
using MultiAI.Helpers;
using MultiAI.Models;

namespace MultiAI;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly List<SessionControl> _sessionControls = new();
    private int _currentRows = 2;
    private int _currentCols = 3;
    private int _rebuildGeneration;
    private bool _closePending;
    private bool _closeConfirmed;

    // Tabs view state
    private bool _tabsMode;
    private int _selectedTabIndex;
    private Border? _tabStripPanel;
    private readonly List<Button> _tabButtons = new();

    private static readonly System.Windows.Media.Brush TabSelectedBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2d, 0x5d, 0x8d));
    private static readonly System.Windows.Media.Brush TabIdleBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2d, 0x2d, 0x2d));

    public MainWindow()
    {
        InitializeComponent();

        // Handle Enter key in dimension text boxes
        RowsBox.KeyDown += DimensionBox_KeyDown;
        ColsBox.KeyDown += DimensionBox_KeyDown;

        // Wire up the toolbar agent combo to the shared agent list
        ToolbarAgentCombo.ItemsSource = AgentManager.Agents;
        ToolbarAgentCombo.Text = "opencode";

        // Tiles/Tabs switch
        ViewModeSelector.ViewModeChanged += OnViewModeChanged;

        // Try to restore last saved layout, otherwise use default
        if (!TryLoadLayoutOnStartup())
        {
            // No sessions are running yet, so the rebuild completes synchronously.
            _ = RebuildGridAsync(2, 3);
        }

        // Handle window closing
        Closing += MainWindow_Closing;
    }

    /// <summary>
    /// Hooks the window procedure so maximizing the borderless window
    /// respects the taskbar (work area) instead of covering the full screen.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowProc);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_GETMINMAXINFO)
        {
            NativeMethods.ApplyWorkAreaToMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Rebuilds the grid with the specified dimensions, populating each cell with a SessionControl.
    /// Running sessions are stopped before the old grid is torn down.
    /// </summary>
    private async Task RebuildGridAsync(int rows, int cols)
    {
        int generation = ++_rebuildGeneration;

        await StopAllSessionsAsync();

        // A newer rebuild/load started while we were stopping sessions; let it win.
        if (generation != _rebuildGeneration)
            return;

        ClearGrid();

        _currentRows = rows;
        _currentCols = cols;

        // Create a SessionControl for each cell
        int total = rows * cols;
        for (int i = 0; i < total; i++)
        {
            int row = i / cols;
            int col = i % cols;

            var session = new SessionInfo
            {
                Name = $"Session {row + 1}-{col + 1}",
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            AddSessionControl(session, row, col);
        }

        RefreshViewLayout();
        UpdateSessionCount();
        UpdateStatusBar($"Grid: {rows}×{cols} ({total} sessions)");
    }

    /// <summary>
    /// Removes all session controls from the grid and unsubscribes their handlers.
    /// </summary>
    private void ClearGrid()
    {
        foreach (var ctrl in _sessionControls)
        {
            ctrl.Session.PropertyChanged -= OnSessionStatusChanged;
        }
        _sessionControls.Clear();

        _tabStripPanel = null;
        _tabButtons.Clear();

        SessionsGrid.Children.Clear();
        SessionsGrid.RowDefinitions.Clear();
        SessionsGrid.ColumnDefinitions.Clear();
    }

    /// <summary>
    /// Creates a SessionControl for the session and places it in the given grid cell.
    /// </summary>
    private void AddSessionControl(SessionInfo session, int row, int col)
    {
        session.PropertyChanged += OnSessionStatusChanged;

        var control = new SessionControl(session) { Margin = new Thickness(2) };
        Grid.SetRow(control, row);
        Grid.SetColumn(control, col);

        _sessionControls.Add(control);
        SessionsGrid.Children.Add(control);
    }

    // ——— Tiles / Tabs view switching ———
    //
    // SessionControls are never moved between containers or removed from
    // SessionsGrid: doing that would unload their ConsoleHost (an HwndHost) and
    // destroy + rebuild the hosted console window, visibly detaching every shell.
    // Instead both modes only change the grid definitions and per-control
    // visibility — a Hidden HwndHost simply hides its HWND while the console
    // process keeps running and buffering output.

    /// <summary>
    /// Re-applies the layout for the current view mode (tiles grid or tabs).
    /// </summary>
    private void RefreshViewLayout()
    {
        if (_tabsMode)
            ApplyTabsLayout();
        else
            ApplyTilesLayout();
    }

    /// <summary>
    /// Lays the sessions out as a rows×cols grid, all visible.
    /// </summary>
    private void ApplyTilesLayout()
    {
        RemoveTabStrip();

        SessionsGrid.RowDefinitions.Clear();
        SessionsGrid.ColumnDefinitions.Clear();

        for (int r = 0; r < _currentRows; r++)
            SessionsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        for (int c = 0; c < _currentCols; c++)
            SessionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < _sessionControls.Count; i++)
        {
            var ctrl = _sessionControls[i];
            Grid.SetRow(ctrl, i / _currentCols);
            Grid.SetColumn(ctrl, i % _currentCols);
            System.Windows.Controls.Panel.SetZIndex(ctrl, 0);
            ctrl.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Lays the sessions out as a single visible shell on the left with a tab
    /// strip listing every session on the right.
    /// </summary>
    private void ApplyTabsLayout()
    {
        SessionsGrid.RowDefinitions.Clear();
        SessionsGrid.ColumnDefinitions.Clear();
        SessionsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        SessionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SessionsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });

        if (_sessionControls.Count == 0)
        {
            RemoveTabStrip();
            return;
        }

        _selectedTabIndex = Math.Clamp(_selectedTabIndex, 0, _sessionControls.Count - 1);

        // Every control shares the content cell; only the selected one is visible.
        for (int i = 0; i < _sessionControls.Count; i++)
        {
            var ctrl = _sessionControls[i];
            Grid.SetRow(ctrl, 0);
            Grid.SetColumn(ctrl, 0);
        }

        BuildTabStrip();
    }

    /// <summary>
    /// Builds the right-hand tab strip (one tab per session, showing its name
    /// and status) and applies the current tab selection.
    /// </summary>
    private void BuildTabStrip()
    {
        RemoveTabStrip();

        _tabStripPanel = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x25, 0x25, 0x25)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1, 0, 0, 0),
        };
        Grid.SetRow(_tabStripPanel, 0);
        Grid.SetColumn(_tabStripPanel, 1);

        var tabsHost = new StackPanel();

        for (int i = 0; i < _sessionControls.Count; i++)
        {
            var session = _sessionControls[i].Session;
            int index = i;

            var dot = new Border { Style = (Style)FindResource("SessionStatusDot") };

            var label = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 8, 0)
            };
            label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(SessionInfo.Name)));

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(dot);
            content.Children.Add(label);

            var tab = new Button
            {
                Content = content,
                DataContext = session,
                Style = (Style)FindResource("TabStripItemStyle"),
                ToolTip = session.WorkingDirectory
            };
            tab.Click += (_, _) => SelectTab(index);

            _tabButtons.Add(tab);
            tabsHost.Children.Add(tab);
        }

        _tabStripPanel.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = tabsHost
        };
        SessionsGrid.Children.Add(_tabStripPanel);

        UpdateTabSelection();
    }

    private void RemoveTabStrip()
    {
        if (_tabStripPanel != null)
            SessionsGrid.Children.Remove(_tabStripPanel);
        _tabStripPanel = null;
        _tabButtons.Clear();
    }

    /// <summary>
    /// Selects the session at the given index in Tabs view: shows its shell,
    /// hides the others, highlights the tab.
    /// </summary>
    private void SelectTab(int index)
    {
        if (!_tabsMode || _sessionControls.Count == 0) return;
        _selectedTabIndex = Math.Clamp(index, 0, _sessionControls.Count - 1);
        UpdateTabSelection();
        UpdateStatusBar($"Tabs: \"{_sessionControls[_selectedTabIndex].Session.Name}\" " +
                        $"({_selectedTabIndex + 1}/{_sessionControls.Count})");
    }

    /// <summary>
    /// Applies the selected tab's state: the selected SessionControl becomes
    /// visible at full size (like a 1×1 tile), the rest stay hidden but alive.
    /// </summary>
    private void UpdateTabSelection()
    {
        for (int i = 0; i < _sessionControls.Count; i++)
        {
            bool selected = i == _selectedTabIndex;
            var ctrl = _sessionControls[i];

            System.Windows.Controls.Panel.SetZIndex(ctrl, selected ? 1 : 0);
            ctrl.Visibility = selected ? Visibility.Visible : Visibility.Hidden;

            if (i < _tabButtons.Count)
                _tabButtons[i].Background = selected ? TabSelectedBrush : TabIdleBrush;
        }
    }

    /// <summary>
    /// Handles the Tiles/Tabs toggle.
    /// </summary>
    private void OnViewModeChanged(bool tabs)
    {
        if (_tabsMode == tabs) return;
        _tabsMode = tabs;

        RefreshViewLayout();

        if (tabs && _sessionControls.Count > 0)
            UpdateStatusBar($"Tabs: \"{_sessionControls[_selectedTabIndex].Session.Name}\" " +
                            $"({_selectedTabIndex + 1}/{_sessionControls.Count})");
        else if (!tabs)
            UpdateStatusBar($"Grid: {_currentRows}×{_currentCols} ({_sessionControls.Count} sessions)");
    }

    /// <summary>
    /// Clears the grid and rebuilds it from saved layout data.
    /// </summary>
    private void ApplyLayout(LayoutData data)
    {
        int rows = Math.Max(1, data.Rows);
        int cols = Math.Max(1, data.Columns);

        ClearGrid();

        _currentRows = rows;
        _currentCols = cols;
        RowsBox.Text = rows.ToString();
        ColsBox.Text = cols.ToString();

        // A hand-edited or stale layout may hold more sessions than the grid has
        // cells for; extra ones would be placed in non-existent rows. Ignore them.
        int cellCount = Math.Min(data.Sessions.Count, rows * cols);
        for (int i = 0; i < cellCount; i++)
        {
            int row = i / cols;
            int col = i % cols;
            var config = data.Sessions[i];

            var session = new SessionInfo
            {
                Name = config.Name,
                WorkingDirectory = config.WorkingDirectory,
                AgentName = config.AgentName
            };

            AddSessionControl(session, row, col);
        }

        RefreshViewLayout();
        UpdateSessionCount();
    }

    /// <summary>
    /// Handles the Apply Grid button click.
    /// </summary>
    private async void ApplyGridButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RowsBox.Text, out int rows) || rows < 1 || rows > 10)
        {
            MessageBox.Show("Rows must be between 1 and 10.", "Invalid Input",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            RowsBox.Focus();
            return;
        }

        if (!int.TryParse(ColsBox.Text, out int cols) || cols < 1 || cols > 10)
        {
            MessageBox.Show("Columns must be between 1 and 10.", "Invalid Input",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ColsBox.Focus();
            return;
        }

        await RebuildGridAsync(rows, cols);
    }

    /// <summary>
    /// Handles preset button clicks (1×1, 2×2, etc.).
    /// </summary>
    private async void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string tag)
        {
            var parts = tag.Split(',');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int rows) &&
                int.TryParse(parts[1], out int cols))
            {
                RowsBox.Text = rows.ToString();
                ColsBox.Text = cols.ToString();
                await RebuildGridAsync(rows, cols);
            }
        }
    }

    /// <summary>
    /// Handles Enter key in dimension text boxes to apply grid.
    /// </summary>
    private void DimensionBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyGridButton_Click(sender, e);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Launches all stopped sessions.
    /// </summary>
    private async void LaunchAllButton_Click(object sender, RoutedEventArgs e)
    {
        string agent = ToolbarAgentCombo.Text.Trim();
        if (string.IsNullOrWhiteSpace(agent)) agent = "opencode";

        int launched = 0;
        var tasks = new List<Task>();

        foreach (var control in _sessionControls)
        {
            if (control.Session.CanLaunch)
            {
                control.SetAgent(agent);
                tasks.Add(control.LaunchSessionAsync());
                launched++;
            }
        }

        if (launched == 0)
        {
            MessageBox.Show("No sessions available to launch.", "Launch All",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        UpdateStatusBar($"Launching {launched} session(s)...");
        await Task.WhenAll(tasks);
        UpdateStatusBar($"Launched {launched} session(s).");
    }

    /// <summary>
    /// Handles the Stop All button click.
    /// </summary>
    private async void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        var running = _sessionControls.Where(c => c.Session.IsRunning).ToList();
        if (running.Count == 0)
        {
            MessageBox.Show("No running sessions to stop.", "Stop All",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await StopAllSessionsAsync();
        UpdateStatusBar("All sessions stopped.");
    }

    /// <summary>
    /// Stops all running sessions.
    /// </summary>
    private async Task StopAllSessionsAsync()
    {
        var running = _sessionControls.Where(c => c.Session.IsRunning).ToList();
        if (running.Count == 0) return;

        var tasks = running.Select(c => c.StopSessionAsync()).ToList();
        UpdateStatusBar($"Stopping {tasks.Count} session(s)...");
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Updates the session count in the status bar.
    /// </summary>
    private void UpdateSessionCount()
    {
        int running = _sessionControls.Count(c => c.Session.Status == SessionStatus.Running);
        int total = _sessionControls.Count;

        if (running > 0)
        {
            SessionCountText.Text = $"{running}/{total} sessions running";
            SessionCountText.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        else
        {
            SessionCountText.Text = $"{total} sessions";
            SessionCountText.Foreground = System.Windows.Media.Brushes.Gray;
        }
    }

    /// <summary>
    /// Updates the status bar text.
    /// </summary>
    private void UpdateStatusBar(string text)
    {
        StatusBarText.Text = text;
    }

    /// <summary>
    /// Saves the current layout.
    /// </summary>
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var sessions = _sessionControls.Select(c => c.Session).ToList();
        LayoutManager.SaveLayout(_currentRows, _currentCols, sessions);
        UpdateStatusBar($"Layout saved ({_currentRows}×{_currentCols}, {sessions.Count} sessions).");
    }

    /// <summary>
    /// Loads a saved layout.
    /// </summary>
    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        var data = LayoutManager.LoadLayout();
        if (data == null)
        {
            MessageBox.Show("No saved layout found.", "Load Layout",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Load saved layout ({data.Rows}×{data.Columns}, {data.Sessions.Count} sessions)?\n" +
            "This will stop all running sessions.",
            "Load Layout",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        int generation = ++_rebuildGeneration;

        await StopAllSessionsAsync();

        if (generation != _rebuildGeneration)
            return;

        ApplyLayout(data);
        UpdateStatusBar($"Layout loaded: {data.Rows}×{data.Columns} ({data.Sessions.Count} sessions).");
    }

    /// <summary>
    /// Enables window dragging from the toolbar.
    /// </summary>
    private void Toolbar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released before the drag started; nothing to do.
        }
    }

    /// <summary>
    /// Double-click on the toolbar toggles maximize/restore.
    /// </summary>
    private void Toolbar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
    }

    /// <summary>
    /// Minimize the window.
    /// </summary>
    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// Toggle maximize/restore.
    /// </summary>
    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    /// <summary>
    /// Close the window.
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Updates the maximize button icon and window border when window state changes.
    /// </summary>
    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            MaximizeButton.Content = "❐";
            MaximizeButton.ToolTip = "Restore";
            WindowBorder.BorderThickness = new Thickness(0);
        }
        else
        {
            MaximizeButton.Content = "□";
            MaximizeButton.ToolTip = "Maximize";
            WindowBorder.BorderThickness = new Thickness(1);
        }
    }

    /// <summary>
    /// Handles window closing - confirm if sessions are running, stop them, and auto-save the layout.
    /// </summary>
    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed)
            return;

        if (_closePending)
        {
            // An earlier close is still stopping sessions and will re-close when done.
            e.Cancel = true;
            return;
        }

        var runningSessions = _sessionControls.Where(c => c.Session.IsRunning).ToList();
        if (runningSessions.Count == 0)
        {
            AutoSaveLayout();
            return;
        }

        // Cancel this close: stopping sessions is asynchronous, and the Closing event
        // has already returned by the time it finishes. Close() is called again below.
        e.Cancel = true;

        var result = MessageBox.Show(
            $"{runningSessions.Count} session(s) are still running.\nStop all sessions and exit?",
            "Confirm Exit",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
            return;

        _closePending = true;
        UpdateStatusBar($"Stopping {runningSessions.Count} session(s)...");
        try
        {
            await StopAllSessionsAsync();
            AutoSaveLayout();

            _closeConfirmed = true;
            Close();
        }
        catch (Exception ex)
        {
            // If stopping fails, report it and let the user try again — never
            // leave the app in a state where Closing is permanently cancelled.
            MessageBox.Show($"Failed to stop sessions: {ex.Message}", "Exit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _closePending = false;
        }
    }

    /// <summary>
    /// Saves the current layout so it can be restored on next startup.
    /// </summary>
    private void AutoSaveLayout()
    {
        if (_sessionControls.Count == 0)
            return;

        var sessions = _sessionControls.Select(c => c.Session).ToList();
        LayoutManager.SaveLayout(_currentRows, _currentCols, sessions);
    }

    /// <summary>
    /// Tries to restore the last saved layout on startup.
    /// </summary>
    private bool TryLoadLayoutOnStartup()
    {
        if (!LayoutManager.HasSavedLayout)
            return false;

        try
        {
            var data = LayoutManager.LoadLayout();
            if (data == null || data.Sessions.Count == 0)
                return false;

            ApplyLayout(data);
            UpdateStatusBar($"Restored layout: {data.Rows}×{data.Columns} ({data.Sessions.Count} sessions).");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Event handler called when any session's status changes.
    /// </summary>
    private void OnSessionStatusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionInfo.Status))
        {
            Dispatcher.BeginInvoke(() => UpdateSessionCount());
        }
    }
}
