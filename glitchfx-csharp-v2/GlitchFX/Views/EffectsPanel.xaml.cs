using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using GlitchFX.Models;

namespace GlitchFX.Views
{
    /// <summary>
    /// Mirrors Python's ui/inspector/effects.py: the main effects inspector.
    /// Global beat-sync/animate/audio-reactive controls at the top, then one
    /// schema-driven "EffectCard" per effect in the project's stack (enable
    /// pill, lock/shuffle-exclude toggle, reset button, animate toggle,
    /// beat-trigger toggle, and the auto-generated ParamPanel body).
    /// Reordering uses simple Up/Down buttons in this first pass instead of
    /// full drag-and-drop (see README "Next steps").
    /// </summary>
    public partial class EffectsPanel : UserControl
    {
        public event Action? SettingsChanged;
        public event Action<string>? RandomizeOneRequested;

        private ProjectSettings? _project;
        private bool _suppressEvents;

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
        }

        private void LoadAudioButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Audio Files|*.mp3;*.wav;*.aac;*.m4a;*.flac|All Files|*.*" };
            if (dialog.ShowDialog() == true)
            {
                LoadAudioRequested?.Invoke(dialog.FileName);
            }
        }
        public event Action<string>? LoadAudioRequested;

        // ---- Per-effect cards ----

        private void RebuildEffectCards()
        {
            EffectCardsPanel.Children.Clear();
            if (_project == null) return;
            for (int i = 0; i < _project.Effects.Count; i++)
            {
                EffectCardsPanel.Children.Add(BuildEffectCard(_project.Effects[i], i));
            }
        }

        private Border BuildEffectCard(EffectSettings settings, int index)
        {
            var card = new Border { Style = (Style)FindResource("CardBorder") };
            var stack = new StackPanel();
            card.Child = stack;

            // Header row: title, enable toggle, reset, lock, randomize-one, up/down
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int c = 0; c < 5; c++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock { Text = ToTitleCase(settings.Kind), Style = (Style)FindResource("CardHeaderText"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(title, 0);
            header.Children.Add(title);

            var enableToggle = new CheckBox { Content = "On", IsChecked = settings.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            enableToggle.Checked += (s, e) => { settings.Enabled = true; RaiseChanged(); };
            enableToggle.Unchecked += (s, e) => { settings.Enabled = false; RaiseChanged(); };
            Grid.SetColumn(enableToggle, 1);
            header.Children.Add(enableToggle);

            var lockToggle = new CheckBox { Content = "\ud83d\udd12", ToolTip = "Lock (exclude from Randomize All)", IsChecked = settings.LockRandom, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            lockToggle.Checked += (s, e) => settings.LockRandom = true;
            lockToggle.Unchecked += (s, e) => settings.LockRandom = false;
            Grid.SetColumn(lockToggle, 2);
            header.Children.Add(lockToggle);

            var randomizeBtn = new Button { Content = "\ud83c\udfb2", ToolTip = "Randomize this effect", Style = (Style)FindResource("ToolbarButton"), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 0) };
            randomizeBtn.Click += (s, e) => { RandomizeOneRequested?.Invoke(settings.Kind); RebuildEffectCards(); };
            Grid.SetColumn(randomizeBtn, 3);
            header.Children.Add(randomizeBtn);

            var upBtn = new Button { Content = "\u2191", Style = (Style)FindResource("ToolbarButton"), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 0) };
            upBtn.Click += (s, e) => { MoveEffect(index, -1); };
            Grid.SetColumn(upBtn, 4);
            header.Children.Add(upBtn);

            var downBtn = new Button { Content = "\u2193", Style = (Style)FindResource("ToolbarButton"), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(4, 0, 0, 0) };
            downBtn.Click += (s, e) => { MoveEffect(index, 1); };
            Grid.SetColumn(downBtn, 5);
            header.Children.Add(downBtn);

            stack.Children.Add(header);

            // Animate / beat-sync row
            var subRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
            var animateToggle = new CheckBox { Content = "Animate", IsChecked = settings.Animate, Margin = new Thickness(0, 0, 12, 0) };
            animateToggle.Checked += (s, e) => { settings.Animate = true; RaiseChanged(); };
            animateToggle.Unchecked += (s, e) => { settings.Animate = false; RaiseChanged(); };
            var beatToggle = new CheckBox { Content = "Beat Trigger", IsChecked = settings.BeatSync };
            beatToggle.Checked += (s, e) => { settings.BeatSync = true; RaiseChanged(); };
            beatToggle.Unchecked += (s, e) => { settings.BeatSync = false; RaiseChanged(); };
            subRow.Children.Add(animateToggle);
            subRow.Children.Add(beatToggle);
            stack.Children.Add(subRow);

            // Param panel body
            foreach (var def in EffectSchemas.SchemaFor(settings.Kind))
            {
                stack.Children.Add(BuildParamRow(settings, def));
            }

            return card;
        }

        private FrameworkElement BuildParamRow(EffectSettings settings, ParamDef def)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock { Text = def.Label, Style = (Style)FindResource("SubText"), VerticalAlignment = VerticalAlignment.Center };
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
                    var swatch = new Border { Width = 24, Height = 20, Background = BrushFromHex(hex), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left };
                    var box = new TextBox { Text = hex, Margin = new Thickness(30, 0, 0, 0), Width = 90 };
                    box.TextChanged += (s, e) =>
                    {
                        settings.Params[def.Name] = box.Text;
                        swatch.Background = BrushFromHex(box.Text);
                        RaiseChanged();
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

        private void MoveEffect(int index, int delta)
        {
            if (_project == null) return;
            int newIndex = index + delta;
            if (newIndex < 0 || newIndex >= _project.Effects.Count) return;
            var item = _project.Effects[index];
            _project.Effects.RemoveAt(index);
            _project.Effects.Insert(newIndex, item);
            RaiseChanged();
            RebuildEffectCards();
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
