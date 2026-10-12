using System;
using System.Collections.Generic;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace BoxeZone.Helpers
{
    /// <summary>
    /// Sizing and placement for the borderless tool windows. AppWindow works in physical
    /// pixels; bounds here refer to the visible frame, not the window rect.
    /// </summary>
    internal static class WindowExtensions
    {
        // Per window: invisible border around the visible frame (only measurable while shown).
        private static readonly Dictionary<IntPtr, Win32.RECT> FrameInsets = [];

        // Per window: window size minus XAML area size, in physical pixels, at a given scale.
        private static readonly Dictionary<IntPtr, (double Scale, int Width, int Height)> FrameOverhead = [];

        public static IntPtr GetHwnd(this Window window) => WindowNative.GetWindowHandle(window);

        public static double GetScale(this Window window) => Win32.GetDpiForWindow(window.GetHwnd()) / 96.0;

        /// <summary>
        /// No title bar, not resizable, always on top, out of the taskbar and Alt+Tab. Keeping the
        /// border preserves the Windows 11 rounded corners, shadow and 1 px frame.
        /// </summary>
        public static void ConfigureAsToolWindow(this Window window)
        {
            var presenter = OverlappedPresenter.Create();
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(true, false);
            window.AppWindow.SetPresenter(presenter);
            window.AppWindow.IsShownInSwitchers = false;

            // The tab (23 px) and the docked strip (12 px) are narrower than Windows allows by default.
            Win32.RemoveMinimumSize(window.GetHwnd());
        }

        /// <summary>
        /// Visible frame size that gives an XAML area of <paramref name="width"/> x <paramref name="height"/> (DIPs).
        /// Without a title bar the XAML area also covers the hidden caption, so the client size
        /// cannot be used; the frame overhead is measured once per scale instead. Returns false
        /// until the content is loaded and the overhead can be measured.
        /// </summary>
        public static bool TryGetVisibleSizeForXaml(this Window window, FrameworkElement content, double width, double height, out SizeInt32 visibleSize)
        {
            IntPtr hwnd = window.GetHwnd();
            double scale = window.GetScale();

            if (!FrameOverhead.TryGetValue(hwnd, out var overhead) || overhead.Scale != scale)
            {
                if (content.XamlRoot is null || content.XamlRoot.Size.Width <= 0)
                {
                    visibleSize = default;
                    return false;
                }

                SizeInt32 size = window.AppWindow.Size;
                overhead = (
                    scale,
                    size.Width - (int)Math.Round(content.XamlRoot.Size.Width * scale),
                    size.Height - (int)Math.Round(content.XamlRoot.Size.Height * scale));
                FrameOverhead[hwnd] = overhead;
            }

            Win32.RECT insets = GetInsets(hwnd);
            visibleSize = new SizeInt32(
                (int)Math.Round(width * scale) + overhead.Width - insets.Left - insets.Right,
                (int)Math.Round(height * scale) + overhead.Height - insets.Top - insets.Bottom);
            return true;
        }

        /// <summary>Rough size before the content is loaded, so the window does not flash at the default size.</summary>
        public static void ResizeClientApproximately(this Window window, double width, double height)
        {
            double scale = window.GetScale();
            window.AppWindow.ResizeClient(new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale)));
        }

        public static RectInt32 GetVisibleBounds(this Window window)
        {
            IntPtr hwnd = window.GetHwnd();
            Win32.GetWindowRect(hwnd, out Win32.RECT outer);
            Win32.RECT insets = GetInsets(hwnd);
            int left = outer.Left + insets.Left;
            int top = outer.Top + insets.Top;
            return new RectInt32(left, top, outer.Right - insets.Right - left, outer.Bottom - insets.Bottom - top);
        }

        /// <summary>
        /// Moves and resizes in one step, so a window growing next to a screen edge never spills
        /// onto a neighboring monitor (which would rescale it to that monitor's DPI).
        /// </summary>
        public static void SetVisibleBounds(this Window window, RectInt32 bounds)
        {
            Win32.RECT insets = GetInsets(window.GetHwnd());
            var outer = new RectInt32(
                bounds.X - insets.Left,
                bounds.Y - insets.Top,
                bounds.Width + insets.Left + insets.Right,
                bounds.Height + insets.Top + insets.Bottom);
            Win32.GetWindowRect(window.GetHwnd(), out Win32.RECT current);
            if (current.Left != outer.X || current.Top != outer.Y ||
                current.Right - current.Left != outer.Width || current.Bottom - current.Top != outer.Height)
            {
                window.AppWindow.MoveAndResize(outer);
            }
        }

        /// <summary>Moves the window so its visible frame starts at (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public static void MoveVisible(this Window window, int x, int y)
        {
            Win32.RECT insets = GetInsets(window.GetHwnd());
            window.AppWindow.Move(new PointInt32(x - insets.Left, y - insets.Top));
        }

        private static Win32.RECT GetInsets(IntPtr hwnd)
        {
            Win32.RECT insets = Win32.GetInvisibleFrameInsets(hwnd);
            if (insets.Left != 0 || insets.Top != 0 || insets.Right != 0 || insets.Bottom != 0)
            {
                FrameInsets[hwnd] = insets;
            }
            else
            {
                FrameInsets.TryGetValue(hwnd, out insets);
            }

            return insets;
        }
    }
}
