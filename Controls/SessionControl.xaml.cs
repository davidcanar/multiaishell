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

    public event PropertyChangedEventHandler? PropertyChanged;

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
    /// Sets the agent combo text (used by Launch All to sync from toolbar).
    /// </summary>
    public void SetAgent(string agentName)
    {
        AgentCombo.Text = agentName;
        _session.AgentName = agentName;
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

        // Bind the agent combo to the shared agent list
        AgentCombo.ItemsSource = AgentManager.Agents;
        AgentCombo.Text = _session.AgentName;

        // Bind the path combo to the shared saved-path list
        PathBox.ItemsSource = PathManager.Entries;

        UpdateUI();
    }

    public SessionControl(SessionInfo session) : this()
    {
        _session = session;
        DataContext = _session;
        SessionNameBox.Text = session.Name;
        PathBox.Text = session.WorkingDirectory;
        AgentCombo.Text = session.AgentName;
        UpdateUI();
    }

    /// <summary>
    /// Sets the session data and updates the UI.
    /// </summary>
    public void SetSession(SessionInfo session)
    {
        _session = session;
        DataContext = _session;
        SessionNameBox.Text = session.Name;
        PathBox.Text = session.WorkingDirectory;
        AgentCombo.Text = session.AgentName;
        UpdateUI();
    }

    /// <summary>
    /// Updates the UI elements to reflect the current session state.
    /// </summary>
    private void UpdateUI()
    {
        var isRunning = _session.Status == SessionStatus.Running || _session.Status == SessionStatus.Starting;

        LaunchButton.IsEnabled = !isRunning && !string.IsNullOrWhiteSpace(PathBox.Text);
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
    /// Handles the Browse button click to select a working directory.
    /// </summary>
    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the working directory for opencode",
            SelectedPath = PathBox.Text,
            ShowNewFolderButton = true
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            PathBox.Text = dialog.SelectedPath;
            _session.WorkingDirectory = dialog.SelectedPath;
        }
    }

    /// <summary>
    /// Handles Enter key in the path combo to save the current path with session name.
    /// </summary>
    private void PathBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SyncPathFromCombo();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Syncs the session working directory when the path combo loses focus.
    /// </summary>
    private void PathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SyncPathFromCombo();
    }

    /// <summary>
    /// When a saved path is selected from the dropdown, restore its saved session name.
    /// </summary>
    private void PathBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is SavedPathEntry entry)
        {
            _session.Name = entry.Name;
            SessionNameBox.Text = entry.Name;
            _session.WorkingDirectory = entry.Path;
        }
    }

    /// <summary>
    /// Syncs the current path combo text to the session model and saves it with the session name.
    /// </summary>
    private void SyncPathFromCombo()
    {
        string text = PathBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            string name = SessionNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "Session";
            PathManager.AddEntry(text, name);
        }
        _session.WorkingDirectory = text;
    }

    /// <summary>
    /// Handles right-click on the path combo to show add/remove context menu.
    /// </summary>
    private void PathBox_RightClick(object sender, MouseButtonEventArgs e)
    {
        string current = PathBox.Text.Trim();

        var menu = new ContextMenu();
        menu.Foreground = System.Windows.Media.Brushes.White;
        menu.Background = new SolidColorBrush(Color.FromRgb(45, 45, 45));

        // Add current text as saved path (with session name)
        if (!string.IsNullOrWhiteSpace(current) &&
            PathManager.TryGetName(current) == null)
        {
            var addItem = new MenuItem
            {
                Header = $"Add \"{current}\"",
                Foreground = System.Windows.Media.Brushes.LightGreen
            };
            addItem.Click += (_, __) =>
            {
                string name = SessionNameBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(name)) name = "Session";
                PathManager.AddEntry(current, name);
            };
            menu.Items.Add(addItem);
            menu.Items.Add(new Separator());
        }

        // List all saved paths with option to remove
        foreach (var entry in PathManager.Entries)
        {
            var item = new MenuItem
            {
                Header = $"Remove \"{entry.Path}\"",
                Foreground = System.Windows.Media.Brushes.LightCoral
            };
            string captured = entry.Path;
            item.Click += (_, __) => PathManager.RemoveEntry(captured);
            menu.Items.Add(item);
        }

        PathBox.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// Handles the Launch button click to start opencode.
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

        string directory = PathBox.Text.Trim();
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

        // Save the selected agent before launching
        string agent = AgentCombo.Text.Trim();
        if (string.IsNullOrWhiteSpace(agent)) agent = "opencode";
        _session.AgentName = agent;

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
        string directory = PathBox.Text.Trim();
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
        string directory = PathBox.Text.Trim();
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
    /// Handles Enter key in the agent combo to add a new agent to the list.
    /// </summary>
    private void AgentCombo_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            string text = AgentCombo.Text.Trim();
            if (!string.IsNullOrWhiteSpace(text) &&
                !AgentManager.Agents.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                AgentManager.AddAgent(text);
            }
            e.Handled = true;
        }
    }

    /// <summary>
    /// Syncs the agent combo text to the session when focus leaves, so a
    /// per-session agent choice is saved even if the session never launches.
    /// </summary>
    private void AgentCombo_LostFocus(object sender, RoutedEventArgs e)
    {
        string text = AgentCombo.Text.Trim();
        if (!string.IsNullOrWhiteSpace(text))
            _session.AgentName = text;
    }

    /// <summary>
    /// Handles right-click on the agent combo to show add/remove context menu.
    /// </summary>
    private void AgentCombo_RightClick(object sender, MouseButtonEventArgs e)
    {
        string current = AgentCombo.Text.Trim();

        var menu = new ContextMenu();
        menu.Foreground = System.Windows.Media.Brushes.White;
        menu.Background = new SolidColorBrush(Color.FromRgb(45, 45, 45));

        // Add current text as new agent
        if (!string.IsNullOrWhiteSpace(current) &&
            !AgentManager.Agents.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            var addItem = new MenuItem
            {
                Header = $"Add \"{current}\"",
                Foreground = System.Windows.Media.Brushes.LightGreen
            };
            addItem.Click += (_, __) => AgentManager.AddAgent(current);
            menu.Items.Add(addItem);
            menu.Items.Add(new Separator());
        }

        // List all agents with option to remove
        foreach (var agent in AgentManager.Agents)
        {
            var item = new MenuItem
            {
                Header = $"Remove \"{agent}\"",
                Foreground = System.Windows.Media.Brushes.LightCoral
            };
            string captured = agent;
            item.Click += (_, __) => AgentManager.RemoveAgent(captured);
            menu.Items.Add(item);
        }

        AgentCombo.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>
    /// Handles the session name text box getting focus (select all text).
    /// </summary>
    private void SessionNameBox_GotFocus(object sender, RoutedEventArgs e)
    {
        SessionNameBox.SelectAll();
    }

    /// <summary>
    /// Handles the session name text box losing focus.
    /// </summary>
    private void SessionNameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitSessionName();
    }

    /// <summary>
    /// Handles Enter key to commit session name.
    /// </summary>
    private void SessionNameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitSessionName();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Commits the session name from the text box to the model.
    /// Also updates the saved name if the current path is a saved entry.
    /// </summary>
    private void CommitSessionName()
    {
        string name = SessionNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Session";
            SessionNameBox.Text = name;
        }
        _session.Name = name;

        // If the current path has a saved entry, update its name
        string path = PathBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(path) && PathManager.TryGetName(path) != null)
        {
            PathManager.UpdateName(path, name);
        }
    }
}
