using System;
using System.Runtime.InteropServices;

namespace BoxeZone.Helpers
{
    internal static class Win32
    {
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

        /// <summary>
        /// Returns how far the window rect extends beyond the visible frame on each side
        /// (Windows 11 adds invisible resize borders around the frame).
        /// </summary>
        public static RECT GetInvisibleFrameInsets(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out RECT outer) ||
                DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT visible, Marshal.SizeOf<RECT>()) != 0)
            {
                return default;
            }

            return new RECT
            {
                Left = visible.Left - outer.Left,
                Top = visible.Top - outer.Top,
                Right = outer.Right - visible.Right,
                Bottom = outer.Bottom - visible.Bottom,
            };
        }
    }
}
