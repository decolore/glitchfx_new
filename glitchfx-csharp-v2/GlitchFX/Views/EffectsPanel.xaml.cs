using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using GlitchFX.Models;

namespace GlitchFX.Views
{
    /// <summary>
    /// Mirrors Python's ui/inspector/effects.py: the main effects inspector.
    /// Global beat-sync/animate/audio-reactive controls at the top (including
    /// the audio drop box, which shows a waveform + reaction-envelope overlay
    /// once a track is loaded), then one schema-driven "EffectCard" per effect
    /// in the project's stack. Each card has a single-row header - collapse
    /// chevron, drag handle, title, enable toggle, animate icon, beat-trigger
    /// icon, lock, randomize - with the title trimmed with an ellipsis (and a
    /// tooltip showing the full name) instead of ever hard-clipping. There are
    /// no separate up/down buttons; reordering is done by dragging the "⋮⋮"
    /// handle in the header. Cards default to collapsed only the very first
    /// time the panel is ever bound (i.e. on app startup/loading a video), not
    /// on every later Bind() from Undo/Redo/Randomize/Load Preset, so the
    /// user's expand/collapse choices persist across those actions.
    /// The Text Overlay effect is conceptually not a "filter" like the others
    /// - it should never be included in Randomize All/This or in the
    /// randomize-order shuffle (enforced in Bridge/Pipeline) - so its card
    /// hides the drag handle, lock, and randomize affordances entirely.
    /// </summary>
    public partial class EffectsPanel : UserControl
    {
        public event Action? SettingsChanged;
        public event Action<string>? RandomizeOneRequested;
        public event Action<string>? LoadAudioRequested;

        /// <summary>
        /// Fired specifically when a change is made that affects the audio
        /// reaction envelope (Reaction Source combo, BPM box) - as opposed to
        /// every settings change - so the owner can recompute just that
        /// (relatively expensive, FFT-based for the "bass" source) envelope
        /// instead of doing so on every slider tick.
        /// </summary>
        public event Action? AudioReactionSettingsChanged;

        private ProjectSettings? _project;
        private bool _suppressEvents;

        // Which effect kinds are currently shown collapsed. Keyed by Kind
        // (not EffectSettings reference, and not index) so collapse state
        // survives both reordering and the wholesale project-object swaps
        // that happen on every Undo/Redo/Randomize/Load Preset. Only ever
        // populated with "collapse everything" the first time Bind() is ever
        // called (see _initialCollapseDone) - after that, only the user's own
        // clicks on a card's collapse chevron change this set.
        private readonly HashSet<string> _collapsedKinds = new();
        private bool _initialCollapseDone;

        // Drag-to-reorder state for the "⋮⋮" handle.
        private Border? _dragCard;
        private int _dragIndex;
        private double _dragStartMouseY;
        private TranslateTransform? _dragTransform;

        // Audio drop box state.
        private float[]? _lastWaveform;
        private float[]? _lastReactionGraph;
        private bool _audioLoaded;
        private static readonly string[] AudioExtensions = { ".mp3", ".wav", ".aac", ".m4a", ".flac" };

        public EffectsPanel()
        {
            InitializeComponent();
        }

        public void Bind(ProjectSettings project)
        {
            _project = project;
            _suppressEvents = true;
            BpmBox.Text = project.Bpm.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TimeSigBox.Text = $"{project.TimeSigNum}/{project.TimeSigDen}";
            BarsBox.Text = project.SyncBars.ToString();
            SelectByContent(SyncModeCombo, project.SyncMode);
            AutoBarsCheck.IsChecked = project.SyncAutoBars;
            InterpolateCheck.IsChecked = project.Interpolate;
            ShuffleOrderCheck.IsChecked = project.RandomizeOrder;
            AnimateCheck.IsChecked = project.AnimateParams;
            AnimAmountSlider.Value = project.AnimationAmount;
            AudioReactiveCheck.IsChecked = project.AudioReactive;
            SelectByContent(ReactionSourceCombo, project.ReactionSource);
            SelectByContent(ReactionModeCombo, project.ReactionMode);
            AudioIntensitySlider.Value = project.AudioIntensity;
            _suppressEvents = false;

            // Only collapse every card the very first time the panel is ever
            // bound (app startup). Later rebinds - from Undo, Redo,
            // Randomize All/This, or Load Preset - must leave whatever the
            // user had expanded/collapsed alone, so cards stop jumping shut
            // every time one of those actions runs.
            if (!_initialCollapseDone)
            {
                _collapsedKinds.Clear();
                foreach (var effect in project.Effects) _collapsedKinds.Add(effect.Kind);
                _initialCollapseDone = true;
            }

            RebuildEffectCards();
        }

        public void UpdateStats(double barSeconds, int bars, double cycleSeconds, double drift)
        {
            StatsText.Text = $"Bar: {barSeconds:F2}s \u00b7 Bars: {bars} \u00b7 Cycle: {cycleSeconds:F2}s \u00b7 Drift: {drift:P1}";
        }

        private static void SelectByContent(ComboBox combo, string value)
        {
            foreach (ComboBoxItem item in combo.Items)
                if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase)) { combo.SelectedItem = item; return; }
        }

        private void OnBeatSyncChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _project == null) return;
            if (double.TryParse(BpmBox.Text, System.Globalization.CultureInfo.InvariantCulture, out double bpm)) _project.Bpm = bpm;
            var parts = TimeSigBox.Text.Split('/');
            if (parts.Length == 2 && int.TryParse(parts[0], out int num) && int.TryParse(parts[1], out int den))
            {
                _project.TimeSigNum = num; _project.TimeSigDen = den;
            }
            if (int.TryParse(BarsBox.Text, out int bars)) _project.SyncBars = bars;
            if (SyncModeCombo.SelectedItem is ComboBoxItem mode) _project.SyncMode = mode.Content.ToString() ?? "off";
            _project.SyncAutoBars = AutoBarsCheck.IsChecked == true;
            _project.Interpolate = InterpolateCheck.IsChecked == true;
            SettingsChanged?.Invoke();
            // BPM affects the "beat" reaction envelope's pulse timing.
            if (sender == BpmBox) AudioReactionSettingsChanged?.Invoke();
        }

        private void OnGlobalChanged(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents || _project == null) return;
            _project.RandomizeOrder = ShuffleOrderCheck.IsChecked == true;
            _project.AnimateParams = AnimateCheck.IsChecked == true;
            _project.AnimationAmount = (int)AnimAmountSlider.Value;
            _project.AudioReactive = AudioReactiveCheck.IsChecked == true;
            if (ReactionSourceCombo.SelectedItem is ComboBoxItem src) _project.ReactionSource = src.Content.ToString() ?? "loudness";
            if (ReactionModeCombo.SelectedItem is ComboBoxItem mode) _project.ReactionMode = mode.Content.ToString() ?? "opacity";
            _project.AudioIntensity = (int)AudioIntensitySlider.Value;
            SettingsChanged?.Invoke();
            // Reaction Source determines which envelope (loudness/bass/beat) is computed.
            if (sender == ReactionSourceCombo) AudioReactionSettingsChanged?.Invoke();
        }

        // ---- Audio drop box: drag & drop or click-to-browse, waveform + reaction overlay ----

        /// <summary>
        /// Updates the audio drop box to show either the empty "drop audio"
        /// state or the loaded waveform + reaction-envelope overlay (mirrors
        /// the audio waveform/reactivity preview from the macOS build). Called
        /// by the window owner whenever the bridge's decoded audio track or
        /// its reaction envelope (which depends on the selected Reaction
        /// Source) changes.
        /// </summary>
        public void SetAudioTrack(string? fileName, float[]? waveform, float[]? reactionGraph)
        {
            _lastWaveform = waveform;
            _lastReactionGraph = reactionGraph;
            _audioLoaded = fileName != null && waveform != null;

            AudioEmptyState.Visibility = _audioLoaded ? Visibility.Collapsed : Visibility.Visible;
            AudioWaveformCanvas.Visibility = _audioLoaded ? Visibility.Visible : Visibility.Collapsed;
            AudioFileNameText.Visibility = _audioLoaded ? Visibility.Visible : Visibility.Collapsed;
            AudioFileNameText.Text = fileName ?? "";
            AudioFileNameText.ToolTip = fileName;

            if (!_audioLoaded)
            {
                AudioHoverOverlay.Visibility = Visibility.Collapsed;
                AudioHoverOverlay.Opacity = 0;
            }

            DrawAudioGraph();
        }

        private void DrawAudioGraph()
        {
            AudioWaveformCanvas.Children.Clear();
            if (_lastWaveform == null || _lastWaveform.Length == 0) return;
            double width = AudioWaveformCanvas.ActualWidth;
            double height = AudioWaveformCanvas.ActualHeight;
            if (width <= 0 || height <= 0) return;

            var waveBrush = (Brush)FindResource("SubTextBrush");
            int n = _lastWaveform.Length;
            double barWidth = Math.Max(1, width / n);
            for (int i = 0; i < n; i++)
            {
                double v = Math.Clamp(_lastWaveform[i], 0, 1);
                double barHeight = Math.Max(1.5, v * (height - 18));
                var bar = new System.Windows.Shapes.Rectangle
                {
                    Width = Math.Max(1, barWidth - 1),
                    Height = barHeight,
                    Fill = waveBrush,
                    Opacity = 0.5,
                };
                System.Windows.Controls.Canvas.SetLeft(bar, i * barWidth);
                System.Windows.Controls.Canvas.SetTop(bar, (height - barHeight) / 2 + 4);
                AudioWaveformCanvas.Children.Add(bar);
            }

            // Reaction envelope overlay: shows where the currently selected
            // Reaction Source (loudness/bass/beat) will drive audio-reactive
            // effects, so the user can see at a glance where the peaks/dips
            // line up against the waveform.
            if (_lastReactionGraph != null && _lastReactionGraph.Length > 1)
            {
                var poly = new System.Windows.Shapes.Polyline
                {
                    Stroke = (Brush)FindResource("AccentBrush"),
                    StrokeThickness = 1.6,
                };
                int rn = _lastReactionGraph.Length;
                for (int i = 0; i < rn; i++)
                {
                    double x = (i / (double)(rn - 1)) * width;
                    double v = Math.Clamp(_lastReactionGraph[i], 0, 1);
                    double y = height - 6 - v * (height - 12);
                    poly.Points.Add(new Point(x, y));
                }
                AudioWaveformCanvas.Children.Add(poly);
            }
        }

        private void AudioWaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawAudioGraph();

        private static string? GetDroppedAudioPath(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;
            return files.FirstOrDefault(f => AudioExtensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()));
        }

        private void AudioDropBox_DragEnter(object sender, DragEventArgs e)
        {
            bool valid = GetDroppedAudioPath(e) != null;
            e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
            AudioDropBox.BorderBrush = valid ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BorderBrush2");
            e.Handled = true;
        }

        private void AudioDropBox_DragLeave(object sender, DragEventArgs e)
        {
            AudioDropBox.BorderBrush = (Brush)FindResource("BorderBrush2");
        }

        private void AudioDropBox_Drop(object sender, DragEventArgs e)
        {
            AudioDropBox.BorderBrush = (Brush)FindResource("BorderBrush2");
            string? path = GetDroppedAudioPath(e);
            if (path != null) LoadAudioRequested?.Invoke(path);
        }

        private void AudioDropBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Audio Files|*.mp3;*.wav;*.aac;*.m4a;*.flac|All Files|*.*" };
            if (dialog.ShowDialog() == true) LoadAudioRequested?.Invoke(dialog.FileName);
        }

        private void AudioDropBox_MouseEnter(object sender, MouseEventArgs e)
        {
            if (!_audioLoaded) return;
            AudioHoverOverlay.Visibility = Visibility.Visible;
            var anim = new DoubleAnimation(AudioHoverOverlay.Opacity, 1, TimeSpan.FromMilliseconds(160));
            AudioHoverOverlay.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private void AudioDropBox_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_audioLoaded) return;
            var anim = new DoubleAnimation(AudioHoverOverlay.Opacity, 0, TimeSpan.FromMilliseconds(160));
            anim.Completed += (s, ev) => AudioHoverOverlay.Visibility = Visibility.Collapsed;
            AudioHoverOverlay.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        // ---- Per-effect cards ----

        private void RebuildEffectCards()
        {
            EffectCardsPanel.Children.Clear();
            _dragCard = null;
            if (_project == null) return;
            for (int i = 0; i < _project.Effects.Count; i++)
            {
                EffectCardsPanel.Children.Add(BuildEffectCard(_project.Effects[i], i));
            }
        }

        private Border BuildEffectCard(EffectSettings settings, int index)
        {
            // Text Overlay isn't really a "filter" - it's excluded from
            // Randomize All/This and from order-shuffling (see Bridge/Pipeline),
            // so its card doesn't offer drag-to-reorder, lock, or randomize
            // affordances that would otherwise imply it participates in those.
            bool isTextOverlay = settings.Kind == "text_overlay";

            var card = new Border { Style = (Style)FindResource("CardBorder") };
            // Locked effects (excluded from Randomize All) are shown dimmed so
            // the lock state is visible at a glance, even while the card is
            // collapsed and the lock icon itself isn't in view.
            card.Opacity = settings.LockRandom ? 0.55 : 1.0;
            var stack = new StackPanel();
            card.Child = stack;

            bool collapsed = _collapsedKinds.Contains(settings.Kind);

            // Single-row header: collapse chevron, drag handle, title, enable
            // toggle, animate icon, beat-trigger icon, lock, randomize. The
            // title column is a Star width with CharacterEllipsis trimming +
            // a tooltip showing the full name, so long names ("Chromatic
            // Aberration") never get hard-clipped even when the row is tight.
            // There are no separate up/down columns; the drag handle ("⋮⋮")
            // already lets the user reorder effects by dragging, so dedicated
            // arrow buttons would only take up space without adding anything.
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 0 collapse chevron
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 1 drag handle
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 2 title
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 3 enable toggle
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 4 animate icon
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 5 beat-trigger icon
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 6 lock
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 7 randomize

            // Param panel body (built before some header handlers so the
            // collapse chevron's click handler can close over it).
            var body = new StackPanel { Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible };

            var collapseBtn = new Button
            {
                Content = collapsed ? "\u25B8" : "\u25BE",
                Style = (Style)FindResource("ToolbarButton"),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = collapsed ? "Expand" : "Collapse",
            };
            // Gives GlitchFX.UiTests a stable way to find and click "expand the
            // first effect card" (index 0) via UI Automation, since these
            // buttons are built dynamically in code and have no x:Name.
            AutomationProperties.SetAutomationId(collapseBtn, $"EffectCollapseButton_{index}");
            collapseBtn.Click += (s, e) =>
            {
                bool nowCollapsed = body.Visibility == Visibility.Visible;
                body.Visibility = nowCollapsed ? Visibility.Collapsed : Visibility.Visible;
                collapseBtn.Content = nowCollapsed ? "\u25B8" : "\u25BE";
                collapseBtn.ToolTip = nowCollapsed ? "Expand" : "Collapse";
                if (nowCollapsed) _collapsedKinds.Add(settings.Kind); else _collapsedKinds.Remove(settings.Kind);
            };
            Grid.SetColumn(collapseBtn, 0);
            header.Children.Add(collapseBtn);

            if (!isTextOverlay)
            {
                var dragHandle = new TextBlock
                {
                    Text = "\u22EE\u22EE",
                    Foreground = (Brush)FindResource("SubTextBrush"),
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 8, 0),
                    Cursor = Cursors.SizeAll,
                    ToolTip = "Drag to reorder",
                };
                Grid.SetColumn(dragHandle, 1);
                header.Children.Add(dragHandle);
                AttachDragHandle(dragHandle, card, index);
            }

            string displayName = ToTitleCase(settings.Kind);
            var title = new TextBlock
            {
                Text = displayName,
                Style = (Style)FindResource("CardHeaderText"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = displayName,
                Margin = new Thickness(0, 0, 6, 0),
            };
            Grid.SetColumn(title, 2);
            header.Children.Add(title);

            var enableToggle = new CheckBox { IsChecked = settings.Enabled, ToolTip = "Enabled", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            enableToggle.Checked += (s, e) => { settings.Enabled = true; RaiseChanged(); };
            enableToggle.Unchecked += (s, e) => { settings.Enabled = false; RaiseChanged(); };
            Grid.SetColumn(enableToggle, 3);
            header.Children.Add(enableToggle);

            // Animate / Beat Trigger are now compact icon toggles in the header
            // (lit = on, via IconToggleCheck's checked-background) instead of a
            // separate labeled row inside the card body, so they don't take up
            // extra vertical space and stay reachable even while collapsed.
            var animateToggle = new CheckBox
            {
                Content = "\u21BB", Style = (Style)FindResource("IconToggleCheck"), ToolTip = "Animate",
                IsChecked = settings.Animate, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
            };
            animateToggle.Checked += (s, e) => { settings.Animate = true; RaiseChanged(); };
            animateToggle.Unchecked += (s, e) => { settings.Animate = false; RaiseChanged(); };
            Grid.SetColumn(animateToggle, 4);
            header.Children.Add(animateToggle);

            var beatToggle = new CheckBox
            {
                Content = "\u266A", Style = (Style)FindResource("IconToggleCheck"), ToolTip = "Beat Trigger",
                IsChecked = settings.BeatSync, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
            };
            beatToggle.Checked += (s, e) => { settings.BeatSync = true; RaiseChanged(); };
            beatToggle.Unchecked += (s, e) => { settings.BeatSync = false; RaiseChanged(); };
            Grid.SetColumn(beatToggle, 5);
            header.Children.Add(beatToggle);

            if (!isTextOverlay)
            {
                var lockToggle = new CheckBox { Content = "\ud83d\udd12", Style = (Style)FindResource("IconToggleCheck"), ToolTip = "Lock (exclude from Randomize All)", IsChecked = settings.LockRandom, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
                lockToggle.Checked += (s, e) => { settings.LockRandom = true; card.Opacity = 0.55; };
                lockToggle.Unchecked += (s, e) => { settings.LockRandom = false; card.Opacity = 1.0; };
                Grid.SetColumn(lockToggle, 6);
                header.Children.Add(lockToggle);

                var randomizeBtn = new Button { Content = "\ud83c\udfb2", ToolTip = "Randomize this effect", Style = (Style)FindResource("ToolbarButton"), Padding = new Thickness(6, 2, 6, 2) };
                randomizeBtn.Click += (s, e) => { RandomizeOneRequested?.Invoke(settings.Kind); RebuildEffectCards(); };
                Grid.SetColumn(randomizeBtn, 7);
                header.Children.Add(randomizeBtn);
            }

            stack.Children.Add(header);

            foreach (var def in EffectSchemas.SchemaFor(settings.Kind))
            {
                body.Children.Add(BuildParamRow(settings, def));
            }

            stack.Children.Add(body);

            return card;
        }

        /// <summary>
        /// Wires up drag-to-reorder from the "⋮⋮" handle: pressing it captures
        /// the mouse on the whole card (so param sliders/combos inside the
        /// card don't steal subsequent move/up events), visually follows the
        /// cursor vertically while dragging, and drops the effect into its
        /// new position based on where the card's center ends up relative to
        /// its siblings.
        /// </summary>
        private void AttachDragHandle(TextBlock handle, Border card, int index)
        {
            handle.MouseLeftButtonDown += (s, e) =>
            {
                _dragCard = card;
                _dragIndex = index;
                _dragStartMouseY = e.GetPosition(EffectCardsPanel).Y;
                _dragTransform = new TranslateTransform();
                card.RenderTransform = _dragTransform;
                Panel.SetZIndex(card, 100);
                card.Opacity = 0.85;
                card.CaptureMouse();
                e.Handled = true;
            };
            card.MouseMove += (s, e) =>
            {
                if (_dragCard != card || e.LeftButton != MouseButtonState.Pressed || _dragTransform == null) return;
                double y = e.GetPosition(EffectCardsPanel).Y;
                _dragTransform.Y = y - _dragStartMouseY;
            };
            card.MouseLeftButtonUp += (s, e) =>
            {
                if (_dragCard != card) return;
                card.ReleaseMouseCapture();
                double dropY = e.GetPosition(EffectCardsPanel).Y;
                int newIndex = ComputeDropIndex(dropY);
                _dragCard = null;
                _dragTransform = null;
                if (newIndex != _dragIndex) MoveEffectTo(_dragIndex, newIndex);
                else RebuildEffectCards();
            };
        }

        private int ComputeDropIndex(double dropY)
        {
            var children = EffectCardsPanel.Children.OfType<Border>().ToList();
            int target = children.Count - 1;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i] == _dragCard) continue;
                var topLeft = children[i].TranslatePoint(new Point(0, 0), EffectCardsPanel);
                double center = topLeft.Y + children[i].ActualHeight / 2;
                if (dropY < center) { target = i; break; }
            }
            return Math.Clamp(target, 0, children.Count - 1);
        }

        private void MoveEffectTo(int from, int to)
        {
            if (_project == null) return;
            to = Math.Clamp(to, 0, _project.Effects.Count - 1);
            if (from == to) { RebuildEffectCards(); return; }
            var item = _project.Effects[from];
            _project.Effects.RemoveAt(from);
            _project.Effects.Insert(to, item);
            RaiseChanged();
            RebuildEffectCards();
        }

        private FrameworkElement BuildParamRow(EffectSettings settings, ParamDef def)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock { Text = def.Label, Style = (Style)FindResource("SubText"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = def.Label };
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            FrameworkElement control;
            switch (def.PType)
            {
                case "float":
                case "int":
                {
                    var slider = new Slider
                    {
                        Minimum = def.Min ?? 0, Maximum = def.Max ?? 1,
                        Value = Convert.ToDouble(settings.Params.GetValueOrDefault(def.Name, def.Default)),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    slider.ValueChanged += (s, e) =>
                    {
                        settings.Params[def.Name] = def.PType == "int" ? (object)(int)Math.Round(slider.Value) : slider.Value;
                        RaiseChanged();
                    };
                    control = slider;
                    break;
                }
                case "bool":
                {
                    var check = new CheckBox { IsChecked = settings.Params.GetValueOrDefault(def.Name, def.Default) is bool b && b, VerticalAlignment = VerticalAlignment.Center };
                    check.Checked += (s, e) => { settings.Params[def.Name] = true; RaiseChanged(); };
                    check.Unchecked += (s, e) => { settings.Params[def.Name] = false; RaiseChanged(); };
                    control = check;
                    break;
                }
                case "choice":
                {
                    var combo = new ComboBox();
                    foreach (var choice in def.Choices ?? Array.Empty<string>()) combo.Items.Add(new ComboBoxItem { Content = choice });
                    var current = settings.Params.GetValueOrDefault(def.Name, def.Default)?.ToString();
                    foreach (ComboBoxItem item in combo.Items)
                        if (string.Equals(item.Content?.ToString(), current, StringComparison.OrdinalIgnoreCase)) combo.SelectedItem = item;
                    combo.SelectionChanged += (s, e) =>
                    {
                        if (combo.SelectedItem is ComboBoxItem sel) { settings.Params[def.Name] = sel.Content.ToString() ?? ""; RaiseChanged(); }
                    };
                    control = combo;
                    break;
                }
                case "color":
                {
                    var hex = settings.Params.GetValueOrDefault(def.Name, def.Default)?.ToString() ?? "#000000";
                    var swatch = new Border
                    {
                        Width = 24, Height = 20, Background = BrushFromHex(hex), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                        HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, ToolTip = "Click to pick a color",
                    };
                    var box = new TextBox { Text = hex, Margin = new Thickness(30, 0, 0, 0), Width = 90 };
                    box.TextChanged += (s, e) =>
                    {
                        settings.Params[def.Name] = box.Text;
                        swatch.Background = BrushFromHex(box.Text);
                        RaiseChanged();
                    };
                    // Clicking the swatch opens the native Windows color picker
                    // (System.Windows.Forms.ColorDialog - GlitchFX.csproj enables
                    // UseWindowsForms for this) seeded with the swatch's current
                    // color; picking OK writes the chosen color back into the hex
                    // TextBox, which already propagates into settings.Params and
                    // the swatch itself via the TextChanged handler above.
                    swatch.MouseLeftButtonDown += (s, e) =>
                    {
                        using var colorDialog = new System.Windows.Forms.ColorDialog
                        {
                            FullOpen = true,
                            Color = System.Drawing.ColorTranslator.FromHtml(box.Text),
                        };
                        if (colorDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        {
                            var c = colorDialog.Color;
                            box.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                        }
                    };
                    var panel = new Grid();
                    panel.Children.Add(swatch);
                    panel.Children.Add(box);
                    control = panel;
                    break;
                }
                default: // string / font
                {
                    var box = new TextBox { Text = settings.Params.GetValueOrDefault(def.Name, def.Default)?.ToString() ?? "" };
                    box.TextChanged += (s, e) => { settings.Params[def.Name] = box.Text; RaiseChanged(); };
                    control = box;
                    break;
                }
            }
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            return row;
        }

        private void RaiseChanged() { if (!_suppressEvents) SettingsChanged?.Invoke(); }

        private static string ToTitleCase(string kind) =>
            string.Join(" ", kind.Split('_').Select(w => char.ToUpper(w[0]) + w.Substring(1)));

        private static Brush BrushFromHex(string hex)
        {
            try { return (Brush)new BrushConverter().ConvertFromString(hex)!; }
            catch { return Brushes.Black; }
        }
    }
}
