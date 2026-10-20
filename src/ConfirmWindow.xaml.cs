using System.Windows;

namespace Cre8Labs.IPSwitcher
{
    /// <summary>A branded yes or no question. Cancel is the default, so Enter never confirms by accident.</summary>
    public partial class ConfirmWindow : Window
    {
        public ConfirmWindow(string heading, string message, string confirmText)
        {
            InitializeComponent();
            Title = heading;
            HeadingText.Text = heading;
            MessageText.Text = message;
            ConfirmButton.Content = confirmText;
        }

        public static bool Ask(Window owner, string heading, string message, string confirmText) =>
            new ConfirmWindow(heading, message, confirmText) { Owner = owner }.ShowDialog() == true;

        void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
