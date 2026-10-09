using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace MultiAI.Models;

public enum SessionStatus
{
    Stopped,
    Starting,
    Running,
    Error
}

public class SessionInfo : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _workingDirectory = string.Empty;
    private SessionStatus _status = SessionStatus.Stopped;
    private IntPtr _consoleHandle = IntPtr.Zero;
    private Process? _process;
    private string _errorMessage = string.Empty;
    private bool _isExternal;
    private string _agentName = "opencode";

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set { _workingDirectory = value; OnPropertyChanged(); }
    }

    public SessionStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    public IntPtr ConsoleHandle
    {
        get => _consoleHandle;
        set { _consoleHandle = value; OnPropertyChanged(); }
    }

    public Process? Process
    {
        get => _process;
        set { _process = value; OnPropertyChanged(); }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// The coding agent to launch (e.g. "opencode", "claude", "pi").
    /// </summary>
    public string AgentName
    {
        get => _agentName;
        set { _agentName = value; OnPropertyChanged(); }
    }

    public bool IsExternal
    {
        get => _isExternal;
        set { _isExternal = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    public string StatusText
    {
        get
        {
            if (_isExternal && _status == SessionStatus.Running)
                return "Running (external)";
            return _status switch
            {
                SessionStatus.Stopped => "Stopped",
                SessionStatus.Starting => "Starting...",
                SessionStatus.Running => "Running",
                SessionStatus.Error => $"Error: {ErrorMessage}",
                _ => "Unknown"
            };
        }
    }

    public bool IsRunning => Status == SessionStatus.Running || Status == SessionStatus.Starting;
    public bool CanLaunch => Status == SessionStatus.Stopped || Status == SessionStatus.Error;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
