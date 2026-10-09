using System.IO;

namespace MultiAI.Helpers;

/// <summary>
/// Keeps a copy of a config file that failed to parse, so the next Save()
/// doesn't silently destroy whatever the user had in it.
/// </summary>
internal static class ConfigBackup
{
    public static void BackupCorruptFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Copy(path, path + ".corrupt.bak", overwrite: true);
        }
        catch { /* best effort */ }
    }
}
