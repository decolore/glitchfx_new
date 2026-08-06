using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using GlitchFX.Models;

namespace GlitchFX.Views
{
    /// <summary>Mirrors Python's ui/inspector/output.py: export + output-size settings card.</summary>
    public partial class OutputPanel : UserControl
    {
        public event Action? SettingsChanged;
        public event Action? ExportRequested;

        private ProjectSettings? _project;
        private bool _suppressEvents;

        public OutputPanel()
        {
            InitializeComponent();
        }

        public void Bind(ProjectSettings project)
        {
            _project = project;
            _suppressEvents = true;
            WidthBox.Text = project.Transform.Width.ToString();
            HeightBox.Text = project.Transform.Height.ToString();
            SelectByContent(FitCombo, project.Transform.Fit);
            SelectByContent(CodecCombo, project.Export.Codec);
            CrfSlider.Value = project.Export.Crf;
            SelectByContent(PresetCombo, project.Export.Preset);
            // Older presets may have saved ffmpeg-style values like "8M";
            // keep only the digits since the box now edits a plain kbps number.
            BitrateBox.Text = new string(project.Export.MaxBitrate.Where(char.IsDigit).ToArray());
            OutputPathBox.Text = project.Export.OutputPath;
            _suppressEvents = false;
        }

        public void SetExportProgress(double? value)
        {
            if (value == null) { ExportProgress.Visibility = Visibility.Collapsed; return; }
            ExportProgress.Visibility = Visibility.Visible;
            ExportProgress.Value = value.Value;
        }

        private static void SelectByContent(ComboBox combo, string value)
        {
            foreach (ComboBoxItem item in combo.Items)
                if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase)) { combo.SelectedItem = item; return; }
        }

        private void OnTransformChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _project == null) return;
            if (int.TryParse(WidthBox.Text, out int w)) _project.Transform.Width = w;
            if (int.TryParse(HeightBox.Text, out int h)) _project.Transform.Height = h;
            if (FitCombo.SelectedItem is ComboBoxItem fit) _project.Transform.Fit = fit.Content.ToString() ?? "cover";
            SettingsChanged?.Invoke();
        }

        private void OnExportChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _project == null) return;
            if (CodecCombo.SelectedItem is ComboBoxItem codec) _project.Export.Codec = codec.Content.ToString() ?? "libx264";
            _project.Export.Crf = (int)CrfSlider.Value;
            if (PresetCombo.SelectedItem is ComboBoxItem preset) _project.Export.Preset = preset.Content.ToString() ?? "medium";
            _project.Export.MaxBitrate = BitrateBox.Text;
            _project.Export.OutputPath = OutputPathBox.Text;
            SettingsChanged?.Invoke();
        }

        // Bitrate is now entered as a plain kbps number (e.g. "5000" for 5 Mbps)
        // instead of ffmpeg's "5M" shorthand, so only digits are accepted.
        private void BitrateBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(char.IsDigit);
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog { Filter = "MP4 Video|*.mp4", FileName = "output.mp4" };
            if (dialog.ShowDialog() == true)
            {
                OutputPathBox.Text = dialog.FileName;
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e) => ExportRequested?.Invoke();
    }
}
