using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace GlitchFX.Views
{
    public enum AppDialogKind { Info, Warning, Error }

    /// <summary>
    /// A small dark, rounded replacement for System.Windows.MessageBox so
    /// export success/failure and validation prompts match the rest of the
    /// app's theme instead of popping up a plain white Windows dialog.
    /// </summary>
    public partial class AppDialog : Window
    {
        public AppDialog()
        {
            InitializeComponent();
        }

        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

        private void OkButton_Click(object sender, RoutedEventArgs e) => Close();

        public static void Show(Window owner, string message, string title, AppDialogKind kind = AppDialogKind.Info)
        {
            var dialog = new AppDialog { Owner = owner };
            dialog.TitleTextBlock.Text = title;
            dialog.MessageTextBlock.Text = message;
            var accent = (Brush)Application.Current.FindResource("AccentBrush");
            dialog.IconText.Text = kind == AppDialogKind.Info ? "\u2139" : "\u26A0";
            dialog.IconText.Foreground = kind == AppDialogKind.Warning ? Brushes.Goldenrod : accent;
            dialog.ShowDialog();
        }
    }
}
