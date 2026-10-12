using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MultiAI.Helpers;

/// <summary>
/// Shared manager for the list of available coding agents.
/// Stored globally so all SessionControls and the toolbar share the same list.
/// Persisted to disk so user additions survive restarts.
/// </summary>
public static class AgentManager
{
    // A safe agent name is a single token: letters, digits, '.', '_', '-', ':' or a
    // path separator. Names are interpolated into a cmd.exe command line, so
    // anything else (spaces, '&', '|', '%', quotes, …) is rejected outright —
    // it would either break the launch or allow arbitrary command injection.
    private static readonly Regex ValidAgentNamePattern =
        new(@"^[A-Za-z0-9._:\-/\\]+$", RegexOptions.Compiled);

    /// <summary>
    /// Returns true if the name is safe to pass to a command line as one token.
    /// </summary>
    public static bool IsValidAgentName(string name) =>
        !string.IsNullOrWhiteSpace(name) && ValidAgentNamePattern.IsMatch(name.Trim());

    private static readonly string ConfigPath;
    private static readonly ObservableCollection<string> _agents = new();
    private static bool _loaded;

    /// <summary>
    /// The shared list of available agent names.
    /// </summary>
    public static ObservableCollection<string> Agents
    {
        get
        {
            if (!_loaded) Load();
            return _agents;
        }
    }

    static AgentManager()
    {
        ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiAI",
            "agents.json");
    }

    /// <summary>
    /// Adds a new agent to the list if it doesn't already exist and is a safe name.
    /// </summary>
    public static bool AddAgent(string name)
    {
        name = name.Trim();
        if (!IsValidAgentName(name)) return false;
        if (_agents.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;

        _agents.Add(name);
        Save();
        return true;
    }

    /// <summary>
    /// Removes an agent from the list.
    /// </summary>
    public static bool RemoveAgent(string name)
    {
        bool removed = _agents.Remove(name);
        if (removed) Save();
        return removed;
    }

    /// <summary>
    /// Raised after an agent was renamed: (old name, new name).
    /// </summary>
    public static event Action<string, string>? AgentRenamed;

    /// <summary>
    /// Renames an agent, keeping its position in the list.
    /// Fails when the new name is invalid or already taken.
    /// </summary>
    public static bool RenameAgent(string oldName, string newName)
    {
        newName = newName.Trim();
        if (!IsValidAgentName(newName)) return false;

        int idx = -1;
        for (int i = 0; i < _agents.Count; i++)
        {
            if (string.Equals(_agents[i], oldName, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }
        if (idx < 0) return false;

        for (int i = 0; i < _agents.Count; i++)
        {
            if (i != idx && string.Equals(_agents[i], newName, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        _agents[idx] = newName;
        AgentRenamed?.Invoke(oldName, newName);
        Save();
        return true;
    }

    /// <summary>
    /// Loads the agent list from disk, or populates defaults if no saved list exists.
    /// </summary>
    public static void Load()
    {
        _loaded = true;
        _agents.Clear();

        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var list = JsonSerializer.Deserialize<List<string>>(json);
                if (list != null)
                {
                    foreach (var a in list)
                    {
                        // Drop entries that no longer pass validation (e.g. tampered
                        // or hand-edited) rather than ever passing them to a shell.
                        if (IsValidAgentName(a))
                            _agents.Add(a.Trim());
                    }
                    if (_agents.Count > 0)
                        return;
                }
            }
        }
        catch
        {
            ConfigBackup.BackupCorruptFile(ConfigPath);
        }

        // Default agents
        _agents.Add("opencode");
        _agents.Add("claude");
        _agents.Add("pi");
    }

    /// <summary>
    /// Saves the current agent list to disk.
    /// </summary>
    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (dir != null) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(_agents.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { /* best effort */ }
    }
}
