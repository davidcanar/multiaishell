using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MultiAI.Helpers;

namespace MultiAI.Controls;

/// <summary>
/// HwndHost that hosts an embedded console window via SetParent,
/// with focus forwarding and clipboard paste via WriteConsoleInput.
/// </summary>
public class ConsoleHost : HwndHost
{
    private IntPtr _consoleHandle = IntPtr.Zero;
    private bool _isEmbedded;
    private int _processId; // PID of the console process (cmd.exe)

    public IntPtr ConsoleHandle
    {
        get => _consoleHandle;
        set
        {
            if (_consoleHandle != value)
            {
                DetachConsole();
                _consoleHandle = value;
                TryAttach();
            }
        }
    }

    private void TryAttach()
    {
        if (_consoleHandle != IntPtr.Zero && Handle != IntPtr.Zero && !_isEmbedded)
            AttachConsole();
    }

    /// <summary>
    /// Set this after launching so we can attach to the console for WriteConsoleInput.
    /// </summary>
    public int ProcessId
    {
        get => _processId;
        set => _processId = value;
    }

    public bool IsEmbedded => _isEmbedded;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        IntPtr hostHwnd = CreateWindowEx(
            0, "static", "",
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, 100, 100,
            hwndParent.Handle,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (hostHwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Failed to create host window. Error: {Marshal.GetLastWin32Error()}");

        // Attach any pending console handle now that the host window exists
        if (_consoleHandle != IntPtr.Zero && !_isEmbedded)
            DoAttach(hostHwnd);

        return new HandleRef(this, hostHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        DetachConsole();
        DestroyWindow(hwnd.Handle);
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        ResizeConsole();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        TryAttach();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => ResizeConsole()));
    }

    // ——— Focus ———

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        FocusConsole();
    }

    protected override bool TabIntoCore(TraversalRequest request)
    {
        FocusConsole();
        return true;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETFOCUS = 0x0007;
        if (msg == WM_SETFOCUS)
            FocusConsole();
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    // ——— Low-level keyboard hook for Ctrl+V ———
    //
    // The embedded console is a cross-process child window, so keystrokes typed
    // into it bypass WPF entirely — intercepting Ctrl+V there requires a global
    // hook. It is tightly scoped: the callback ignores every keystroke unless
    // MultiAI's own window is the foreground window and one of the embedded
    // consoles verifiably holds keyboard focus, and the hook is uninstalled
    // while no console is embedded.

    private static readonly List<ConsoleHost> _instances = new();
    private static IntPtr _kbHookId = IntPtr.Zero;
    private static HookProc? _kbHookProc;

    private static readonly object _hookLock = new();

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_CHAR = 0x0102;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_V = 0x56;
    private const uint SHIFT_PRESSED = 0x0010;
    private const uint GA_ROOT = 2;

    /// <summary>
    /// Installs the keyboard hook once, shared by all instances.
    /// </summary>
    private static void EnsureKeyboardHookInstalled()
    {
        lock (_hookLock)
        {
            if (_kbHookId != IntPtr.Zero) return;

            _kbHookProc = KeyboardHookCallback;
            _kbHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _kbHookProc, GetModuleHandle(null!), 0);
        }
    }

    /// <summary>
    /// Removes the keyboard hook once the last embedded console is gone, so the
    /// app runs with no system-wide hooks at all while idle.
    /// </summary>
    private static void UninstallKeyboardHookIfIdle()
    {
        lock (_hookLock)
        {
            if (_instances.Count != 0 || _kbHookId == IntPtr.Zero) return;
            UnhookWindowsHookEx(_kbHookId);
            _kbHookId = IntPtr.Zero;
            _kbHookProc = null;
        }
    }

    private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                var kbStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vkCode = kbStruct.vkCode;

                // Check for Ctrl+V
                if (vkCode == VK_V && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0)
                {
                    ConsoleHost? target = FindFocusedHost();
                    if (target != null)
                    {
                        // BeginInvoke: clipboard access inside a low-level hook callback
                        // can exceed the system hook timeout, which silently uninstalls
                        // the hook and kills Ctrl+V for the rest of the session.
                        target.Dispatcher.BeginInvoke(new Action(() => target.PasteFromClipboard()));
                        return (IntPtr)1; // Block the keystroke
                    }
                }
            }
        }

        return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// Returns the embedded console host whose console window currently holds the
    /// keyboard focus, or null — in which case Ctrl+V must not be intercepted.
    /// Null unless MultiAI's own window is the foreground window: when the focus
    /// is anywhere else (a WPF text box, another window, another application) the
    /// keystroke belongs to that target, not to us.
    /// </summary>
    private static ConsoleHost? FindFocusedHost()
    {
        // Hard guard: only ever intercept while one of our own windows is the
        // foreground window. (When an embedded console has focus, the foreground
        // *root* window is still this process's main window, so embedded consoles
        // pass this check; every other application's windows do not.)
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return null;
        GetWindowThreadProcessId(foreground, out int foregroundPid);
        if (foregroundPid != Environment.ProcessId) return null;

        // The foreground thread's focus window is authoritative when it reports one
        // (e.g. a WPF text box holds focus on the main window's HWND) — Ctrl+V then
        // belongs to that control and must pass through untouched.
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (GetGUIThreadInfo(0, ref info) && info.hwndFocus != IntPtr.Zero)
            return FindHostByConsole(info.hwndFocus);

        // The foreground thread reports no focus window: the focus lives on an
        // embedded console's own (cross-process) thread. Trust that thread's focus
        // only for a console that actually sits inside the foreground window, so a
        // stale focus state can't hijack Ctrl+V aimed at another window.
        foreach (var host in _instances)
        {
            IntPtr console = host._consoleHandle;
            if (!host._isEmbedded || console == IntPtr.Zero || !NativeMethods.IsWindow(console))
                continue;
            if (GetAncestor(console, GA_ROOT) != foreground)
                continue;

            int threadId = GetWindowThreadProcessId(console, out _);
            if (threadId == 0) continue;

            var consoleInfo = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo((uint)threadId, ref consoleInfo) &&
                (consoleInfo.hwndFocus == console ||
                 (consoleInfo.hwndFocus != IntPtr.Zero && IsChild(console, consoleInfo.hwndFocus))))
            {
                return host;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the host whose embedded console window is (or contains) the given window.
    /// </summary>
    private static ConsoleHost? FindHostByConsole(IntPtr hwnd)
    {
        foreach (var host in _instances)
        {
            IntPtr console = host._consoleHandle;
            if (host._isEmbedded && console != IntPtr.Zero && NativeMethods.IsWindow(console) &&
                (hwnd == console || IsChild(console, hwnd)))
            {
                return host;
            }
        }
        return null;
    }

    // ——— Paste via WriteConsoleInput ———

    /// <summary>
    /// Pastes clipboard text into the console's input buffer using WriteConsoleInput.
    /// This is the correct way to inject input into any console application.
    /// </summary>
    public void PasteFromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            string text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text)) return;

            PasteText(text);
        }
        catch { }
    }

    /// <summary>
    /// Injects text into the console. The primary path writes key records straight
    /// into the console's input buffer (WriteConsoleInput); the fallback posts
    /// WM_CHAR messages to the console window.
    /// </summary>
    public void PasteText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Normalize all line endings to \n. It is injected as a bare LF (the
        // Ctrl+J / Ctrl+Enter character), which agent TUIs treat as "insert a
        // newline" rather than "submit the prompt".
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // Method 1: WriteConsoleInput (injects directly into console input buffer)
        if (_processId > 0 && PasteViaConsoleInput(text))
            return;

        // Method 2: Fallback - send WM_CHAR messages to the console window
        PasteViaWindowMessages(text);
    }

    /// <summary>
    /// Paste by writing key events into the console's input buffer.
    /// Opens CONIN$ explicitly: this GUI process's std handles can point at a
    /// redirected pipe (e.g. when launched under a debugger) instead of the
    /// attached console, which made the GetStdHandle-based version fail — the
    /// same lesson the font code learned with CONOUT$.
    /// Every record carries an explicit modifier state, so the Ctrl key still
    /// physically held from Ctrl+V can't combine with the injected characters
    /// (conhost turns Ctrl+Space into NUL under VT input, which ate spaces).
    /// Must run on the UI thread: AttachConsole/FreeConsole is process-global
    /// state that the font watchdog also cycles on that thread.
    /// </summary>
    private bool PasteViaConsoleInput(string text)
    {
        try
        {
            FreeConsole();
            if (!AttachConsole(_processId))
                return false;

            try
            {
                IntPtr hIn = CreateFile("CONIN$", GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (hIn == IntPtr.Zero || hIn == (IntPtr)(-1))
                    return false;

                try
                {
                    var records = BuildKeyRecords(text);
                    if (records.Length == 0)
                        return true; // nothing printable to paste

                    // Bounded chunks so a single call never marshals a huge buffer.
                    const int chunkSize = 2048;
                    bool anyWritten = false;
                    for (int offset = 0; offset < records.Length; offset += chunkSize)
                    {
                        int count = Math.Min(chunkSize, records.Length - offset);
                        var chunk = new INPUT_RECORD[count];
                        Array.Copy(records, offset, chunk, 0, count);

                        if (!WriteConsoleInput(hIn, chunk, (uint)count, out uint written) || written == 0)
                        {
                            // Once anything was written, report success anyway so the
                            // fallback doesn't paste the whole text a second time.
                            return anyWritten;
                        }
                        anyWritten = true;
                    }
                    return true;
                }
                finally
                {
                    CloseHandle(hIn);
                }
            }
            finally
            {
                FreeConsole();
            }
        }
        catch
        {
            try { FreeConsole(); } catch { }
            return false;
        }
    }

    /// <summary>
    /// Builds a key-down/key-up record pair per character. The virtual key (plus
    /// SHIFT flag) is reported when the character maps to a plain or shifted key
    /// on the current layout; characters that would need Ctrl/Alt (AltGr) are sent
    /// as bare character records so no control-key state is ever implied.
    /// </summary>
    private static INPUT_RECORD[] BuildKeyRecords(string text)
    {
        var records = new List<INPUT_RECORD>(text.Length * 2);

        foreach (char ch in text)
        {
            // Strip control characters except newline and tab; they only confuse TUIs.
            if ((ch < 0x20 && ch != '\n' && ch != '\t') || ch == (char)0x7F)
                continue;

            ushort vk = 0;
            uint controlState = 0;

            // '\n' keeps VK 0 so cooked-mode readers don't treat it as Enter.
            if (ch != '\n')
            {
                short scan = VkKeyScanW(ch);
                if (scan != -1)
                {
                    int modifiers = (scan >> 8) & 0xFF;
                    if ((modifiers & ~1) == 0)
                    {
                        vk = (ushort)(scan & 0xFF);
                        if ((modifiers & 1) != 0)
                            controlState = SHIFT_PRESSED;
                    }
                }
            }

            records.Add(MakeKeyRecord(true, vk, ch, controlState));
            records.Add(MakeKeyRecord(false, vk, ch, controlState));
        }

        return records.ToArray();
    }

    private static INPUT_RECORD MakeKeyRecord(bool keyDown, ushort vk, char ch, uint controlState)
    {
        var record = new INPUT_RECORD { EventType = KEY_EVENT };
        record.Event.bKeyDown = keyDown ? 1 : 0;
        record.Event.wRepeatCount = 1;
        record.Event.wVirtualKeyCode = vk;
        record.Event.wVirtualScanCode = 0;
        record.Event.UnicodeChar = ch;
        record.Event.dwControlKeyState = controlState;
        return record;
    }

    /// <summary>
    /// Fallback paste: post WM_CHAR messages to the console window. conhost stamps
    /// the *live* keyboard state onto records it synthesizes from WM_CHAR, so this
    /// waits for Ctrl/Shift/Alt (still held from Ctrl+V) to be released first —
    /// otherwise spaces arrive as Ctrl+Space, which VT input translates to NUL
    /// and the agent drops them.
    /// </summary>
    private void PasteViaWindowMessages(string text)
    {
        Task.Run(() =>
        {
            if (_consoleHandle == IntPtr.Zero) return;
            if (!NativeMethods.IsWindow(_consoleHandle)) return;

            // Focus the console first so it processes the messages
            try { Dispatcher.Invoke(FocusConsole); } catch { }

            long deadline = Environment.TickCount64 + 2000;
            while (Environment.TickCount64 < deadline &&
                   ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 ||
                    (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 ||
                    (GetAsyncKeyState(VK_MENU) & 0x8000) != 0))
            {
                System.Threading.Thread.Sleep(15);
            }

            // '\n' is posted like any other char: conhost passes the bare LF through,
            // which agent TUIs treat as newline-without-submit. Posted messages keep
            // their order; pace in small batches so conhost's message queue is never
            // flooded (PostMessage silently drops once the queue fills).
            int posted = 0;
            foreach (char c in text)
            {
                PostMessage(_consoleHandle, WM_CHAR, (IntPtr)(ushort)c, IntPtr.Zero);
                if (++posted % 500 == 0)
                    System.Threading.Thread.Sleep(50);
            }
        });
    }

    // ——— Right-click context menu ———

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        ShowContextMenu();
    }

    public void ShowContextMenu()
    {
        if (!_isEmbedded) return;

        var menu = new ContextMenu();
        menu.Foreground = Brushes.White;
        menu.Background = new SolidColorBrush(Color.FromRgb(45, 45, 45));

        var pasteItem = new MenuItem
        {
            Header = "Paste",
            InputGestureText = "Ctrl+V",
            Foreground = Brushes.White,
            FontSize = 12
        };
        pasteItem.Click += (_, _) => PasteFromClipboard();
        menu.Items.Add(pasteItem);

        menu.Items.Add(new Separator());

        var detachItem = new MenuItem
        {
            Header = "Detach to separate window",
            Foreground = Brushes.LightCoral,
            FontSize = 12
        };
        detachItem.Click += (_, _) =>
        {
            // Go through the setter so DetachConsole runs and the shared
            // watchdog/instance bookkeeping stays consistent.
            if (_consoleHandle != IntPtr.Zero && _isEmbedded)
                ConsoleHandle = IntPtr.Zero;
        };
        menu.Items.Add(detachItem);

        this.ContextMenu = menu;
        menu.IsOpen = true;
    }

    // ——— Focus (unchanged) ———

    private void FocusConsole()
    {
        if (_consoleHandle == IntPtr.Zero || !_isEmbedded) return;
        if (!NativeMethods.IsWindow(_consoleHandle)) return;

        try
        {
            int foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            int consoleThread = GetWindowThreadProcessId(_consoleHandle, out _);
            int ourThread = GetCurrentThreadId();

            if (foregroundThread != ourThread && foregroundThread != consoleThread)
            {
                AttachThreadInput(foregroundThread, ourThread, true);
                SetForegroundWindow(_consoleHandle);
                SetFocus(_consoleHandle);
                AttachThreadInput(foregroundThread, ourThread, false);
            }
            else
            {
                SetForegroundWindow(_consoleHandle);
                SetFocus(_consoleHandle);
            }
        }
        catch { }
    }

    // ——— Attach / Detach ———

    private void AttachConsole()
    {
        if (_consoleHandle == IntPtr.Zero || Handle == IntPtr.Zero || _isEmbedded) return;
        DoAttach(Handle);
    }

    private void DoAttach(IntPtr hostHwnd)
    {
        if (_consoleHandle == IntPtr.Zero || _isEmbedded) return;

        NativeMethods.StyleWindowForEmbedding(_consoleHandle);
        NativeMethods.SetParent(_consoleHandle, hostHwnd);

        NativeMethods.ShowWindow(_consoleHandle, NativeMethods.SW_SHOWMAXIMIZED);
        NativeMethods.GetClientRect(hostHwnd, out var rect);
        NativeMethods.MoveWindow(_consoleHandle, 0, 0,
            rect.Right - rect.Left, rect.Bottom - rect.Top, true);

        NativeMethods.SetWindowPos(_consoleHandle, IntPtr.Zero, 0, 0, 0, 0,
            0x0001 | 0x0002 | 0x0010);

        _isEmbedded = true;

        ConfigureConsoleFont();

        NativeMethods.SendMessage(_consoleHandle, 0x0006, IntPtr.Zero, IntPtr.Zero);
        NativeMethods.SendMessage(_consoleHandle, 0x0007, _consoleHandle, IntPtr.Zero);

        _instances.Add(this);
        EnsureKeyboardHookInstalled();
        EnsureFontWatchdogRunning();

        FocusConsole();
    }

    private void DetachConsole()
    {
        if (_consoleHandle == IntPtr.Zero || !_isEmbedded) return;
        _isEmbedded = false;

        _instances.Remove(this);
        StopFontWatchdogIfIdle();
        UninstallKeyboardHookIfIdle();

        if (NativeMethods.IsWindow(_consoleHandle))
            ConsoleWindowManager.DetachEmbeddedWindow(_consoleHandle);
    }

    public void ResizeConsole()
    {
        if (_consoleHandle == IntPtr.Zero || Handle == IntPtr.Zero) return;
        if (!_isEmbedded) return;
        ConsoleWindowManager.ResizeEmbeddedWindow(_consoleHandle, Handle);
    }

    /// <summary>
    /// Sets a fixed font size and a wide buffer on the embedded console so the font
    /// does not recalculate when long lines (e.g. "thinking" output) are printed.
    /// Called once after embedding; the font watchdog re-asserts the font afterwards
    /// because conhost re-derives its font on its own (e.g. on DPI re-evaluation),
    /// which made the font shrink some time after launch.
    /// </summary>
    public void ConfigureConsoleFont()
    {
        if (_processId <= 0) return;

        FreeConsole();
        if (AttachConsole(_processId))
        {
            try
            {
                IntPtr hOut = OpenConsoleOutput();
                if (hOut != IntPtr.Zero && hOut != (IntPtr)(-1))
                {
                    var bufSize = new COORD { X = 120, Y = 9000 };
                    SetConsoleScreenBufferSize(hOut, bufSize);

                    ApplyDesiredFont(hOut);
                    CloseHandle(hOut);
                }
            }
            finally
            {
                FreeConsole();
            }
        }
    }

    private const short DesiredFontHeight = 16;
    private const string DesiredFontFace = "Consolas";

    // One watchdog shared by all embedded consoles: each check runs a process-global
    // FreeConsole/AttachConsole cycle, so a single timer keeps that churn bounded and
    // serialized instead of paying it once per session every second.
    private static System.Windows.Threading.DispatcherTimer? _fontWatchdog;

    /// <summary>
    /// Opens the console's *active* screen buffer. Claude and other TUI agents switch
    /// to the alternate screen buffer, so the std handle captured at attach time may
    /// not be the buffer currently displayed.
    /// </summary>
    private static IntPtr OpenConsoleOutput()
    {
        return CreateFile("CONOUT$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
    }

    private static void ApplyDesiredFont(IntPtr hOut)
    {
        var fontInfo = new CONSOLE_FONT_INFO_EX();
        fontInfo.cbSize = (uint)Marshal.SizeOf<CONSOLE_FONT_INFO_EX>();
        GetCurrentConsoleFontEx(hOut, false, ref fontInfo);

        fontInfo.dwFontSizeX = 0; // let conhost pick the width for the height
        fontInfo.dwFontSizeY = DesiredFontHeight;
        fontInfo.FaceName = DesiredFontFace;
        SetCurrentConsoleFontEx(hOut, false, ref fontInfo);
    }

    private static void EnsureFontWatchdogRunning()
    {
        if (_fontWatchdog != null) return;

        _fontWatchdog = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _fontWatchdog.Tick += (_, _) =>
        {
            foreach (var host in _instances.ToArray())
            {
                if (host._isEmbedded && host._processId > 0)
                    host.EnforceConsoleFont();
            }
        };
        _fontWatchdog.Start();
    }

    private static void StopFontWatchdogIfIdle()
    {
        if (_instances.Count != 0) return;
        _fontWatchdog?.Stop();
        _fontWatchdog = null;
    }

    /// <summary>
    /// Re-applies the desired console font if conhost changed it (it recalculates
    /// the font on DPI/window-state changes, shrinking the text). Cheap when the
    /// font is still correct: one attach + one font query.
    /// </summary>
    private void EnforceConsoleFont()
    {
        try
        {
            FreeConsole();
            if (!AttachConsole(_processId))
            {
                // Process is gone; the exit monitor will detach us shortly.
                return;
            }

            try
            {
                IntPtr hOut = OpenConsoleOutput();
                if (hOut == IntPtr.Zero || hOut == (IntPtr)(-1)) return;

                var current = new CONSOLE_FONT_INFO_EX();
                current.cbSize = (uint)Marshal.SizeOf<CONSOLE_FONT_INFO_EX>();
                bool ok = GetCurrentConsoleFontEx(hOut, false, ref current);

                if (!ok ||
                    current.dwFontSizeY != DesiredFontHeight ||
                    !string.Equals(current.FaceName, DesiredFontFace, StringComparison.OrdinalIgnoreCase))
                {
                    ApplyDesiredFont(hOut);
                }

                CloseHandle(hOut);
            }
            finally
            {
                FreeConsole();
            }
        }
        catch { }
    }

    // ——— Win32 ———

    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_CLIPCHILDREN = 0x02000000;

    #region P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public NativeMethods.RECT rcCaret;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct KEY_EVENT_RECORD
    {
        public int bKeyDown;          // BOOL (4 bytes in Windows API)
        public ushort wRepeatCount;   // WORD
        public ushort wVirtualKeyCode; // WORD
        public ushort wVirtualScanCode; // WORD
        public char UnicodeChar;       // WCHAR
        public uint dwControlKeyState; // DWORD
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct INPUT_RECORD
    {
        public ushort EventType;
        private ushort _padding; // align to 4 bytes
        public KEY_EVENT_RECORD Event;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CONSOLE_FONT_INFO_EX
    {
        public uint cbSize;
        public uint nFont;
        public short dwFontSizeX;
        public short dwFontSizeY;
        public uint FontFamily;
        public uint FontWeight;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    private const ushort KEY_EVENT = 1;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsoleInput(IntPtr hConsoleInput, INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleScreenBufferSize(IntPtr hConsoleOutput, COORD dwSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCurrentConsoleFontEx(IntPtr hConsoleOutput, bool bMaximumWindow, ref CONSOLE_FONT_INFO_EX lpConsoleCurrentFontEx);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCurrentConsoleFontEx(IntPtr hConsoleOutput, bool bMaximumWindow, ref CONSOLE_FONT_INFO_EX lpConsoleCurrentFontEx);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(int idAttach, int idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    private static int GetWindowThreadProcessId(IntPtr hWnd, out int processId)
    {
        return NativeMethods.GetWindowThreadProcessId(hWnd, out processId);
    }

    #endregion
}
