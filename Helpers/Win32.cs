using System;
using System.Runtime.InteropServices;

namespace BoxeZone.Helpers
{
    internal static class Win32
    {
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const uint WM_GETMINMAXINFO = 0x0024;
        private const uint WM_WINDOWPOSCHANGING = 0x0046;
        private const uint SWP_NOSIZE = 0x0001;
        private const int MinTrackSizeOffset = 24; // MINMAXINFO.ptMinTrackSize
        private const int GWL_EXSTYLE = -20;
        private const int GWLP_HWNDPARENT = -8;
        private const long WS_EX_NOACTIVATE = 0x08000000;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        /// <summary>Physical state of the primary mouse button, honoring swapped buttons.</summary>
        public static bool IsPrimaryMouseButtonDown()
        {
            const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, SM_SWAPBUTTON = 23;
            int key = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON;
            return (GetAsyncKeyState(key) & 0x8000) != 0;
        }

        // Kept in a field so the GC does not collect the delegate while Windows still calls it.
        private static readonly SubclassProc MinimumSizeProc = RemoveMinimumSizeProc;

        public delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr idSubclass, UIntPtr refData);

        [DllImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr idSubclass, UIntPtr refData);

        [DllImport("comctl32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr idSubclass);

        [DllImport("comctl32.dll")]
        public static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllowSetForegroundWindow(uint processId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);

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

        /// <summary>
        /// Lets the window be resized below the system minimum (about 136 px wide for a window
        /// with a border). OverlappedPresenter.PreferredMinimumWidth cannot go below it.
        /// </summary>
        public static void RemoveMinimumSize(IntPtr hwnd) => SetWindowSubclass(hwnd, MinimumSizeProc, (UIntPtr)(++_minimumSizeLayer), 0);

        private static uint _minimumSizeLayer;

        private static IntPtr RemoveMinimumSizeProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr idSubclass, UIntPtr refData)
        {
            if (msg == WM_WINDOWPOSCHANGING)
            {
                // WINDOWPOS: hwnd, hwndInsertAfter, x, y, cx, cy, flags.
                int cxOffset = (2 * IntPtr.Size) + 8;
                int requestedWidth = Marshal.ReadInt32(lParam, cxOffset);
                int requestedHeight = Marshal.ReadInt32(lParam, cxOffset + 4);
                uint flags = (uint)Marshal.ReadInt32(lParam, cxOffset + 8);

                IntPtr changingResult = DefSubclassProc(hwnd, msg, wParam, lParam);
                if ((flags & SWP_NOSIZE) == 0)
                {
                    Marshal.WriteInt32(lParam, cxOffset, requestedWidth);
                    Marshal.WriteInt32(lParam, cxOffset + 4, requestedHeight);
                }

                return changingResult;
            }

            IntPtr result = DefSubclassProc(hwnd, msg, wParam, lParam);
            if (msg == WM_GETMINMAXINFO)
            {
                Marshal.WriteInt32(lParam, MinTrackSizeOffset, 1);
                Marshal.WriteInt32(lParam, MinTrackSizeOffset + 4, 1);
            }

            return result;
        }

        /// <summary>Makes <paramref name="owner"/> own <paramref name="owned"/>; an owned window always stays above its owner.</summary>
        public static void SetOwner(IntPtr owned, IntPtr owner) => SetWindowLongPtr(owned, GWLP_HWNDPARENT, owner);

        /// <summary>Clicking the window no longer activates it (or takes focus from the widget).</summary>
        public static void SetNoActivate(IntPtr hwnd)
        {
            long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_NOACTIVATE));
        }

        // The *Ptr variants only exist in 64-bit user32; x86 builds use the 32-bit ones.
        private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));

        private static void SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value)
        {
            if (IntPtr.Size == 8)
            {
                SetWindowLongPtr64(hwnd, index, value);
            }
            else
            {
                SetWindowLong32(hwnd, index, value.ToInt32());
            }
        }
    }
}
