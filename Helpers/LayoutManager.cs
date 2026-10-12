using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MultiAI.Models;

namespace MultiAI.Helpers;

/// <summary>
/// Manages saving and loading of grid layouts (session configurations).
/// </summary>
public static class LayoutManager
{
    private static readonly string ConfigFolder;
    private static readonly string ConfigFilePath;

    static LayoutManager()
    {
        ConfigFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiAI");
        ConfigFilePath = Path.Combine(ConfigFolder, "layout.json");

        Directory.CreateDirectory(ConfigFolder);
    }

    /// <summary>
    /// Saves the current grid layout to disk.
    /// </summary>
    public static void SaveLayout(int rows, int cols, IReadOnlyList<SessionInfo> sessions,
        bool tabsMode = false, int selectedTabIndex = 0)
    {
        try
        {
            var data = new LayoutData
            {
                Rows = rows,
                Columns = cols,
                ViewMode = tabsMode ? "tabs" : "tiles",
                SelectedTabIndex = selectedTabIndex,
                Sessions = sessions.Select(s => new SessionConfig
                {
                    Name = s.Name,
                    WorkingDirectory = s.WorkingDirectory,
                    AgentName = s.AgentName
                }).ToList()
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            string json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(ConfigFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save layout: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads the grid layout from disk.
    /// </summary>
    public static LayoutData? LoadLayout()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
                return null;

            string json = File.ReadAllText(ConfigFilePath);
            return JsonSerializer.Deserialize<LayoutData>(json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load layout: {ex.Message}");
            // Keep a copy of the unreadable file so the next save (auto-save on
            // exit) can't silently destroy whatever was in it.
            ConfigBackup.BackupCorruptFile(ConfigFilePath);
            return null;
        }
    }

    /// <summary>
    /// Checks if a saved layout exists.
    /// </summary>
    public static bool HasSavedLayout => File.Exists(ConfigFilePath);
}

/// <summary>
/// Serializable layout configuration.
/// </summary>
public class LayoutData
{
    [JsonPropertyName("rows")]
    public int Rows { get; set; } = 2;

    [JsonPropertyName("columns")]
    public int Columns { get; set; } = 3;

    // "tabs" or "tiles" — which top-bar mode was active at save time.
    // Missing (older layout.json) falls back to tiles, the old behavior.
    [JsonPropertyName("viewMode")]
    public string ViewMode { get; set; } = "tiles";

    // Which tab was selected (only meaningful when ViewMode is "tabs").
    [JsonPropertyName("selectedTab")]
    public int SelectedTabIndex { get; set; } = 0;

    [JsonPropertyName("sessions")]
    public List<SessionConfig> Sessions { get; set; } = new();
}

/// <summary>
/// Serializable session configuration.
/// </summary>
public class SessionConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Session";

    [JsonPropertyName("workingDirectory")]
    public string WorkingDirectory { get; set; } = "";

    [JsonPropertyName("agent")]
    public string AgentName { get; set; } = "opencode";
}
