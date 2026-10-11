using System;
using BoxeZone.Helpers;

namespace BoxeZone.Services
{
    [Flags]
    public enum HotkeyModifiers : uint
    {
        None = 0,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Windows = 0x0008,
    }

    /// <summary>
    /// System-wide shortcut through RegisterHotKey. WM_HOTKEY arrives at the window's message
    /// loop, which WinUI owns, so the window is subclassed to receive it (even while hidden).
    /// </summary>
    public sealed class HotkeyService : IDisposable
    {
        private const int HotkeyId = 1;
        private const uint WM_HOTKEY = 0x0312;
        private const uint MOD_NOREPEAT = 0x4000;
        private static readonly UIntPtr SubclassId = 1;

        private readonly IntPtr _hwnd;

        // Kept in a field so the GC does not collect the delegate while Windows still calls it.
        private readonly Win32.SubclassProc _windowProc;

        private bool _isRegistered;

        public HotkeyService(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _windowProc = WindowProc;
            Win32.SetWindowSubclass(_hwnd, _windowProc, SubclassId, 0);
        }

        public event EventHandler? Pressed;

        /// <summary>Registers the shortcut, replacing the current one. False if another app already uses it.</summary>
        public bool Register(HotkeyModifiers modifiers, uint virtualKey)
        {
            Unregister();
            _isRegistered = Win32.RegisterHotKey(_hwnd, HotkeyId, (uint)modifiers | MOD_NOREPEAT, virtualKey);
            return _isRegistered;
        }

        public void Unregister()
        {
            if (_isRegistered)
            {
                Win32.UnregisterHotKey(_hwnd, HotkeyId);
                _isRegistered = false;
            }
        }

        public void Dispose()
        {
            Unregister();
            Win32.RemoveWindowSubclass(_hwnd, _windowProc, SubclassId);
        }

        private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr idSubclass, UIntPtr refData)
        {
            if (msg == WM_HOTKEY && wParam == HotkeyId)
            {
                Pressed?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;
            }

            return Win32.DefSubclassProc(hwnd, msg, wParam, lParam);
        }
    }
}
