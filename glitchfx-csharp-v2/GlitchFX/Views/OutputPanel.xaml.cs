using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using GlitchFX.Export;
using GlitchFX.Models;

namespace GlitchFX.Views
{
    /// <summary>Mirrors Python's ui/inspector/output.py: export + output-size settings card.</summary>
    public partial class OutputPanel : UserControl
    {
        public event Action? SettingsChanged;
        public event Action? ExportRequested;
        public event Action? StopExportRequested;

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

        public void SetExportProgress(GlitchFX.Export.ExportProgressInfo? info)
        {
            if (info == null)
            {
                ExportProgress.Visibility = Visibility.Collapsed;
                ExportProgressText.Visibility = Visibility.Collapsed;
                StopExportButton.Visibility = Visibility.Collapsed;
                return;
            }
            ExportProgress.Visibility = Visibility.Visible;
            ExportProgressText.Visibility = Visibility.Visible;
            StopExportButton.Visibility = Visibility.Visible;
            ExportProgress.Value = info.Fraction;

            if (info.IsFinalizing)
            {
                ExportProgressText.Text = "Finalizing export\u2026";
            }
            else if (info.TotalFrames > 0)
            {
                string eta = info.EstimatedRemaining is TimeSpan remaining ? $" \u00b7 ~{FormatDuration(remaining)} left" : "";
                ExportProgressText.Text = $"Frame {info.CurrentFrame} / {info.TotalFrames} \u00b7 {FormatDuration(info.Elapsed)} elapsed{eta}";
            }
            else
            {
                ExportProgressText.Text = $"{info.Fraction:P0}";
            }
        }

        private static string FormatDuration(TimeSpan t)
        {
            t = t.Duration();
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";
        }

        private static void SelectByContent(ComboBox combo, string value)
        {
            foreach (ComboBoxItem item in combo.Items)
                if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase)) { combo.SelectedItem = item; return; }
        }

        // Fit applies immediately (a combo selection is always a valid, complete
        // value, unlike a width/height text box mid-edit) - see CommitTransformSize
        // below for why Width/Height are handled separately.
        private void OnTransformChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _project == null) return;
            if (FitCombo.SelectedItem is ComboBoxItem fit) _project.Transform.Fit = fit.Content.ToString() ?? "cover";
            SettingsChanged?.Invoke();
        }

        // Width/Height only commit on Enter or when focus leaves the box, instead
        // of on every keystroke. Previously typing a large resolution (e.g.
        // "16000") re-rendered the live preview at every intermediate digit typed
        // (1, 16, 160, 1600...), which felt like the app was hanging.
        private void SizeBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            CommitTransformSize();
            e.Handled = true;
        }

        private void SizeBox_LostFocus(object sender, RoutedEventArgs e) => CommitTransformSize();

        private void CommitTransformSize()
        {
            if (_suppressEvents || _project == null) return;
            bool changed = false;
            if (int.TryParse(WidthBox.Text, out int w) && w > 0 && w != _project.Transform.Width) { _project.Transform.Width = w; changed = true; }
            if (int.TryParse(HeightBox.Text, out int h) && h > 0 && h != _project.Transform.Height) { _project.Transform.Height = h; changed = true; }
            if (changed) SettingsChanged?.Invoke();
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

        private void BitrateBox_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            e.Handled = !e.Text.All(char.IsDigit);
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog { Filter = "MP4 Video|*.mp4", FileName = System.IO.Path.GetFileName(OutputPathBox.Text) };
            if (!string.IsNullOrWhiteSpace(OutputPathBox.Text))
            {
                string? dir = System.IO.Path.GetDirectoryName(OutputPathBox.Text);
                if (!string.IsNullOrWhiteSpace(dir) && System.IO.Directory.Exists(dir)) dialog.InitialDirectory = dir;
            }
            if (dialog.ShowDialog() == true)
            {
                OutputPathBox.Text = dialog.FileName;
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e) => ExportRequested?.Invoke();

        private void StopExportButton_Click(object sender, RoutedEventArgs e) => StopExportRequested?.Invoke();
    }
}
