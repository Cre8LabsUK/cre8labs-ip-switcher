using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Cre8Labs.IPSwitcher.Input
{
    /// <summary>System-wide hotkeys through RegisterHotKey, so shortcuts work while the app is in the background.</summary>
    public sealed class GlobalHotkeys
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        const int WmHotkey = 0x0312;
        const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

        readonly IntPtr _hwnd;
        readonly Dictionary<int, Shortcut> _registered = new Dictionary<int, Shortcut>();
        int _nextId = 0xB000;

        public GlobalHotkeys(IntPtr hwnd) => _hwnd = hwnd;

        public event Action<Shortcut> Pressed;

        /// <summary>Returns false if another app already owns this combination.</summary>
        public bool Register(Shortcut shortcut)
        {
            var modifiers = ModNoRepeat;
            if ((shortcut.Modifiers & ModifierKeys.Alt) != 0) modifiers |= ModAlt;
            if ((shortcut.Modifiers & ModifierKeys.Control) != 0) modifiers |= ModControl;
            if ((shortcut.Modifiers & ModifierKeys.Shift) != 0) modifiers |= ModShift;
            if ((shortcut.Modifiers & ModifierKeys.Windows) != 0) modifiers |= ModWin;

            if (!Add(shortcut, modifiers, shortcut.Key)) return false;
            // Digits also answer on the numpad, like they do inside the app.
            if (shortcut.Key >= Key.D0 && shortcut.Key <= Key.D9) Add(shortcut, modifiers, Key.NumPad0 + (shortcut.Key - Key.D0));
            return true;
        }

        bool Add(Shortcut shortcut, uint modifiers, Key key)
        {
            var id = _nextId++;
            if (!RegisterHotKey(_hwnd, id, modifiers, (uint)KeyInterop.VirtualKeyFromKey(key))) return false;
            _registered[id] = shortcut;
            return true;
        }

        public void Clear()
        {
            foreach (var id in _registered.Keys) UnregisterHotKey(_hwnd, id);
            _registered.Clear();
        }

        public IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out var shortcut))
            {
                handled = true;
                Pressed?.Invoke(shortcut);
            }
            return IntPtr.Zero;
        }
    }
}
