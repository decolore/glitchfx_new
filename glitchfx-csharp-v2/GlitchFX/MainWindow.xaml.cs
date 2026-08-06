using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using GlitchFX.Models;

namespace GlitchFX
{
    /// <summary>
    /// Mirrors Python's ui/main_window.py: top toolbar (seed/presets/
    /// randomize/load/undo/redo), left inspector switcher (Effects/Output,
    /// analogous to ui/inspector/base.py's segmented control), preview on the
    /// right, and a bottom timeline bar with play/pause + export.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly Bridge _bridge = new();
        private bool _draggingTimeline;

        public MainWindow()
        {
            InitializeComponent();

            _bridge.PreviewFrameReady += OnPreviewFrameReady;
            EffectsPanelView.SettingsChanged += OnSettingsChanged;
            EffectsPanelView.RandomizeOneRequested += kind => { _bridge.RandomizeOne(kind); _bridge.RebuildPipeline(); };
            EffectsPanelView.LoadAudioRequested += path => _bridge.LoadAudio(path);
            OutputPanelView.SettingsChanged += OnSettingsChanged;
            OutputPanelView.ExportRequested += OnExportRequested;

            SeedBox.Text = _bridge.Project.MasterSeed.ToString();
            EffectsPanelView.Bind(_bridge.Project);
            OutputPanelView.Bind(_bridge.Project);
            RefreshStats();

            LoadVideoFromCommandLineIfProvided();
        }

        /// <summary>
        /// If the app was launched with a video file path as its first
        /// command-line argument, load and play it immediately. This has no
        /// effect on normal manual double-click launches (no args), and
        /// exists so GlitchFX.UiTests can drive the real preview pipeline
        /// instead of only exercising the empty "Drop a video here" state.
        /// </summary>
        private void LoadVideoFromCommandLineIfProvided()
        {
            var cmdArgs = Environment.GetCommandLineArgs();
            if (cmdArgs.Length < 2) return;
            string candidate = cmdArgs[1];
            if (!File.Exists(candidate)) return;
            if (_bridge.LoadVideo(candidate))
            {
                _bridge.Play();
                RefreshStats();
            }
        }

        private void OnSettingsChanged()
        {
            _bridge.RebuildPipeline();
            RefreshStats();
        }

        private void RefreshStats()
        {
            double duration = _bridge.Reader.Info?.Duration ?? 0;
            var stats = SyncHelpers.ComputeSyncStats(_bridge.Project, duration);
            EffectsPanelView.UpdateStats(stats.barSeconds, stats.bars, stats.cycleSeconds, stats.drift);
        }

        private void OnPreviewFrameReady(System.Windows.Media.Imaging.BitmapSource frame, double time)
        {
            Dispatcher.Invoke(() =>
            {
                PreviewView.ShowFrame(frame);
                if (!_draggingTimeline)
                {
                    double duration = Math.Max(_bridge.Reader.Info?.Duration ?? 1, 0.01);
                    TimelineSlider.Value = time / duration;
                    TimeText.Text = $"{FormatTime(time)} / {FormatTime(duration)}";
                }
            });
        }

        private static string FormatTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
        }

        private void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Video Files|*.mp4;*.mov;*.mkv;*.avi|All Files|*.*" };
            if (dialog.ShowDialog() == true)
            {
                if (_bridge.LoadVideo(dialog.FileName))
                {
                    _bridge.Play();
                    RefreshStats();
                }
                else
                {
                    MessageBox.Show(this, "Could not open the selected video.", "Glitch FX", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void RandomizeButton_Click(object sender, RoutedEventArgs e)
        {
            int? seed = int.TryParse(SeedBox.Text, out int s) ? s : null;
            _bridge.PushUndo();
            _bridge.RandomizeAll(seed);
            SeedBox.Text = _bridge.Project.MasterSeed.ToString();
            EffectsPanelView.Bind(_bridge.Project);
            OutputPanelView.Bind(_bridge.Project);
        }

        /// <summary>Presets default to %AppData%/GlitchFX/presets (created on
        /// demand), matching the Python version's preset storage location,
        /// instead of leaving Save/Load dialogs pointed at whatever directory
        /// Windows last remembered.</summary>
        private static string PresetsDirectory
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlitchFX", "presets");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private void SavePresetButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Glitch FX Preset|*.json",
                InitialDirectory = PresetsDirectory,
                FileName = $"preset_{DateTime.Now:yyyyMMdd_HHmmss}.json",
            };
            if (dialog.ShowDialog() == true) _bridge.SavePreset(dialog.FileName);
        }

        private void LoadPresetButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Glitch FX Preset|*.json", InitialDirectory = PresetsDirectory };
            if (dialog.ShowDialog() == true && _bridge.LoadPreset(dialog.FileName))
            {
                EffectsPanelView.Bind(_bridge.Project);
                OutputPanelView.Bind(_bridge.Project);
                RefreshStats();
            }
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            _bridge.Undo();
            EffectsPanelView.Bind(_bridge.Project);
            OutputPanelView.Bind(_bridge.Project);
        }

        private void RedoButton_Click(object sender, RoutedEventArgs e)
        {
            _bridge.Redo();
            EffectsPanelView.Bind(_bridge.Project);
            OutputPanelView.Bind(_bridge.Project);
        }

        private void EffectsTabButton_Click(object sender, RoutedEventArgs e)
        {
            EffectsPanelView.Visibility = Visibility.Visible;
            OutputPanelView.Visibility = Visibility.Collapsed;
            EffectsTabButton.Style = (Style)FindResource("AccentButton");
            OutputTabButton.Style = (Style)FindResource("ToolbarButton");
        }

        private void OutputTabButton_Click(object sender, RoutedEventArgs e)
        {
            EffectsPanelView.Visibility = Visibility.Collapsed;
            OutputPanelView.Visibility = Visibility.Visible;
            OutputTabButton.Style = (Style)FindResource("AccentButton");
            EffectsTabButton.Style = (Style)FindResource("ToolbarButton");
        }

        private bool _playing;
        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            _playing = !_playing;
            if (_playing) { _bridge.Play(); PlayPauseButton.Content = "\u23f8\ufe0e"; }
            else { _bridge.Pause(); PlayPauseButton.Content = "\u25b6\ufe0e"; }
        }

        private void TimelineSlider_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) => _draggingTimeline = true;

        private void TimelineSlider_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            _draggingTimeline = false;
            double duration = _bridge.Reader.Info?.Duration ?? 0;
            _bridge.Seek(TimelineSlider.Value * duration);
        }

        private void OnExportRequested()
        {
            if (string.IsNullOrEmpty(_bridge.Project.SourcePath))
            {
                MessageBox.Show(this, "Load a video first.", "Glitch FX", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrEmpty(_bridge.Project.Export.OutputPath))
            {
                MessageBox.Show(this, "Choose an output path in the Output tab first.", "Glitch FX", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _bridge.Pause();
            var exporter = new GlitchFX.Export.ExportService();
            exporter.Progress += p => Dispatcher.Invoke(() => OutputPanelView.SetExportProgress(p));
            OutputPanelView.SetExportProgress(0);
            System.Threading.Tasks.Task.Run(() =>
            {
                exporter.ExportVideo(_bridge.Project, _bridge.Project.SourcePath, (success, message) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        OutputPanelView.SetExportProgress(null);
                        MessageBox.Show(this, success ? $"Exported to {message}" : $"Export failed: {message}",
                            "Glitch FX", MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Error);
                    });
                });
            });
        }
    }
}
