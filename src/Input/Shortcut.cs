using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace Cre8Labs.IPSwitcher.Input
{
    /// <summary>
    /// A key plus modifiers, stored as text such as "Ctrl+Shift+D1" and shown as "Ctrl+Shift+1".
    /// Numpad digits are treated as the matching number-row key.
    /// </summary>
    public readonly struct Shortcut : IEquatable<Shortcut>
    {
        public static readonly Shortcut None = default;

        public Shortcut(ModifierKeys modifiers, Key key)
        {
            Modifiers = modifiers;
            Key = Normalize(key);
        }

        public ModifierKeys Modifiers { get; }
        public Key Key { get; }
        public bool IsEmpty => Key == Key.None;

        /// <summary>Ctrl, Alt or Win: needed for background shortcuts so they never swallow normal typing.</summary>
        public bool HasStrongModifier => (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;

        /// <summary>Keys the app or Windows needs for navigation, so they cannot be assigned.</summary>
        public bool IsReserved =>
            (Modifiers == ModifierKeys.None || Modifiers == ModifierKeys.Shift) && (Key == Key.Tab || Key == Key.Enter || Key == Key.Escape) ||
            Modifiers == ModifierKeys.Alt && (Key == Key.F4 || Key == Key.Tab || Key == Key.Space);

        static Key Normalize(Key key) =>
            key >= Key.NumPad0 && key <= Key.NumPad9 ? Key.D0 + (key - Key.NumPad0) : key;

        public static bool IsModifierKey(Key key) =>
            key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin;

        public static Shortcut FromKeyEvent(KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
            return IsModifierKey(key) || key == Key.None ? None : new Shortcut(Keyboard.Modifiers, key);
        }

        public static Shortcut Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return None;
            var parts = text.Split('+');
            var modifiers = ModifierKeys.None;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].Trim().ToLowerInvariant())
                {
                    case "ctrl": case "control": modifiers |= ModifierKeys.Control; break;
                    case "alt": modifiers |= ModifierKeys.Alt; break;
                    case "shift": modifiers |= ModifierKeys.Shift; break;
                    case "win": case "windows": modifiers |= ModifierKeys.Windows; break;
                    default: return None;
                }
            }
            return Enum.TryParse(parts[parts.Length - 1].Trim(), true, out Key key) && key != Key.None && !IsModifierKey(key)
                ? new Shortcut(modifiers, key)
                : None;
        }

        /// <summary>Storage form, for example "Ctrl+D1".</summary>
        public override string ToString() => IsEmpty ? "" : Prefix() + Key;

        /// <summary>Display form, for example "Ctrl+1".</summary>
        public string Display => IsEmpty ? "" : Prefix() + KeyName(Key);

        string Prefix()
        {
            var parts = new List<string>();
            if ((Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((Modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
            return parts.Count == 0 ? "" : string.Join("+", parts) + "+";
        }

        static string KeyName(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9) return ((int)(key - Key.D0)).ToString();
            switch (key)
            {
                case Key.OemPlus: return "=";
                case Key.OemMinus: return "-";
                case Key.OemComma: return ",";
                case Key.OemPeriod: return ".";
                case Key.OemQuestion: return "/";
                case Key.OemSemicolon: return ";";
                case Key.OemQuotes: return "'";
                case Key.OemOpenBrackets: return "[";
                case Key.OemCloseBrackets: return "]";
                case Key.OemPipe: return "\\";
                case Key.OemTilde: return "`";
                case Key.Enter: return "Enter";
                case Key.Back: return "Backspace";
                case Key.Next: return "PageDown";
                case Key.Prior: return "PageUp";
                case Key.Space: return "Space";
                default: return key.ToString();
            }
        }

        public bool Equals(Shortcut other) => Modifiers == other.Modifiers && Key == other.Key;
        public override bool Equals(object obj) => obj is Shortcut s && Equals(s);
        public override int GetHashCode() => ((int)Modifiers << 16) ^ (int)Key;
    }
}
