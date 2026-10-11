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

        /// <summary>"Ctrl+Alt+D" style text for a shortcut.</summary>
        public static string Format(HotkeyModifiers modifiers, uint virtualKey)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
            parts.Add(FormatKey(virtualKey));
            return string.Join('+', parts);
        }

        private static string FormatKey(uint virtualKey) => virtualKey switch
        {
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(), // 0-9, A-Z
            >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",                                 // F1-F24
            >= 0x60 and <= 0x69 => $"Num {virtualKey - 0x60}",
            0x20 => "Espaço",
            0x0D => "Enter",
            0x09 => "Tab",
            0x08 => "Backspace",
            0x2E => "Delete",
            0x2D => "Insert",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            _ => ((Windows.System.VirtualKey)virtualKey).ToString(),
        };

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
