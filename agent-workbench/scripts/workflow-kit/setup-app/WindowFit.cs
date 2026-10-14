using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClaudeWorkflowSetup
{
    // A window without system frame maximizes over the whole monitor, taskbar included. Windows asks
    // for the maximized size with WM_GETMINMAXINFO; answering with the work area of the monitor the
    // window is on keeps the taskbar visible, also on a second screen.
    static class WindowFit
    {
        const int WM_GETMINMAXINFO = 0x0024;
        const int MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        class MONITORINFO
        {
            public int Size = Marshal.SizeOf(typeof(MONITORINFO));
            public RECT Monitor;
            public RECT Work;
            public int Flags;
        }

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern bool GetMonitorInfo(IntPtr monitor, MONITORINFO info);

        public static void Attach(Window window) =>
            window.SourceInitialized += (s, e) =>
                HwndSource.FromHwnd(new WindowInteropHelper(window).Handle).AddHook(Hook);

        static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO();
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, info)) return IntPtr.Zero;
            var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
            mmi.MaxPosition.X = info.Work.Left - info.Monitor.Left;
            mmi.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
            mmi.MaxSize.X = info.Work.Right - info.Work.Left;
            mmi.MaxSize.Y = info.Work.Bottom - info.Work.Top;
            Marshal.StructureToPtr(mmi, lParam, true);
            return IntPtr.Zero;
        }
    }
}
