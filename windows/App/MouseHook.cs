using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DropDock.Core;

namespace DropDock.App
{
    /// <summary>
    /// Watches the pointer system-wide with a low-level mouse hook. Windows calls it only when the mouse
    /// actually moves or clicks, and each call is a few comparisons, so it costs nothing while idle.
    /// </summary>
    sealed class MouseHook : IDisposable
    {
        readonly DropBar bar;
        readonly Native.LowLevelMouseProc proc;
        readonly Timer dwell = new Timer { Interval = 220 };
        IntPtr handle;
        bool down, fired, dwellIsDrag;
        int downX, downY, restX;
        Rectangle[] screens = new Rectangle[0];

        public MouseHook(DropBar bar)
        {
            this.bar = bar;
            proc = Callback;
            dwell.Tick += DwellTick;
        }

        public void Start()
        {
            RefreshScreens();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => RefreshScreens();
            handle = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
            if (handle == IntPtr.Zero) Log.Info("mouse hook failed: " + Marshal.GetLastWin32Error());
        }

        void RefreshScreens()
        {
            var all = Screen.AllScreens;
            screens = new Rectangle[all.Length];
            for (int i = 0; i < all.Length; i++) screens[i] = all[i].Bounds;
        }

        IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                int x = Marshal.ReadInt32(lParam), y = Marshal.ReadInt32(lParam, 4);
                if (msg == Native.WM_MOUSEMOVE) OnMove(x, y);
                else if (msg == Native.WM_LBUTTONDOWN)
                {
                    if (!dwellIsDrag) dwell.Stop();
                    down = true;
                    fired = false;
                    downX = x;
                    downY = y;
                    bar.GlobalMouseDown(new Point(x, y));
                }
                else if (msg == Native.WM_LBUTTONUP) down = false;
            }
            return Native.CallNextHookEx(handle, nCode, wParam, lParam);
        }

        void OnMove(int x, int y)
        {
            if (bar.Visible) { dwell.Stop(); return; }
            Rectangle screen;
            if (!AtTopEdge(x, y, out screen))
            {
                if (dwell.Enabled) dwell.Stop();
                return;
            }
            if (down)
            {
                if (fired || Math.Abs(x - downX) + Math.Abs(y - downY) < 12) return;
                uint thread;
                var info = ForegroundInfo(out thread);
                if ((info.flags & Native.GUI_INMOVESIZE) != 0) return; // dragging a window, not a file
                var capture = info.hwndCapture == IntPtr.Zero ? "" : Native.ClassName(info.hwndCapture);
                if (capture == "CLIPBRDWNDCLASS" || capture.IndexOf("Drag", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    fired = true;
                    var p = new Point(x, y);
                    bar.BeginInvoke((Action)(() => bar.Reveal(DropBar.Reason.Drag, p)));
                }
                else if (!dwell.Enabled)
                {
                    // Not sure it is a file drag: only open if the pointer stays at the edge for a moment.
                    dwellIsDrag = true;
                    dwell.Interval = 220;
                    dwell.Start();
                }
            }
            else if (Settings.Shared.HoverReveal)
            {
                // Resting anywhere on the top edge opens the bar. Sliding along it restarts the wait.
                if (dwell.Enabled && !dwellIsDrag && Math.Abs(x - restX) < 6) return;
                restX = x;
                dwellIsDrag = false;
                dwell.Stop();
                dwell.Interval = 300;
                dwell.Start();
            }
        }

        void DwellTick(object sender, EventArgs e)
        {
            dwell.Stop();
            var p = Cursor.Position;
            Rectangle screen;
            if (bar.Visible || !AtTopEdge(p.X, p.Y, out screen)) return;
            if (dwellIsDrag)
            {
                if ((Control.MouseButtons & MouseButtons.Left) != 0) bar.Reveal(DropBar.Reason.Drag, p);
            }
            else if (Math.Abs(p.X - restX) < 10 && Control.MouseButtons == MouseButtons.None)
            {
                bar.Reveal(DropBar.Reason.Hover, p);
            }
        }

        static Native.GUITHREADINFO ForegroundInfo(out uint thread)
        {
            var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
            thread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), IntPtr.Zero);
            Native.GetGUIThreadInfo(thread, ref info);
            return info;
        }

        /// <summary>True at the top row of a screen that has no other screen above it.</summary>
        bool AtTopEdge(int x, int y, out Rectangle screen)
        {
            screen = Rectangle.Empty;
            foreach (var r in screens)
            {
                if (x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom) { screen = r; break; }
            }
            if (screen.IsEmpty || y > screen.Top + 1) return false;
            foreach (var r in screens)
                if (x >= r.Left && x < r.Right && screen.Top - 3 >= r.Top && screen.Top - 3 < r.Bottom) return false;
            return true;
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero) Native.UnhookWindowsHookEx(handle);
            handle = IntPtr.Zero;
            dwell.Dispose();
        }
    }
}
