using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private int _consoleThreadId; // thread owning the console window, for AttachThreadInput

    // Serializes every kernel32 FreeConsole/AttachConsole cycle in this process
    // (paste workers, font watchdog, font configuration) — those calls are
    // process-global, so an unsynchronized cycle could attach to the wrong
    // console. Paste runs on a worker thread behind this lock; UI-thread font
    // work uses Monitor.TryEnter so it never blocks the UI on a long paste.
    private static readonly object _consoleStateLock = new();

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

    // The embedded console that was last told it is active. Switching between
    // embedded consoles moves the real focus but never generates WM_ACTIVATE
    // (the top-level window is already active), so we deliver those notices
    // ourselves — conhost only processes keyboard input while it believes it
    // is the active console.
    private static IntPtr _lastActivatedConsole = IntPtr.Zero;

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
        const int WM_PARENTNOTIFY = 0x0210;
        if (msg == WM_SETFOCUS)
        {
            FocusConsole();
        }
        else if (msg == WM_PARENTNOTIFY)
        {
            // A mouse button was pressed over a child window — the embedded
            // console. Windows does not move focus to cross-thread children on
            // its own (and console threads don't answer GetGUIThreadInfo), so
            // this notification is the parent's chance to focus the console
            // the user clicked. Replaces the old global mouse hook.
            int lo = unchecked((short)((long)wParam & 0xFFFF));
            if (lo is 0x0201 or 0x0202 or 0x0204 or 0x0205 ||   // L/R button down
                lo is 0x0207 or 0x0208 or 0x020B or 0x020C)     // middle/X button down
            {
                FocusConsole();
            }
        }
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

        // The foreground thread reports no focus window: the focus may sit on an
        // embedded console. Console window threads are special — GetGUIThreadInfo
        // does not answer for them — but our thread shares an input queue with
        // every embedded console (see DoAttach), so GetFocus() reports the shared
        // focus state directly. Only consoles inside the foreground window count.
        IntPtr focused = GetFocus();
        if (focused == IntPtr.Zero) return null;
        foreach (var host in _instances)
        {
            IntPtr console = host._consoleHandle;
            if (!host._isEmbedded || console == IntPtr.Zero || !NativeMethods.IsWindow(console))
                continue;
            if (GetAncestor(console, GA_ROOT) != foreground)
                continue;
            if (focused == console || IsChild(console, focused))
                return host;
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
    /// Pastes the clipboard into the console's input buffer using WriteConsoleInput.
    /// This is the correct way to inject input into any console application.
    /// Text is injected as key records; an image is staged as a PNG file and its
    /// path is injected as a bracketed paste (see PasteImageFromClipboard).
    /// </summary>
    public void PasteFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (string.IsNullOrEmpty(text)) return;

                // Clipboard access must stay on this (STA UI) thread; everything else
                // below runs on a worker so a long paste never freezes the window.
                Task.Run(() => PasteText(text));
                return;
            }

            PasteImageFromClipboard();
        }
        catch { }
    }

    /// <summary>
    /// Image paste. opencode's TUI attaches a clipboard image when it receives a
    /// paste whose text is a path to an image file (resolvePastedAttachments), the
    /// same way it does for a drag-and-dropped file. So stage the clipboard bitmap
    /// as a PNG under %TEMP%\MultiAI and paste the path wrapped in the bracketed
    /// paste markers a terminal sends around a real paste (ESC[200~ ... ESC[201~);
    /// opencode then turns it into an [Image #N] attachment. Any other agent still
    /// sees the path as plain text. Text keeps priority: an image is only pasted
    /// when the clipboard holds no text at all (screenshots, copied pictures).
    /// </summary>
    private void PasteImageFromClipboard()
    {
        // Clipboard reads must stay on the UI (STA) thread; the PNG encode, disk
        // write, and injection run on the paste worker like any other paste.
        byte[]? pngBytes = TryReadPngStreamFromClipboard();
        ClipboardPixels? pixels = pngBytes == null ? TryReadClipboardPixels() : null;
        if (pngBytes == null && pixels == null) return;

        Task.Run(() =>
        {
            string? path = WriteStagedImage(pngBytes, pixels);
            if (path == null) return;
            PasteText(BracketedPaste(path), allowEscape: true);
        });
    }

    private static string BracketedPaste(string text) => "\x1b[200~" + text + "\x1b[201~";

    // ——— Clipboard image staging ———

    private sealed record ClipboardPixels(
        int Width, int Height, double DpiX, double DpiY, PixelFormat Format, byte[] Pixels, int Stride);

    /// <summary>
    /// Reads a verbatim PNG stream off the clipboard when one is present (the
    /// Snipping Tool and browsers put a "PNG" format there). Verified against the
    /// PNG signature so a mislabeled stream can never be written as a .png file.
    /// Runs on the UI (STA) thread.
    /// </summary>
    private static byte[]? TryReadPngStreamFromClipboard()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return null;

            foreach (string format in new[] { "PNG", "image/png" })
            {
                if (!data.GetDataPresent(format)) continue;
                if (data.GetData(format) is not MemoryStream stream) continue;

                byte[] bytes = stream.ToArray();
                // PNG signature: 0x89 'P' 'N' 'G' \r \n \x1a \n
                if (bytes.Length > 8 &&
                    bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
                    bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
                {
                    return bytes;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Copies the clipboard bitmap into raw pixels on the UI (STA) thread, in a
    /// form that can be re-created on a worker thread without touching WPF's
    /// thread-affine BitmapSource. Runs on the UI (STA) thread.
    /// </summary>
    private static ClipboardPixels? TryReadClipboardPixels()
    {
        try
        {
            if (!Clipboard.ContainsImage()) return null;
            var source = Clipboard.GetImage();
            if (source == null || source.PixelWidth == 0 || source.PixelHeight == 0) return null;

            // Palettized or 16-bit sources: normalize to Bgra32 so CopyPixels
            // yields plain 8-bit-per-channel pixels with no palette to carry over.
            if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Bgr32)
            {
                var converted = new FormatConvertedBitmap();
                converted.BeginInit();
                converted.Source = source;
                converted.DestinationFormat = PixelFormats.Bgra32;
                converted.EndInit();
                source = converted;
            }

            int stride = (source.PixelWidth * source.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[stride * (long)source.PixelHeight];
            source.CopyPixels(pixels, stride, 0);
            return new ClipboardPixels(source.PixelWidth, source.PixelHeight,
                source.DpiX, source.DpiY, source.Format, pixels, stride);
        }
        catch { return null; }
    }

    private static readonly string StagedImageDir =
        Path.Combine(Path.GetTempPath(), "MultiAI");

    /// <summary>
    /// Writes the staged image to disk and returns its path. A clipboard PNG
    /// stream is written byte-for-byte; raw pixels are re-encoded as PNG on the
    /// worker thread. Also prunes staged files older than a day. Runs on a worker.
    /// </summary>
    private static string? WriteStagedImage(byte[]? pngBytes, ClipboardPixels? pixels)
    {
        try
        {
            if (pngBytes == null && pixels == null) return null;
            Directory.CreateDirectory(StagedImageDir);
            CleanupStaleStagedImages();

            string file = Path.Combine(StagedImageDir, "paste-"
                + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")
                + "-" + Guid.NewGuid().ToString("N")[..6] + ".png");

            if (pngBytes != null)
            {
                File.WriteAllBytes(file, pngBytes);
                return file;
            }

            double dpiX = pixels!.DpiX > 0 ? pixels.DpiX : 96;
            double dpiY = pixels.DpiY > 0 ? pixels.DpiY : 96;
            var source = BitmapSource.Create(pixels.Width, pixels.Height, dpiX, dpiY,
                pixels.Format, null, pixels.Pixels, pixels.Stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var stream = File.Create(file))
                encoder.Save(stream);
            return file;
        }
        catch { return null; }
    }

    private static void CleanupStaleStagedImages()
    {
        try
        {
            DateTime cutoff = DateTime.Now - TimeSpan.FromHours(24);
            foreach (string file in Directory.EnumerateFiles(StagedImageDir, "paste-*.png"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                        File.Delete(file);
                }
                catch { /* file locked or gone — leave it */ }
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Injects text into the console. The primary path writes key records straight
    /// into the console's input buffer (WriteConsoleInput); the fallback posts
    /// WM_CHAR messages to the console window.
    /// <paramref name="allowEscape"/> lets ESC characters through (used for image
    /// pastes, which carry their own bracketed-paste markers); without it control
    /// characters are stripped as for any plain text paste.
    /// </summary>
    public void PasteText(string text, bool allowEscape = false)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Normalize all line endings to \n. It is injected as a bare LF (the
        // Ctrl+J / Ctrl+Enter character), which agent TUIs treat as "insert a
        // newline" rather than "submit the prompt".
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // Method 1: WriteConsoleInput (injects directly into console input buffer)
        if (_processId > 0 && PasteViaConsoleInput(text, allowEscape))
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
    /// Runs on a worker thread. AttachConsole/FreeConsole is process-global state
    /// also cycled by the font watchdog, so every cycle takes _consoleStateLock;
    /// UI-thread callers use Monitor.TryEnter and skip when a paste holds it.
    /// </summary>
    private bool PasteViaConsoleInput(string text, bool allowEscape = false)
    {
        lock (_consoleStateLock)
        {
            return PasteViaConsoleInputCore(text, allowEscape);
        }
    }

    private bool PasteViaConsoleInputCore(string text, bool allowEscape)
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
                    var records = BuildKeyRecords(text, allowEscape);
                    if (records.Length == 0)
                        return true; // nothing printable to paste

                    // Pin the array and hand WriteConsoleInput a raw pointer: the
                    // default marshaler copies struct-by-struct, which for long
                    // pastes costs more than everything else combined. The struct
                    // is blittable, so conhost reads the managed memory directly.
                    var pin = GCHandle.Alloc(records, GCHandleType.Pinned);
                    try
                    {
                        // Large but bounded chunks; with the pointer overload a
                        // chunk is just a memcpy on conhost's side.
                        const int chunkSize = 16384;
                        bool anyWritten = false;
                        for (int offset = 0; offset < records.Length;)
                        {
                            int count = Math.Min(chunkSize, records.Length - offset);
                            IntPtr chunkPtr = Marshal.UnsafeAddrOfPinnedArrayElement(records, offset);

                            if (!WriteConsoleInput(hIn, chunkPtr, (uint)count, out uint written) ||
                                written == 0 || written > (uint)count)
                            {
                                // Once anything was written, report success anyway so the
                                // fallback doesn't paste the whole text a second time.
                                return anyWritten;
                            }
                            anyWritten = true;
                            offset += (int)written; // honor partial writes
                        }
                        return true;
                    }
                    finally
                    {
                        if (pin.IsAllocated) pin.Free();
                    }
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
    /// Control characters are stripped, except newline and tab — and ESC when
    /// <paramref name="allowEscape"/> is set, for pastes that carry their own
    /// bracketed-paste markers (image pastes).
    /// </summary>
    private static INPUT_RECORD[] BuildKeyRecords(string text, bool allowEscape = false)
    {
        var records = new List<INPUT_RECORD>(text.Length * 2);

        foreach (char ch in text)
        {
            // Strip control characters except newline and tab (and ESC when the
            // paste brings its own markers); they only confuse TUIs.
            if ((ch < 0x20 && ch != '\n' && ch != '\t' && !(allowEscape && ch == '\x1B')) || ch == (char)0x7F)
                continue;

            ushort vk = 0;
            uint controlState = 0;

            // '\n' keeps VK 0 so cooked-mode readers don't treat it as Enter.
            if (ch != '\n')
            {
                short scan = GetVkScanCached(ch);
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

    // VkKeyScanW is one P/Invoke per call; without a cache a long paste pays it
    // once per character. Keyed by the keyboard layout so switching layouts
    // invalidates the cache naturally (HKLs are distinct per layout).
    private static readonly object _vkCacheLock = new();
    private static IntPtr _vkCacheLayout = IntPtr.Zero;
    private static readonly Dictionary<char, short> _vkScanCache = new();

    private static short GetVkScanCached(char ch)
    {
        IntPtr layout = GetKeyboardLayout(0);
        lock (_vkCacheLock)
        {
            if (_vkCacheLayout != layout)
            {
                _vkCacheLayout = layout;
                _vkScanCache.Clear();
            }
            if (_vkScanCache.TryGetValue(ch, out short cached))
                return cached;
        }

        short scan = VkKeyScanW(ch);

        lock (_vkCacheLock)
        {
            _vkCacheLayout = layout;
            _vkScanCache[ch] = scan;
        }
        return scan;
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
            // their order. No fixed pacing — instead, retry when PostMessage fails
            // (the console message queue is full) so fast pastes never sleep and
            // flooded queues never silently drop characters.
            foreach (char c in text)
            {
                for (int retry = 0; ; retry++)
                {
                    if (PostMessage(_consoleHandle, WM_CHAR, (IntPtr)(ushort)c, IntPtr.Zero) != IntPtr.Zero)
                        break;
                    if (retry >= 200 || !NativeMethods.IsWindow(_consoleHandle))
                        return; // queue permanently full or window gone
                    System.Threading.Thread.Sleep(5);
                }
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
            // SetFocus on the console window is legal because our thread stays
            // attached to the console thread while embedded (see DoAttach).
            // SetForegroundWindow on the child brings its root — our main
            // window — to the foreground and focuses the console itself.
            SetForegroundWindow(_consoleHandle);
            SetFocus(_consoleHandle);

            // Deliver the activation notice conhost needs to process input.
            // WA_ACTIVE = 1 for this console, WA_INACTIVE = 0 for the previous.
            if (_lastActivatedConsole != IntPtr.Zero &&
                _lastActivatedConsole != _consoleHandle &&
                NativeMethods.IsWindow(_lastActivatedConsole))
            {
                NativeMethods.SendMessage(_lastActivatedConsole, NativeMethods.WM_ACTIVATE,
                    IntPtr.Zero, IntPtr.Zero);
            }
            NativeMethods.SendMessage(_consoleHandle, NativeMethods.WM_ACTIVATE,
                (IntPtr)1, IntPtr.Zero);
            _lastActivatedConsole = _consoleHandle;
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

        // Attach our input queue to the console's thread for the whole embedding.
        // Without this, a click on the (cross-process) console child cannot move
        // keyboard focus to it — focus changes between unattached threads are
        // refused — so after switching between two embedded consoles neither
        // would accept input. While the queues are attached, plain clicks and
        // SetFocus both work, for every embedded console at once.
        _consoleThreadId = GetWindowThreadProcessId(_consoleHandle, out _);
        if (_consoleThreadId != 0 && _consoleThreadId != GetCurrentThreadId())
            AttachThreadInput(GetCurrentThreadId(), _consoleThreadId, true);

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

        // Undo the input-queue attachment before the console leaves our window.
        if (_consoleThreadId != 0 && _consoleThreadId != GetCurrentThreadId())
        {
            try { AttachThreadInput(GetCurrentThreadId(), _consoleThreadId, false); }
            catch { }
            _consoleThreadId = 0;
        }

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

        // Runs on the UI thread (from DoAttach). Never block on a paste in
        // flight: skipping is safe because the font watchdog re-asserts shortly.
        if (!Monitor.TryEnter(_consoleStateLock, 50)) return;
        try
        {
            ConfigureConsoleFontCore();
        }
        finally
        {
            Monitor.Exit(_consoleStateLock);
        }
    }

    private void ConfigureConsoleFontCore()
    {
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
        // Runs on the UI thread (font watchdog tick). Skip rather than block
        // while a paste holds the console-state lock; the next tick re-checks.
        if (!Monitor.TryEnter(_consoleStateLock, 50)) return;
        try
        {
            EnforceConsoleFontCore();
        }
        finally
        {
            Monitor.Exit(_consoleStateLock);
        }
    }

    private void EnforceConsoleFontCore()
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsoleInput(IntPtr hConsoleInput, IntPtr lpBuffer, uint nLength, out uint lpNumberOfEventsWritten);

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
    private static extern IntPtr GetFocus();

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
