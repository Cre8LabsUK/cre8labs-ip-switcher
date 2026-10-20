using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Cre8Labs.IPSwitcher.Input
{
    /// <summary>
    /// A text box that records a key combination instead of text.
    /// Backspace or Delete clears it. Tab, Enter and Esc still move focus, save and cancel.
    /// </summary>
    public sealed class ShortcutBox : TextBox
    {
        public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
            nameof(Gesture), typeof(string), typeof(ShortcutBox),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnGestureChanged));

        public ShortcutBox()
        {
            IsReadOnly = true;
            IsReadOnlyCaretVisible = false;
            IsUndoEnabled = false;
            Cursor = Cursors.Hand;
            ContextMenu = null;
        }

        /// <summary>Storage form, for example "Ctrl+D1". Empty means no shortcut.</summary>
        public string Gesture
        {
            get => (string)GetValue(GestureProperty);
            set => SetValue(GestureProperty, value);
        }

        static void OnGestureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((ShortcutBox)d).Text = Shortcut.Parse(e.NewValue as string).Display;

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var plain = Keyboard.Modifiers == ModifierKeys.None || Keyboard.Modifiers == ModifierKeys.Shift;

            // Leave navigation, save and cancel working.
            if (plain && (key == Key.Tab || key == Key.Escape || key == Key.Enter))
            {
                base.OnPreviewKeyDown(e);
                return;
            }

            e.Handled = true;
            if (Keyboard.Modifiers == ModifierKeys.None && (key == Key.Back || key == Key.Delete))
            {
                Gesture = "";
                return;
            }
            var shortcut = Shortcut.FromKeyEvent(e);
            if (!shortcut.IsEmpty) Gesture = shortcut.ToString();
        }
    }
}
