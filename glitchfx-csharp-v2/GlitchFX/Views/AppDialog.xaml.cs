using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GlitchFX.Views
{
    public enum AppDialogKind { Info, Warning, Error }

    /// <summary>Dark, rounded modal dialog replacing System.Windows.MessageBox.
    /// Supports a plain info/warning/error OK dialog (Show) and a Cancel/OK
    /// confirmation dialog (Confirm), e.g. for the export-overwrite warning.</summary>
    public partial class AppDialog : Window
    {
        public AppDialog()
        {
            InitializeComponent();
        }

        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

        private void OkButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private static void Configure(AppDialog dialog, string message, string title, AppDialogKind kind)
        {
            dialog.TitleTextBlock.Text = title;
            dialog.MessageTextBlock.Text = message;
            var accent = (Brush)Application.Current.FindResource("AccentBrush");
            dialog.IconText.Text = kind == AppDialogKind.Info ? "\u2139" : "\u26A0";
            dialog.IconText.Foreground = kind == AppDialogKind.Warning ? Brushes.Goldenrod : accent;
        }

        public static void Show(Window owner, string message, string title, AppDialogKind kind = AppDialogKind.Info)
        {
            var dialog = new AppDialog { Owner = owner };
            Configure(dialog, message, title, kind);
            dialog.ShowDialog();
        }

        /// <summary>
        /// Shows a dark confirm dialog with OK/Cancel buttons (used for the
        /// export-overwrite warning) and returns true only if the user chose
        /// the primary action.
        /// </summary>
        public static bool Confirm(Window owner, string message, string title, string confirmText = "OK", AppDialogKind kind = AppDialogKind.Warning)
        {
            var dialog = new AppDialog { Owner = owner };
            Configure(dialog, message, title, kind);
            dialog.OkButton.Content = confirmText;
            dialog.CancelButton.Visibility = Visibility.Visible;
            return dialog.ShowDialog() == true;
        }
    }
}
