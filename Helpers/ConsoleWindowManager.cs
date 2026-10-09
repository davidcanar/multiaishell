using System.Diagnostics;
using System.IO;
using MultiAI.Models;

namespace MultiAI.Helpers;

/// <summary>
/// Manages console window processes: starting, embedding, and stopping them.
/// </summary>
public static class ConsoleWindowManager
{
    /// <summary>
    /// Starts a new cmd.exe process running opencode in the specified directory,
    /// and returns the process info needed for embedding.
    /// Uses the standard approach that works with legacy console host.
    /// </summary>
    /// <param name="agentName">The coding agent to run (e.g. "opencode", "claude", "pi").</param>
    public static async Task<ProcessStartResult> StartConsoleProcessAsync(
        string workingDirectory,
        string sessionId,
        string agentName,
        CancellationToken cancellationToken)
    {
        var result = new ProcessStartResult();

        if (!Directory.Exists(workingDirectory))
        {
            result.ErrorMessage = $"Directory does not exist: {workingDirectory}";
            return result;
        }

        if (string.IsNullOrWhiteSpace(agentName))
            agentName = "opencode";

        // The agent name is interpolated into a cmd.exe command line; never
        // launch anything that isn't a single safe token (defense in depth —
        // AgentManager already validates on add and on load).
        if (!AgentManager.IsValidAgentName(agentName))
        {
            result.ErrorMessage =
                $"Invalid agent name \"{agentName}\". Only letters, digits and " +
                "'. _ - : / \\' are allowed — spaces and shell characters are rejected for safety.";
            return result;
        }

        try
        {
            string windowTitle = $"MultiAI_Session_{sessionId}";

            // Set title, cd to directory, then run the selected agent. No outer quotes
            // around the /k payload: nested quotes would depend on cmd's fragile
            // first/last-quote stripping. Quoting only the path keeps cmd specials
            // (& ^ parentheses) in it literal, and '&&' keeps the agent from silently
            // running in the wrong directory if the cd fails (e.g. UNC path).
            // Known limitation: cmd expands %VAR% even inside quotes. The agent name
            // itself is validated (single safe token) before we get here.
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k title {windowTitle} & cd /d \"{workingDirectory}\" && {agentName}",
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal,
            };

            var process = Process.Start(psi);
            if (process == null)
            {
                result.ErrorMessage = "Failed to start process.";
                return result;
            }

            result.Process = process;

            // Wait for the console window to appear by polling for the unique title
            IntPtr consoleHwnd = IntPtr.Zero;
            int maxRetries = 40; // ~4 seconds at 100ms intervals
            int retryCount = 0;

            while (consoleHwnd == IntPtr.Zero && retryCount < maxRetries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Find by exact title (set via the 'title' command). The title
                // contains a fresh GUID, so it can only match our own console.
                consoleHwnd = NativeMethods.FindWindowByTitle(windowTitle);

                if (consoleHwnd == IntPtr.Zero)
                {
                    await Task.Delay(100, cancellationToken);
                    retryCount++;
                }
            }

            if (consoleHwnd == IntPtr.Zero)
            {
                result.ErrorMessage = "Console window did not appear within the expected time. " +
                    "This may happen if Windows Terminal is your default terminal.\n" +
                    "Please change your default terminal to 'Windows Console Host':\n" +
                    "Open Windows Terminal → Settings → Default terminal application → 'Windows Console Host'.";
                return result;
            }

            result.ConsoleHandle = consoleHwnd;
            return result;
        }
        catch (OperationCanceledException)
        {
            // The start was cancelled mid-flight (e.g. Stop clicked during the
            // window-appear wait); don't leave a freshly started console running
            // untracked — nothing else would ever own or stop it.
            TryKillAndDispose(result.Process);
            result.Process = null;
            result.ErrorMessage = "Operation was cancelled.";
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Error starting process: {ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// Best-effort kill and dispose of a process we no longer track.
    /// </summary>
    private static void TryKillAndDispose(Process? process)
    {
        try
        {
            if (process != null && !process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { /* best effort */ }
        try { process?.Dispose(); } catch { }
    }

    /// <summary>
    /// Embeds a console window into a parent window and styles it for embedding.
    /// </summary>
    public static void EmbedConsoleWindow(IntPtr consoleHwnd, IntPtr parentHwnd)
    {
        if (consoleHwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero) return;
        if (!NativeMethods.IsWindow(consoleHwnd)) return;

        // Reparent the console window to our host
        NativeMethods.SetParent(consoleHwnd, parentHwnd);

        // Style it for embedding (remove title bar, borders, etc.)
        NativeMethods.StyleWindowForEmbedding(consoleHwnd);

        // Show and resize to fill parent
        NativeMethods.ShowWindow(consoleHwnd, NativeMethods.SW_SHOWMAXIMIZED);

        NativeMethods.GetClientRect(parentHwnd, out var rect);
        NativeMethods.MoveWindow(consoleHwnd, 0, 0,
            rect.Right - rect.Left, rect.Bottom - rect.Top, true);

        // Send activation messages so the console knows it should process input
        NativeMethods.SendMessage(consoleHwnd, 0x0006, IntPtr.Zero, IntPtr.Zero); // WM_ACTIVATE
        NativeMethods.SendMessage(consoleHwnd, 0x0007, consoleHwnd, IntPtr.Zero);  // WM_SETFOCUS

        NativeMethods.UpdateWindow(consoleHwnd);
    }

    /// <summary>
    /// Resizes an embedded console window to fill its parent.
    /// </summary>
    public static void ResizeEmbeddedWindow(IntPtr consoleHwnd, IntPtr parentHwnd)
    {
        if (consoleHwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero) return;
        if (!NativeMethods.IsWindow(consoleHwnd)) return;

        NativeMethods.GetClientRect(parentHwnd, out var rect);
        NativeMethods.MoveWindow(consoleHwnd, 0, 0,
            rect.Right - rect.Left, rect.Bottom - rect.Top, true);
    }

    /// <summary>
    /// Detaches a previously embedded console window, restoring its title bar and reparenting to desktop.
    /// </summary>
    public static void DetachEmbeddedWindow(IntPtr consoleHwnd)
    {
        if (consoleHwnd == IntPtr.Zero) return;
        if (!NativeMethods.IsWindow(consoleHwnd)) return;

        int style = NativeMethods.GetWindowLong(consoleHwnd, NativeMethods.GWL_STYLE);
        style = (int)((uint)style | NativeMethods.WS_CAPTION | NativeMethods.WS_SYSMENU |
                      NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX | NativeMethods.WS_THICKFRAME);
        style = (int)((uint)style & ~NativeMethods.WS_CHILD);
        NativeMethods.SetWindowLong(consoleHwnd, NativeMethods.GWL_STYLE, style);

        NativeMethods.SetParent(consoleHwnd, IntPtr.Zero);

        NativeMethods.SetWindowPos(consoleHwnd, IntPtr.Zero, 100, 100, 900, 600,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_SHOWWINDOW);
        NativeMethods.ShowWindow(consoleHwnd, NativeMethods.SW_SHOWNORMAL);
    }

    /// <summary>
    /// Stops a console process gracefully, then forcefully if needed.
    /// </summary>
    public static async Task StopProcessAsync(Process? process, CancellationToken cancellationToken)
    {
        try
        {
            // HasExited can throw (handle races), so it lives inside the try —
            // callers treat stopping as best-effort and must not fail on it.
            if (process == null || process.HasExited) return;

            process.CloseMainWindow();

            if (!process.HasExited)
                await Task.Delay(1000, cancellationToken);

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await Task.Delay(500, cancellationToken);
            }
        }
        catch (Exception)
        {
            // Stop is best-effort
        }
    }
}

/// <summary>
/// Result of starting a console process.
/// </summary>
public class ProcessStartResult
{
    public Process? Process { get; set; }
    public IntPtr ConsoleHandle { get; set; } = IntPtr.Zero;
    public string? ErrorMessage { get; set; }
    public bool Success => ErrorMessage == null && Process != null;
}
