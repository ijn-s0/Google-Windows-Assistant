using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GeminiLauncher
{
    /// <summary>
    /// Low-level keyboard + mouse hooks on a dedicated thread (so UI work never delays input).
    ///  - Lone tap of the Windows key  -> open launcher, native Start suppressed.
    ///    Win+E / Win+D / Win+Shift+S etc. still work: we only act if no other key was pressed while Win was held.
    ///  - Left click on the taskbar Start button -> open launcher instead of Start.
    ///    Shift+click (or Ctrl+Esc) still opens the native Start menu.
    /// </summary>
    public sealed class InputHook : IDisposable
    {
        public event Action Triggered;
        /// <summary>Raised (on a pool thread) for a mouse click on a window that isn't ours, while <see cref="WatchClicks"/> is on.</summary>
        public event Action ClickedOutside;
        public volatile bool WatchClicks;
        static readonly uint MyPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        public volatile bool WinKeyEnabled = true;
        public volatile bool StartButtonEnabled = true;

        Thread _thread;
        uint _threadId;
        IntPtr _kbHook, _msHook;
        Native.LowLevelKeyboardProc _kbProc, _msProc; // keep delegates alive
        bool _winDown, _otherPressed, _swallowUp;
        int _winVk;
        long _winDownTicks;
        volatile Native.RECT[] _startRects = new Native.RECT[0];
        volatile Native.RECT[] _trayRects = new Native.RECT[0];
        int _rectsAt = int.MinValue / 2, _lastMoveCheck, _refreshing;
        Timer _refreshTimer;
        const int WM_MOUSEMOVE = 0x0200;

        public void Start()
        {
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = Native.GetCurrentThreadId();
                _kbProc = KeyboardProc;
                _msProc = MouseProc;
                var mod = Native.GetModuleHandle(null);
                _kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, mod, 0);
                _msHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _msProc, mod, 0);
                ready.Set();
                while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { }
                if (_kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_kbHook);
                if (_msHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_msHook);
            })
            { IsBackground = true, Name = "InputHook", Priority = ThreadPriority.Highest };
            _thread.Start();
            ready.Wait(3000);

            // Locate the Start button(s) now and every 20 s (taskbar can move / monitors change).
            _refreshTimer = new Timer(_ => RefreshStartRects(), null, 0, 30000);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(1500); RefreshStartRects(); });
        }

        void Fire() => ThreadPool.QueueUserWorkItem(_ => Triggered?.Invoke());

        IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                int msg = wParam.ToInt32();
                bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
                bool injected = (k.flags & Native.LLKHF_INJECTED) != 0;
                int vk = (int)k.vkCode;
                bool isWin = vk == Native.VK_LWIN || vk == Native.VK_RWIN;

                if (!injected)
                {
                    if (isWin && down)
                    {
                        if (!_winDown) { _winDown = true; _otherPressed = false; _winVk = vk; _winDownTicks = Environment.TickCount; }
                    }
                    else if (isWin && up)
                    {
                        bool lone = _winDown && !_otherPressed && vk == _winVk;
                        long held = Environment.TickCount - _winDownTicks;
                        _winDown = false;
                        if (lone && WinKeyEnabled && held < 1500)
                        {
                            // Swallow the real Win-up, then inject mask-down, mask-up, Win-up:
                            // Windows sees "Win + <unassigned key>" and does not open Start.
                            SendMaskedWinUp(vk);
                            Fire();
                            return (IntPtr)1;
                        }
                    }
                    else if (down && _winDown)
                    {
                        _otherPressed = true;
                    }
                }
            }
            return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_MOUSEMOVE && StartButtonEnabled)
                {
                    // When the pointer is over a taskbar, keep the Start button position fresh
                    // (Windows 11 centered taskbars shift as apps open/close).
                    int now = Environment.TickCount;
                    if (now - _lastMoveCheck > 120)
                    {
                        _lastMoveCheck = now;
                        if (now - _rectsAt > 1000)
                        {
                            var m = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                            foreach (var r in _trayRects)
                                if (m.pt.x >= r.left && m.pt.x < r.right && m.pt.y >= r.top && m.pt.y < r.bottom)
                                { QueueRefresh(); break; }
                        }
                    }
                }
                else if (msg == Native.WM_LBUTTONDOWN && StartButtonEnabled)
                {
                    var m = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                    bool injected = (m.flags & 1) != 0;
                    bool shift = (Native.GetAsyncKeyState(Native.VK_SHIFT) & 0x8000) != 0;
                    bool fresh = Environment.TickCount - _rectsAt < 4000;
                    if (!fresh) QueueRefresh();
                    if (!injected && !shift && fresh && IsOnStartButton(m.pt))
                    {
                        _swallowUp = true;
                        Fire();
                        return (IntPtr)1;
                    }
                }
                else if (msg == Native.WM_LBUTTONUP && _swallowUp)
                {
                    _swallowUp = false;
                    return (IntPtr)1;
                }
                // Any button press outside the launcher (and its menus/dialogs) closes it, like Start.
                // Works even when Windows didn't hand us activation, so the Deactivated event never comes.
                if (WatchClicks && (msg == Native.WM_LBUTTONDOWN || msg == 0x0204 /*R*/ || msg == 0x0207 /*M*/ || msg == 0x020B /*X*/))
                {
                    var m = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                    var root = Native.GetAncestor(Native.WindowFromPoint(m.pt), 2 /*GA_ROOT*/);
                    Native.GetWindowThreadProcessId(root, out uint pid);
                    if (pid != MyPid) ThreadPool.QueueUserWorkItem(_ => ClickedOutside?.Invoke());
                }
            }
            return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);
        }

        bool IsOnStartButton(Native.POINT pt)
        {
            bool inRect = false;
            foreach (var r in _startRects)
                if (pt.x >= r.left && pt.x < r.right && pt.y >= r.top && pt.y < r.bottom) { inRect = true; break; }
            if (!inRect) return false;
            // Make sure the taskbar is actually what's under the cursor (not a full-screen app).
            var root = Native.GetAncestor(Native.WindowFromPoint(pt), 2 /*GA_ROOT*/);
            var cls = new StringBuilder(64);
            Native.GetClassName(root, cls, cls.Capacity);
            var c = cls.ToString();
            return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd";
        }

        void QueueRefresh()
        {
            if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ => { try { RefreshStartRects(); } finally { Interlocked.Exchange(ref _refreshing, 0); } });
        }

        /// <summary>Finds Start buttons on every taskbar (Win10: "Start" child window; Win11: UI Automation "StartButton").</summary>
        void RefreshStartRects()
        {
            var rects = new List<Native.RECT>();
            var trayRects = new List<Native.RECT>();
            try
            {
                var trays = new List<IntPtr>();
                var main = Native.FindWindow("Shell_TrayWnd", null);
                if (main != IntPtr.Zero) trays.Add(main);
                IntPtr sec = IntPtr.Zero;
                while ((sec = Native.FindWindowEx(IntPtr.Zero, sec, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) trays.Add(sec);

                foreach (var tray in trays)
                {
                    if (Native.GetWindowRect(tray, out var tr)) trayRects.Add(tr);
                    bool found = false;
                    var start = Native.FindWindowEx(tray, IntPtr.Zero, "Start", null);
                    if (start != IntPtr.Zero && Native.IsWindowVisible(start) && Native.GetWindowRect(start, out var wr) && wr.right - wr.left > 4)
                    {
                        rects.Add(wr); found = true;
                    }
                    if (!found)
                    {
                        try
                        {
                            var el = System.Windows.Automation.AutomationElement.FromHandle(tray);
                            var btn = el?.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.AutomationIdProperty, "StartButton"));
                            if (btn != null)
                            {
                                var b = btn.Current.BoundingRectangle;
                                if (!b.IsEmpty && b.Width > 4)
                                    rects.Add(new Native.RECT { left = (int)b.Left, top = (int)b.Top, right = (int)b.Right, bottom = (int)b.Bottom });
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            _startRects = rects.ToArray();
            _trayRects = trayRects.ToArray();
            _rectsAt = Environment.TickCount;
        }

        static void SendMaskedWinUp(int winVk)
        {
            var inputs = new Native.INPUT[3];
            inputs[0].type = Native.INPUT_KEYBOARD;
            inputs[0].U.ki.wVk = Native.VK_MASK;
            inputs[1].type = Native.INPUT_KEYBOARD;
            inputs[1].U.ki.wVk = Native.VK_MASK;
            inputs[1].U.ki.dwFlags = Native.KEYEVENTF_KEYUP;
            inputs[2].type = Native.INPUT_KEYBOARD;
            inputs[2].U.ki.wVk = (ushort)winVk;
            inputs[2].U.ki.dwFlags = Native.KEYEVENTF_KEYUP | Native.KEYEVENTF_EXTENDEDKEY;
            Native.SendInput(3, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        public void Dispose()
        {
            _refreshTimer?.Dispose();
            if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
