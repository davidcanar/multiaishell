using Microsoft.Win32;

namespace MultiAI.Helpers;

/// <summary>
/// Helper class for detecting the default terminal application on Windows.
/// </summary>
public static class TerminalHelper
{
    /// <summary>
    /// Checks whether Windows Terminal is set as the default terminal application.
    /// In Windows 11, this is controlled by the "DelegationConsole" and "DelegationTerminal"
    /// registry values under HKCU\Console\%%Startup.
    /// </summary>
    public static bool IsWindowsTerminalDefault()
    {
        try
        {
            // Windows 10/11: Check if Windows Terminal is the default console host
            // The delegation keys are under HKCU\Console\%%Startup
            using var key = Registry.CurrentUser.OpenSubKey(@"Console\%%Startup");
            if (key != null)
            {
                var delegationConsole = key.GetValue("DelegationConsole") as string;
                var delegationTerminal = key.GetValue("DelegationTerminal") as string;

                // Windows Terminal's CLSID
                const string wtClsid = "{2EACA947-7F5F-4CFA-BA87-8F7FBEEFBE69}";

                if (delegationConsole?.Contains(wtClsid, StringComparison.OrdinalIgnoreCase) == true ||
                    delegationTerminal?.Contains(wtClsid, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }

            // Alternative check: look for Windows Terminal in the default terminal setting
            using var key2 = Registry.CurrentUser.OpenSubKey(@"Console");
            if (key2 != null)
            {
                var defaultValue = key2.GetValue("Default") as string;
                if (defaultValue?.Contains("WindowsTerminal", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore registry errors
        }

        return false;
    }

    /// <summary>
    /// Checks if the current environment is likely to support console window embedding.
    /// </summary>
    public static bool SupportsConsoleEmbedding()
    {
        // Console embedding works best with the legacy Windows Console Host (conhost.exe)
        // Windows Terminal does not support embedding individual tabs
        return !IsWindowsTerminalDefault();
    }

    /// <summary>
    /// Gets a user-friendly message about the terminal configuration.
    /// </summary>
    public static string GetTerminalStatusMessage()
    {
        if (IsWindowsTerminalDefault())
        {
            return "Windows Terminal is set as the default terminal application. " +
                   "For console embedding to work, please change the setting to " +
                   "'Windows Console Host' in:\n" +
                   "Settings → Privacy & security → For developers → Terminal.\n\n" +
                   "Or you can use the 'Separate Window' mode (right-click a session).";
        }

        return "Legacy console host detected. Console embedding should work.";
    }

    /// <summary>
    /// Returns instructions for changing the terminal setting.
    /// </summary>
    public static string GetChangeInstructions()
    {
        return "To change the default terminal:\n" +
               "1. Open Windows Settings (Win + I)\n" +
               "2. Go to 'Privacy & security' → 'For developers'\n" +
               "3. Under 'Terminal', select 'Windows Console Host'\n" +
               "4. Restart the application";
    }
}
