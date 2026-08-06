using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using GlitchFX.Export;
using GlitchFX.Models;
using GlitchFX.Views;

namespace GlitchFX
{
    /// <summary>
    /// Mirrors Python's ui/main_window.py: top toolbar (seed/presets/
    /// randomize/load/undo/redo/global strength), left inspector switcher
    /// (Effects/Output, analogous to ui/inspector/base.py's segmented
    /// control), preview on the right, and a bottom timeline bar with
    /// play/pause + export.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly Bridge _bridge = new();
        private bool _draggingTimeline;
        // Tracks whether the user has typed into SeedBox since the last time
        // it was set programmatically. Lets RandomizeButton_Click tell apart
        // "the box just shows whatever seed was last used" (should pick a
        // brand new random seed) from "the user typed a specific seed they
        // want to reproduce" (use exactly that seed once). Without this,
        // Randomize kept feeding the displayed seed back into itself forever,
        // so MasterSeed never changed even though the effect parameters did.
        private bool _seedManuallyEdited;
        private bool _suppressSeedBoxEvents;
        private static readonly string[] VideoExtensions = { ".mp4", ".mov", ".mkv", ".avi" };

        // The ExportService currently running an export, if any - kept so the
        // Output panel's Stop button can cancel it. Set right before the
        // background export Task starts and cleared once it completes
        // (successfully, with an error, or because it was cancelled).
        private ExportService? _activeExporter;

        public MainWindow()
        {
            InitializeComponent();

            _bridge.PreviewFrameReady += OnPreviewFrameReady;
            _bridge.AudioChanged += () => Dispatcher.Invoke(RefreshAudioBox);
            EffectsPanelView.SettingsChanged += OnSettingsChanged;
            EffectsPanelView.RandomizeOneRequested += kind => { _bridge.RandomizeOne(kind); _bridge.RebuildPipeline(); };
            EffectsPanelView.LoadAudioRequested += OnLoadAudioRequested;
            EffectsPanelView.AudioReactionSettingsChanged += () => _bridge.RecomputeAudioReaction();
            OutputPanelView.SettingsChanged += OnSettingsChanged;
            OutputPanelView.ExportRequested += OnExportRequested;
            OutputPanelView.StopExportRequested += OnStopExportRequested;
            PreviewView.LoadVideoRequested += OnPreviewLoadVideoRequested;

            SetSeedBoxText(_bridge.Project.MasterSeed.ToString());
            EffectsPanelView.Bind(_bridge.Project);
            OutputPanelView.Bind(_bridge.Project);
            SyncGlobalControls();
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

        private void OnLoadAudioRequested(string path)
        {
            if (!_bridge.LoadAudio(path))
            {
                AppDialog.Show(this, "Could not load the selected audio track.", "Glitch FX", AppDialogKind.Error);
            }
        }

        private void OnSettingsChanged()
        {
            _bridge.RebuildPipeline();
            RefreshStats();
        }

        /// <summary>
        /// Keeps the top-toolbar Seed box and Strength slider, and the
        /// effects panel's audio drop box (waveform + reaction envelope +
        /// file name), in sync with whatever is currently in
        /// _bridge.Project / _bridge's decoded audio cache. Called once at
        /// startup and again after every EffectsPanelView.Bind(...) call
        /// (randomize/undo/redo/load preset), since those all swap out the
        /// bound project wholesale.
        /// </summary>
        private void SyncGlobalControls()
        {
            SetSeedBoxText(_bridge.Project.MasterSeed.ToString());
            StrengthSlider.Value = _bridge.Project.GlobalStrength;
            StrengthValueText.Text = $"{_bridge.Project.GlobalStrength * 100:F0}%";
            RefreshAudioBox();
        }

        private void RefreshAudioBox()
        {
            string? fileName = string.IsNullOrEmpty(_bridge.Project.AudioPath) ? null : Path.GetFileName(_bridge.Project.AudioPath);
            EffectsPanelView.SetAudioTrack(fileName, _bridge.AudioWaveformGraph, _bridge.AudioReactionGraph);
        }

        private void StrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // MainWindow.xaml sets StrengthSlider's Value="1" explicitly, which
            // differs from Slider's own default of 0. WPF raises ValueChanged
            // synchronously the moment that attribute is applied, which happens
            // *during* InitializeComponent() itself - before later-declared named
            // elements such as StrengthValueText have been connected to their
            // fields yet. Without this guard that early, spurious event throws a
            // NullReferenceException here, which (since it happens inside the
            // constructor, during XAML load) crashes the whole app before the
            // window is ever shown. SyncGlobalControls(), called right after
            // InitializeComponent() finishes, performs the real initial sync, so
            // it's safe to simply ignore this handler until everything is wired up.
            if (StrengthValueText == null) return;
            _bridge.Project.GlobalStrength = StrengthSlider.Value;
            StrengthValueText.Text = $"{StrengthSlider.Value * 100:F0}%";
            _bridge.RebuildPipeline();
        }

        private void RefreshStats()
        {
            double duration = _bridge.Reader.Info?.Duration ?? 0;
            var stats = SyncHelpers.ComputeSyncStats(_bridge.Project, duration);
            EffectsPanelView.UpdateStats(stats.barSeconds, stats.bars, stats.cycleSeconds, stats.drift);
            RefreshTimelineTicks();
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
                    AppDialog.Show(this, "Could not open the selected video.", "Glitch FX", AppDialogKind.Error);
                }
            }
        }

        // Clicking the empty preview area opens the same file picker as the
        // toolbar's Load button (see PreviewControl.LoadVideoRequested).
        private void OnPreviewLoadVideoRequested() => LoadButton_Click(this, new RoutedEventArgs());

        // ---- Drag & drop: dropping a video file anywhere on the window loads it, ----
        // mirroring LoadButton_Click instead of requiring the file picker every time.
        private static string? GetDroppedVideoPath(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;
            return files.FirstOrDefault(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        }

        private void Window_DragEnter(object sender, DragEventArgs e)
        {
            bool valid = GetDroppedVideoPath(e) != null;
            e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
            DropOverlay.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
            e.Handled = true;
        }

        private void Window_DragLeave(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            string? path = GetDroppedVideoPath(e);
            if (path == null) return;
            if (_bridge.LoadVideo(path))
            {
                _bridge.Play();
                RefreshStats();
            }
            else
            {
                AppDialog.Show(this, "Could not open the dropped video.", "Glitch FX", AppDialogKind.Error);
            }
        }

        // ---- Space hotkey: play/pause only, and only when the user isn't typing in a text field. ----
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space) return;
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
            PlayPauseButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }

        private void SeedBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_suppressSeedBoxEvents) return;
            _seedManuallyEdited = true;
        }

        /// <summary>
        /// Sets SeedBox.Text programmatically (from Randomize/Undo/Redo/preset
        /// load) without marking the seed as manually edited by the user - see
        /// _seedManuallyEdited and RandomizeButton_Click.
        /// </summary>
        private void SetSeedBoxText(string text)
        {
            _suppressSeedBoxEvents = true;
            SeedBox.Text = text;
            _suppressSeedBoxEvents = false;
        }

        private void RandomizeButton_Click(object sender, RoutedEventArgs e)
        {
            // Only honor the SeedBox's current text as an explicit seed if the
            // user actually typed into it since the last update; otherwise
            // Randomize always picks a brand new random seed. Previously this
            // always re-parsed whatever the box displayed (which itself was
            // set from the *previous* randomize result), so MasterSeed just
            // fed back into itself forever and never visibly changed, even
            // though the effect parameters did.
            int? seed = _seedManuallyEdited && int.TryParse(SeedBox.Text, out int s) ? s : null;
            _seedManuallyEdited = false;
            _bridge.PushUndo();
            _bridge.RandomizeAll(seed);
            EffectsPanelView.Bind(_bridge.Project);
            OutputPanelView.Bind(_bridge.Project);
            SyncGlobalControls();
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
                SyncGlobalControls();
                RefreshStats();
            }
        }

        private void UndoButton_Click(object sender, RoutedEventArgs e)
        {
            _bridge.Undo();
            EffectsPanelView.Bind(_bridge.Project);
            OutputPanelView.Bind(_bridge.Project);
            SyncGlobalControls();
        }

        private void RedoButton_Click(object sender, RoutedEventArgs e)
        {
            _bridge.Redo();
            EffectsPanelView.Bind(_bridge.Project);
            OutputPanelView.Bind(_bridge.Project);
            SyncGlobalControls();
        }

        private void EffectsTabButton_Click(object sender, RoutedEventArgs e)
        {
            EffectsPanelView.Visibility = Visibility.Visible;
            OutputPanelView.Visibility = Visibility.Collapsed;
            EffectsTabButton.Style = (Style)FindResource("SegmentButtonSelected");
            OutputTabButton.Style = (Style)FindResource("SegmentButtonUnselected");
        }

        private void OutputTabButton_Click(object sender, RoutedEventArgs e)
        {
            EffectsPanelView.Visibility = Visibility.Collapsed;
            OutputPanelView.Visibility = Visibility.Visible;
            OutputTabButton.Style = (Style)FindResource("SegmentButtonSelected");
            EffectsTabButton.Style = (Style)FindResource("SegmentButtonUnselected");
        }

        private bool _playing;
        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            _playing = !_playing;
            if (_playing)
            {
                _bridge.Play();
                PlayIcon.Visibility = Visibility.Collapsed;
                PauseIcon.Visibility = Visibility.Visible;
            }
            else
            {
                _bridge.Pause();
                PauseIcon.Visibility = Visibility.Collapsed;
                PlayIcon.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// Timeline scrubbing: IsMoveToPointEnabled (set in MainWindow.xaml)
        /// makes a single click jump the thumb straight to the clicked
        /// position instead of paging toward it a little at a time. These
        /// three handlers pause the playback-driven Value updates for the
        /// whole duration of the interaction and seek immediately on every
        /// resulting Value change, so clicking anywhere on the bar (or
        /// dragging the thumb) jumps/scrubs the preview right away instead of
        /// only seeking once the mouse button is released.
        /// </summary>
        private void TimelineSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _draggingTimeline = true;

        private void TimelineSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _draggingTimeline = false;
            double duration = _bridge.Reader.Info?.Duration ?? 0;
            _bridge.Seek(TimelineSlider.Value * duration);
        }

        private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_draggingTimeline) return;
            double duration = _bridge.Reader.Info?.Duration ?? 0;
            _bridge.Seek(TimelineSlider.Value * duration);
        }

        // ---- Timeline tick marks: a light tick at every bar boundary and a taller ----
        // accent-colored tick at every full sync-cycle boundary, so the user can see
        // at a glance where the beat-synced loop repeats along the scrub bar.
        private void RefreshTimelineTicks()
        {
            TimelineTicksCanvas.Children.Clear();
            double duration = _bridge.Reader.Info?.Duration ?? 0;
            double width = TimelineTicksCanvas.ActualWidth;
            if (duration <= 0 || width <= 0) return;

            var stats = SyncHelpers.ComputeSyncStats(_bridge.Project, duration);
            if (stats.barSeconds <= 0) return;

            var tickBrush = (Brush)FindResource("BorderBrush2");
            var cycleBrush = (Brush)FindResource("AccentBrush");

            int barIndex = 1;
            for (double t = stats.barSeconds; t < duration - 0.001; t += stats.barSeconds, barIndex++)
            {
                bool isCycleBoundary = stats.bars > 0 && barIndex % stats.bars == 0;
                var tick = new System.Windows.Shapes.Rectangle
                {
                    Width = isCycleBoundary ? 2 : 1,
                    Height = isCycleBoundary ? 14 : 7,
                    Fill = isCycleBoundary ? cycleBrush : tickBrush,
                };
                double x = (t / duration) * width;
                System.Windows.Controls.Canvas.SetLeft(tick, x);
                System.Windows.Controls.Canvas.SetTop(tick, isCycleBoundary ? 3 : 6);
                TimelineTicksCanvas.Children.Add(tick);
            }
        }

        private void TimelineTicksCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshTimelineTicks();

        private void OnStopExportRequested() => _activeExporter?.Cancel();

        private void OnExportRequested()
        {
            if (string.IsNullOrEmpty(_bridge.Project.SourcePath))
            {
                AppDialog.Show(this, "Load a video first.", "Glitch FX", AppDialogKind.Warning);
                return;
            }
            if (string.IsNullOrEmpty(_bridge.Project.Export.OutputPath))
            {
                AppDialog.Show(this, "Choose an output path in the Output tab first.", "Glitch FX", AppDialogKind.Warning);
                return;
            }
            if (File.Exists(_bridge.Project.Export.OutputPath))
            {
                string fileName = Path.GetFileName(_bridge.Project.Export.OutputPath);
                bool overwrite = AppDialog.Confirm(this, $"\"{fileName}\" already exists and will be overwritten. Continue?", "Glitch FX", "Overwrite");
                if (!overwrite) return;
            }
            _bridge.Pause();
            var exporter = new ExportService();
            _activeExporter = exporter;
            exporter.Progress += info => Dispatcher.Invoke(() => OutputPanelView.SetExportProgress(info));
            OutputPanelView.SetExportProgress(new ExportProgressInfo { Fraction = 0 });
            System.Threading.Tasks.Task.Run(() =>
            {
                exporter.ExportVideo(_bridge.Project, _bridge.Project.SourcePath, (success, message) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        _activeExporter = null;
                        OutputPanelView.SetExportProgress(null);
                        bool cancelled = !success && message == "Export cancelled";
                        string text = success ? $"Exported to {message}" : (cancelled ? "Export cancelled." : $"Export failed: {message}");
                        AppDialog.Show(this, text, "Glitch FX", success || cancelled ? AppDialogKind.Info : AppDialogKind.Error);
                    });
                });
            });
        }
    }
}
