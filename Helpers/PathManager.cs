using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MultiAI.Helpers;

public static class PathManager
{
    private static readonly string ConfigPath;
    private static readonly ObservableCollection<SavedPathEntry> _entries = new();
    private static bool _loaded;

    public static ObservableCollection<SavedPathEntry> Entries
    {
        get
        {
            if (!_loaded) Load();
            return _entries;
        }
    }

    static PathManager()
    {
        ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiAI",
            "paths.json");
    }

    public static bool AddEntry(string path, string name)
    {
        path = path.Trim();
        if (string.IsNullOrWhiteSpace(path)) return false;

        // Update name if entry already exists
        var existing = _entries.FirstOrDefault(e =>
            e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Name = name;
            Save();
            return true;
        }

        _entries.Add(new SavedPathEntry { Path = path, Name = name });
        Save();
        return true;
    }

    public static bool RemoveEntry(string path)
    {
        var entry = _entries.FirstOrDefault(e =>
            e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return false;

        bool removed = _entries.Remove(entry);
        if (removed) Save();
        return removed;
    }

    public static string? TryGetName(string path)
    {
        return _entries.FirstOrDefault(e =>
            e.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    public static void UpdateName(string path, string newName)
    {
        var entry = _entries.FirstOrDefault(e =>
            e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            entry.Name = newName;
            Save();
        }
    }

    public static void Load()
    {
        _loaded = true;
        _entries.Clear();

        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var list = JsonSerializer.Deserialize<List<SavedPathEntry>>(json);
                if (list != null && list.Count > 0)
                {
                    foreach (var e in list) _entries.Add(e);
                    return;
                }
            }
        }
        catch
        {
            // If deserialization fails (e.g. old string[] format), start fresh —
            // but keep a copy so the next Save() can't silently destroy the file.
            ConfigBackup.BackupCorruptFile(ConfigPath);
        }
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (dir != null) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(_entries.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { /* best effort */ }
    }
}
