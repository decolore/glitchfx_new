using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlitchFX.Views
{
    /// <summary>
    /// Minimal color picker built entirely from plain WPF controls (RGB
    /// sliders + a hex text box + a live preview swatch).
    ///
    /// This exists instead of System.Windows.Forms.ColorDialog because
    /// enabling UseWindowsForms alongside UseWPF in the same project makes
    /// the .NET SDK add implicit global usings for both System.Windows and
    /// System.Windows.Forms, and many identically-named types exist in both
    /// (Application, UserControl, ComboBox, Point, Brush, DragEventArgs,
    /// KeyEventArgs, MouseEventArgs, ...). That turned nearly every WPF file
    /// in the project into a CS0104 "ambiguous reference" build error, so
    /// WinForms is intentionally avoided altogether.
    /// </summary>
    public static class ColorPickerDialog
    {
        /// <summary>
        /// Shows a modal color picker seeded with <paramref name="initialHex"/>.
        /// Returns true and sets <paramref name="resultHex"/> if the user
        /// clicked OK; returns false (leaving resultHex equal to the input)
        /// if they cancelled or closed the window.
        /// </summary>
        public static bool Show(Window owner, string initialHex, out string resultHex)
        {
            Color initial = ParseHex(initialHex);
            resultHex = initialHex;

            var window = new Window
            {
                Title = "Pick a color",
                Width = 280,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                Background = owner.Background,
            };

            var root = new StackPanel { Margin = new Thickness(16) };
            window.Content = root;

            var preview = new Border
            {
                Height = 40,
                Margin = new Thickness(0, 0, 0, 12),
                Background = new SolidColorBrush(initial),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
            };
            root.Children.Add(preview);

            var hexBox = new TextBox { Text = $"#{initial.R:X2}{initial.G:X2}{initial.B:X2}", Margin = new Thickness(0, 0, 0, 12) };
            root.Children.Add(hexBox);

            Slider AddSlider(string label, byte value)
            {
                root.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 2) });
                var slider = new Slider { Minimum = 0, Maximum = 255, Value = value, Margin = new Thickness(0, 0, 0, 8) };
                root.Children.Add(slider);
                return slider;
            }

            var rSlider = AddSlider("R", initial.R);
            var gSlider = AddSlider("G", initial.G);
            var bSlider = AddSlider("B", initial.B);

            bool suppress = false;

            void UpdateFromSliders()
            {
                if (suppress) return;
                var c = Color.FromRgb((byte)rSlider.Value, (byte)gSlider.Value, (byte)bSlider.Value);
                suppress = true;
                hexBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                suppress = false;
                preview.Background = new SolidColorBrush(c);
            }

            rSlider.ValueChanged += (s, e) => UpdateFromSliders();
            gSlider.ValueChanged += (s, e) => UpdateFromSliders();
            bSlider.ValueChanged += (s, e) => UpdateFromSliders();
            hexBox.TextChanged += (s, e) =>
            {
                if (suppress) return;
                var c = ParseHex(hexBox.Text);
                suppress = true;
                rSlider.Value = c.R;
                gSlider.Value = c.G;
                bSlider.Value = c.B;
                suppress = false;
                preview.Background = new SolidColorBrush(c);
            };

            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelBtn = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
            var okBtn = new Button { Content = "OK", Padding = new Thickness(12, 4, 12, 4), IsDefault = true };
            buttonRow.Children.Add(cancelBtn);
            buttonRow.Children.Add(okBtn);
            root.Children.Add(buttonRow);

            bool accepted = false;
            okBtn.Click += (s, e) => { accepted = true; window.Close(); };
            cancelBtn.Click += (s, e) => window.Close();

            window.ShowDialog();

            if (accepted) resultHex = hexBox.Text;
            return accepted;
        }

        private static Color ParseHex(string hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex)!; }
            catch { return Colors.Black; }
        }
    }
}
