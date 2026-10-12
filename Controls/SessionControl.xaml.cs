using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MultiAI.Helpers;
using MultiAI.Models;

namespace MultiAI.Controls;

/// <summary>
/// Interaction logic for SessionControl.xaml
/// </summary>
public partial class SessionControl : System.Windows.Controls.UserControl, INotifyPropertyChanged
{
    private SessionInfo _session;
    private CancellationTokenSource? _cts;

    // The "Add new entry" item and the last real selection (to restore on cancel)
    private ComboBoxItem? _addPathItem;
    private SavedPathEntry? _lastPathEntry;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised when the user clicks the session's ✕ button; MainWindow asks for
    /// confirmation, stops the session if running, and removes the cell.
    /// </summary>
    public event EventHandler? CloseRequested;

    public SessionInfo Session
    {
        get => _session;
        private set
        {
            _session = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Session)));
        }
    }

    public ConsoleHost ConsoleHostControlInstance => ConsoleHostControl;

    /// <summary>
    /// Selects the agent for this session (used by Launch All to sync from toolbar).
    /// </summary>
    public void SetAgent(string agentName)
    {
        _session.AgentName = agentName;
        SelectAgent(agentName);
    }

    public SessionControl()
    {
        InitializeComponent();

        _session = new SessionInfo
        {
            Name = "Session",
            WorkingDirectory = Environment.CurrentDirectory
        };

        DataContext = _session;

        InitComboItems();

        // Compact mode: hide secondary buttons when the cell gets narrow
        SizeChanged += SessionControl_SizeChanged;

        SelectAgent(_session.AgentName);
        SelectPathFor(_session.WorkingDirectory);

        UpdateUI();
    }

    /// <summary>
    /// Cells shrink horizontally to share the row (there is no horizontal
    /// scrolling), so when a cell gets narrow, hide the convenience buttons
    /// first and keep Launch / Stop / ✕ usable.
    /// </summary>
    private void SessionControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = e.NewSize.Width;
        DetachButton.Visibility = w < 380 ? Visibility.Collapsed : Visibility.Visible;
        ExplorerButton.Visibility = w < 440 ? Visibility.Collapsed : Visibility.Visible;
        TerminalButton.Visibility = w < 440 ? Visibility.Collapsed : Visibility.Visible;
    }

    public SessionControl(SessionInfo session) : this()
    {
        SetSession(session);
    }

    /// <summary>
    /// Sets the session data and updates the UI.
    /// </summary>
    public void SetSession(SessionInfo session)
    {
        _session = session;
        DataContext = _session;

        // Selecting the saved folder renames the session to its label, so
        // re-assert the session's own name afterwards.
        SelectPathFor(session.WorkingDirectory);
        SelectAgent(session.AgentName);
        SessionNameBox.Text = session.Name;
        _session.Name = session.Name;
        UpdateUI();
    }

    /// <summary>
    /// Populates the dropdowns. The folder dropdown starts with an "Add new
    /// entry" item followed by the saved label+folder pairs (kept live via the
    /// observable collection); the agent dropdown shows the shared agent list.
    /// </summary>
    private void InitComboItems()
    {
        _addPathItem = new ComboBoxItem { Content = MakeAddEntryText() };
        var pathItems = new System.Windows.Data.CompositeCollection();
        pathItems.Add(_addPathItem);
        pathItems.Add(new System.Windows.Data.CollectionContainer { Collection = PathManager.Entries });
        PathBox.ItemsSource = pathItems;

        AgentCombo.ItemsSource = AgentManager.Agents;
    }

    private static TextBlock MakeAddEntryText() => new TextBlock
    {
        Text = "➕ Add new entry",
        Foreground = new SolidColorBrush(Color.FromRgb(0x4e, 0xc9, 0xb0))
    };

    /// <summary>
    /// Selects the saved entry matching the session's folder, or clears the
    /// selection. The combo's tooltip always shows the actual folder.
    /// </summary>
    private void SelectPathFor(string? workingDirectory)
    {
        var match = PathManager.Entries.FirstOrDefault(e =>
            string.Equals(e.Path, workingDirectory, StringComparison.OrdinalIgnoreCase));
        _lastPathEntry = match;
        PathBox.SelectedItem = match;
        PathBox.ToolTip = string.IsNullOrWhiteSpace(workingDirectory)
            ? "Select a saved folder"
            : workingDirectory;
    }

    /// <summary>
    /// Selects the session's agent in the dropdown, or clears the selection
    /// when the agent is not in the list.
    /// </summary>
    private void SelectAgent(string? agentName)
    {
        var match = AgentManager.Agents.FirstOrDefault(a =>
            string.Equals(a, agentName, StringComparison.OrdinalIgnoreCase));
        AgentCombo.SelectedItem = match;
    }

    /// <summary>
    /// Re-applies the session's saved folder selection (after an entry was
    /// edited elsewhere).
    /// </summary>
    public void RefreshPathSelection() => SelectPathFor(_session.WorkingDirectory);

    /// <summary>
    /// Re-applies the session's agent selection (after an agent was renamed or
    /// removed elsewhere).
    /// </summary>
    public void RefreshAgentSelection() => SelectAgent(_session.AgentName);

    /// <summary>
    /// Updates the read-only label display.
    /// </summary>
    public void SetSessionNameDisplay(string name) => SessionNameBox.Text = name;

    /// <summary>
    /// Updates the UI elements to reflect the current session state.
    /// </summary>
    private void UpdateUI()
    {
        var isRunning = _session.Status == SessionStatus.Running || _session.Status == SessionStatus.Starting;

        LaunchButton.IsEnabled = !isRunning && !string.IsNullOrWhiteSpace(_session.WorkingDirectory);
        StopButton.IsEnabled = isRunning;
        PathBox.IsEnabled = !isRunning;

        StatusIndicator.Background = _session.Status switch
        {
            SessionStatus.Running => new SolidColorBrush(Color.FromRgb(80, 200, 80)),
            SessionStatus.Starting => new SolidColorBrush(Color.FromRgb(200, 200, 80)),
            SessionStatus.Error => new SolidColorBrush(Color.FromRgb(200, 80, 80)),
            _ => new SolidColorBrush(Color.FromRgb(100, 100, 100))
        };

        StatusIndicator.ToolTip = string.IsNullOrEmpty(_session.ErrorMessage)
            ? _session.StatusText
            : $"{_session.StatusText}\n{_session.ErrorMessage}";

        // Enable Detach button when running and the console handle is valid
        DetachButton.IsEnabled = isRunning && ConsoleHostControl.ConsoleHandle != IntPtr.Zero;
    }

    /// <summary>
    /// Handles dropdown selections: a saved folder sets the session's directory
    /// and label; the "Add new entry" item opens the add dialog instead.
    /// </summary>
    private void PathBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;

        if (e.AddedItems[0] is ComboBoxItem item && ReferenceEquals(item, _addPathItem))
        {
            var entry = AddEntryDialogs.PromptForFolder(Window.GetWindow(this), SessionNameBox.Text);
            if (entry != null)
            {
                PathManager.AddEntry(entry.Path, entry.Name);
                SelectPathFor(entry.Path);
                _session.Name = entry.Name;
                SessionNameBox.Text = entry.Name;
                _session.WorkingDirectory = entry.Path;
                UpdateUI();
            }
            else
            {
                PathBox.SelectedItem = _lastPathEntry;
            }
            return;
        }

        if (e.AddedItems[0] is SavedPathEntry entrySel)
        {
            _lastPathEntry = entrySel;
            _session.Name = entrySel.Name;
            SessionNameBox.Text = entrySel.Name;
            _session.WorkingDirectory = entrySel.Path;
            UpdateUI();
        }
    }

    /// <summary>
    /// Right-click lists saved folders so they can be removed.
    /// </summary>
    /// <summary>
    /// Right-click lists saved folders; each entry has Edit and Delete
    /// sub-options. Editing opens the folder dialog pre-filled with the pair.
    /// </summary>
    private void PathBox_RightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Foreground = System.Windows.Media.Brushes.White;
        menu.Background = new SolidColorBrush(Color.FromRgb(45, 45, 45));

        foreach (var entry in PathManager.Entries)
        {
            string oldPath = entry.Path;
            var entryItem = new MenuItem { Header = entry.Name, ToolTip = entry.Path };

            var editItem = new MenuItem { Header = "✏  Edit…" };
            editItem.Click += (_, __) =>
            {
                var updated = AddEntryDialogs.PromptForFolder(Window.GetWindow(this), entry.Name, entry.Path, editMode: true);
                if (updated != null)
                    PathManager.UpdateEntry(oldPath, updated.Name, updated.Path);
            };

            var deleteItem = new MenuItem { Header = "🗑  Delete" };
            deleteItem.Click += (_, __) => PathManager.RemoveEntry(oldPath);

            entryItem.Items.Add(editItem);
            entryItem.Items.Add(deleteItem);
            menu.Items.Add(entryItem);
        }

        if (PathManager.Entries.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No saved folders", IsEnabled = false });
        }

        PathBox.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// Handles the Launch button click to start the selected coding agent.
    /// </summary>
    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        await LaunchSessionAsync();
    }

    /// <summary>
    /// Starts the session: launches opencode in the specified directory.
    /// </summary>
    public async Task LaunchSessionAsync()
    {
        if (_session.Status == SessionStatus.Running || _session.Status == SessionStatus.Starting)
            return;

        string directory = _session.WorkingDirectory.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            MessageBox.Show("Please select a working directory first.", "No Directory", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Directory.Exists(directory))
        {
            var result = MessageBox.Show(
                $"Directory does not exist:\n{directory}\n\nCreate it now?",
                "Directory Not Found",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    Directory.CreateDirectory(directory);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to create directory:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            else
            {
                return;
            }
        }

        // Default the agent if the session has none
        if (string.IsNullOrWhiteSpace(_session.AgentName)) _session.AgentName = "opencode";

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _session.Status = SessionStatus.Starting;
        _session.ErrorMessage = string.Empty;
        UpdateUI();

        try
        {
            var result = await ConsoleWindowManager.StartConsoleProcessAsync(
                directory,
                _session.Id.ToString("N"),
                _session.AgentName,
                _cts.Token);

            if (!result.Success)
            {
                // The shell started but no console window appeared to embed —
                // this is what happens when Windows Terminal hosts the console
                // (its windows can't be hosted via SetParent). Keep the process
                // running as an external session instead of erroring out and
                // orphaning the shell.
                if (result.Process != null && result.ConsoleHandle == IntPtr.Zero)
                {
                    _session.Process = result.Process;
                    _session.ConsoleHandle = IntPtr.Zero;
                    _session.IsExternal = true;
                    _session.Status = SessionStatus.Running;
                    _session.ErrorMessage =
                        "Opened in a separate window: the console window did not appear in time to be " +
                        "embedded. Stop this session and relaunch it; if it keeps happening, it may be " +
                        "a Windows update changing how consoles are hosted.";
                    UpdateUI();
                    _ = MonitorProcessAsync(result.Process);
                    return;
                }

                _session.Status = SessionStatus.Error;
                _session.ErrorMessage = result.ErrorMessage ?? "Unknown error";
                UpdateUI();
                return;
            }

            _session.Process = result.Process;
            _session.ConsoleHandle = result.ConsoleHandle;
            _session.Status = SessionStatus.Running;
            _session.IsExternal = false;

            // Embed the console window in our host
            if (result.ConsoleHandle != IntPtr.Zero)
            {
                ConsoleHostControl.ConsoleHandle = result.ConsoleHandle;
                ConsoleHostControl.ProcessId = result.ConsoleClientProcessId;
                ConsoleHostControl.ResizeConsole();
            }

            UpdateUI();

            // Monitor the process for exit
            if (result.Process != null)
            {
                _ = MonitorProcessAsync(result.Process);
            }
        }
        catch (OperationCanceledException)
        {
            _session.Status = SessionStatus.Stopped;
            UpdateUI();
        }
        catch (Exception ex)
        {
            _session.Status = SessionStatus.Error;
            _session.ErrorMessage = ex.Message;
            UpdateUI();
        }
    }

    /// <summary>
    /// Monitors the process and updates status when it exits. The wait is
    /// event-based (a blocking WaitForExit can't be cancelled, it would just
    /// leak a parked thread); it completes naturally when Stop kills the
    /// process, and this task is the sole owner of disposing the Process.
    /// </summary>
    private async Task MonitorProcessAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None);

            await Dispatcher.InvokeAsync(() =>
            {
                if (_session.Status == SessionStatus.Running || _session.Status == SessionStatus.Starting)
                {
                    _session.Status = SessionStatus.Stopped;
                    _session.Process = null;

                    // Detach the console handle
                    ConsoleHostControl.ConsoleHandle = IntPtr.Zero;
                    ConsoleHostControl.ProcessId = 0;

                    UpdateUI();
                }
            });
        }
        catch (Exception ex)
        {
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _session.Status = SessionStatus.Error;
                    _session.ErrorMessage = $"Monitor error: {ex.Message}";
                    UpdateUI();
                });
            }
            catch { /* dispatcher may be shutting down */ }
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Handles the Stop button click to terminate the session.
    /// </summary>
    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        await StopSessionAsync();
    }

    /// <summary>
    /// Stops the running session.
    /// </summary>
    public async Task StopSessionAsync()
    {
        if (_session.Status != SessionStatus.Running && _session.Status != SessionStatus.Starting)
            return;

        _cts?.Cancel();

        _session.Status = SessionStatus.Stopped;
        UpdateUI();

        await ConsoleWindowManager.StopProcessAsync(_session.Process, CancellationToken.None);

        // The Process object itself is disposed by MonitorProcessAsync once it
        // observes the exit our stop just caused; disposing it here would race.
        _session.Process = null;
        _session.ConsoleHandle = IntPtr.Zero;
        ConsoleHostControl.ConsoleHandle = IntPtr.Zero;
        ConsoleHostControl.ProcessId = 0;

        UpdateUI();
    }

    /// <summary>
    /// Handles the Explorer button click to open the directory in Windows Explorer.
    /// </summary>
    private void ExplorerButton_Click(object sender, RoutedEventArgs e)
    {
        string directory = _session.WorkingDirectory.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            MessageBox.Show("No directory selected.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Directory.Exists(directory))
        {
            MessageBox.Show($"Directory does not exist:\n{directory}", "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{directory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open Explorer:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TerminalButton_Click(object sender, RoutedEventArgs e)
    {
        string directory = _session.WorkingDirectory.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            MessageBox.Show("No directory selected.", "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Directory.Exists(directory))
        {
            MessageBox.Show($"Directory does not exist:\n{directory}", "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/K cd /d \"{directory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to open terminal:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Handles the Detach button click to pop out the embedded console to a separate window.
    /// </summary>
    private void DetachButton_Click(object sender, RoutedEventArgs e)
    {
        // Go through the setter so DetachConsole (window restore + bookkeeping)
        // runs exactly once, and keep the model in sync with the host.
        ConsoleHostControl.ConsoleHandle = IntPtr.Zero;
        ConsoleHostControl.ProcessId = 0;
        _session.ConsoleHandle = IntPtr.Zero;
        DetachButton.IsEnabled = false;
        UpdateUI();
    }

    /// <summary>
    /// Handles dropdown selections: picking an agent sets it for this session.
    /// Agents are added, edited and removed from the header combo.
    /// </summary>
    private void AgentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;

        if (e.AddedItems[0] is string agent)
        {
            _session.AgentName = agent;
        }
    }

    /// <summary>
    /// Resizes the embedded console to fill this control (used after the control
    /// becomes visible again — a hidden HwndHost doesn't track layout changes).
    /// </summary>
    public void ResizeConsole()
    {
        ConsoleHostControl.ResizeConsole();
    }

    private void CloseSessionButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
