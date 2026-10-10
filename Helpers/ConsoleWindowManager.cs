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

            // Launch through conhost.exe explicitly: this bypasses the "default
            // terminal application" delegation entirely (Windows 11 otherwise
            // hands new consoles to Windows Terminal, whose windows cannot be
            // embedded), and works regardless of the user's terminal setting.
            //
            // No outer quotes around the cmd payload: nested quotes would depend
            // on cmd's fragile first/last-quote stripping. Quoting only the path
            // keeps cmd specials (& ^ parentheses) in it literal, and '&&' keeps
            // the agent from silently running in the wrong directory if the cd
            // fails (e.g. UNC path). Known limitation: cmd expands %VAR% even
            // inside quotes. The agent name is validated (single safe token).
            var psi = new ProcessStartInfo
            {
                FileName = "conhost.exe",
                Arguments = $"cmd /k title {windowTitle} & cd /d \"{workingDirectory}\" && {agentName}",
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

                // Match by title PREFIX: modern Windows cmd appends
                // " - <running command>" to the console title while a command
                // executes, so the exact title only exists for milliseconds.
                // The GUID prefix stays stable from the 'title' command until
                // the agent's TUI overrides the title — far longer than our
                // polling interval. The prefix contains a fresh GUID, so it
                // can only ever match our own console.
                consoleHwnd = NativeMethods.FindWindowByTitlePrefix(windowTitle);

                if (consoleHwnd == IntPtr.Zero)
                {
                    await Task.Delay(100, cancellationToken);
                    retryCount++;
                }
            }

            if (consoleHwnd == IntPtr.Zero)
            {
                // Keep the process: the session will be tracked as an external
                // ("Running (external)") window by the caller.
                result.ErrorMessage = "Console window did not appear within the expected time; " +
                    "the session will keep running in its own window.";
                return result;
            }

            result.ConsoleHandle = consoleHwnd;

            // The tracked process is conhost.exe. Console APIs used later
            // (AttachConsole for paste and font control) need the PID of a
            // process attached to the console as a *client*, i.e. the cmd.exe
            // child that conhost spawned.
            result.ConsoleClientProcessId = NativeMethods.FindChildProcessId(process.Id, "cmd.exe");
            if (result.ConsoleClientProcessId == 0)
                result.ConsoleClientProcessId = process.Id; // fallback: paste may not work, embedding still does

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

    /// <summary>
    /// PID of a process attached to the console as a client (the cmd.exe child),
    /// needed by AttachConsole-based paste/font control. The tracked <see cref="Process"/>
    /// itself is the conhost.exe host.
    /// </summary>
    public int ConsoleClientProcessId { get; set; }

    public string? ErrorMessage { get; set; }
    public bool Success => ErrorMessage == null && Process != null;
}
