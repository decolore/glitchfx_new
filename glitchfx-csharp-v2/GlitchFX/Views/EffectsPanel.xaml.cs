using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using GlitchFX.Models;

namespace GlitchFX.Views
{
    /// <summary>
    /// Mirrors Python's ui/inspector/effects.py: the main effects inspector.
    /// Global beat-sync/animate/audio-reactive controls at the top, then one
    /// schema-driven "EffectCard" per effect in the project's stack. Each
    /// card has a two-row header: row 1 is collapse chevron + drag handle +
    /// title + enable toggle (kept short so long effect names always have
    /// room to render in full instead of being clipped), and row 2 is a
    /// right-aligned strip of lock/randomize/up/down actions that stays
    /// visible even while the card is collapsed. Cards default to collapsed
    /// on every Bind() (app startup/undo/redo/preset load) to save space,
    /// and can be reordered either with the Up/Down buttons or by dragging
    /// the "⋮⋮" handle in the header.
    /// </summary>
    public partial class EffectsPanel : UserControl
    {
        public event Action? SettingsChanged;
        public event Action<string>? RandomizeOneRequested;

        private ProjectSettings? _project;
        private bool _suppressEvents;

        // Which effects are currently shown collapsed. Keyed by EffectSettings
        // reference (not index) so state survives reordering; reset to "all
        // collapsed" every time a whole project is (re)bound.
        private readonly HashSet<EffectSettings> _collapsedEffects = new();

        // Drag-to-reorder state for the "⋮⋮" handle.
        private Border? _dragCard;
        private int _dragIndex;
        private double _dragStartMouseY;
        private TranslateTransform? _dragTransform;

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

            // Start every effect card collapsed to save space; the user can
            // expand the ones they're actively tweaking.
            _collapsedEffects.Clear();
            foreach (var effect in project.Effects) _collapsedEffects.Add(effect);

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
            _dragCard = null;
            if (_project == null) return;
            for (int i = 0; i < _project.Effects.Count; i++)
            {
                EffectCardsPanel.Children.Add(BuildEffectCard(_project.Effects[i], i));
            }
        }

        private Border BuildEffectCard(EffectSettings settings, int index)
        {
            var card = new Border { Style = (Style)FindResource("CardBorder") };
            // Locked effects (excluded from Randomize All) are shown dimmed so
            // the lock state is visible at a glance, even while the card is
            // collapsed and the lock icon itself isn't in view.
            card.Opacity = settings.LockRandom ? 0.55 : 1.0;
            var stack = new StackPanel();
            card.Child = stack;

            bool collapsed = _collapsedEffects.Contains(settings);

            // Row 1: collapse chevron, drag handle, title, enable toggle. Kept
            // to just these four so the title column always has room to show
            // even the longest effect names (e.g. "Chromatic Aberration") in full.
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 0 collapse chevron
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 1 drag handle
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 2 title
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 3 enable toggle

            // Param panel body (built before the header so the collapse
            // chevron's click handler can close over it).
            var body = new StackPanel { Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible };

            var collapseBtn = new Button
            {
                Content = collapsed ? "\u25B8" : "\u25BE",
                Style = (Style)FindResource("ToolbarButton"),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = collapsed ? "Expand" : "Collapse",
            };
            collapseBtn.Click += (s, e) =>
            {
                bool nowCollapsed = body.Visibility == Visibility.Visible;
                body.Visibility = nowCollapsed ? Visibility.Collapsed : Visibility.Visible;
                collapseBtn.Content = nowCollapsed ? "\u25B8" : "\u25BE";
                collapseBtn.ToolTip = nowCollapsed ? "Expand" : "Collapse";
                if (nowCollapsed) _collapsedEffects.Add(settings); else _collapsedEffects.Remove(settings);
            };
            Grid.SetColumn(collapseBtn, 0);
            header.Children.Add(collapseBtn);

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

            string displayName = ToTitleCase(settings.Kind);
            var title = new TextBlock
            {
                Text = displayName,
                Style = (Style)FindResource("CardHeaderText"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = displayName,
            };
            Grid.SetColumn(title, 2);
            header.Children.Add(title);

            var enableToggle = new CheckBox { IsChecked = settings.Enabled, ToolTip = "Enabled", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            enableToggle.Checked += (s, e) => { settings.Enabled = true; RaiseChanged(); };
            enableToggle.Unchecked += (s, e) => { settings.Enabled = false; RaiseChanged(); };
            Grid.SetColumn(enableToggle, 3);
            header.Children.Add(enableToggle);

            stack.Children.Add(header);

            // Row 2: lock, randomize-one, up/down — kept outside the collapsible
            // body (so they're reachable without expanding) but on their own
            // line instead of crammed into row 1.
            var actionsRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };

            var lockToggle = new CheckBox { Content = "\ud83d\udd12", Style = (Style)FindResource("IconToggleCheck"), ToolTip = "Lock (exclude from Randomize All)", IsChecked = settings.LockRandom, VerticalAlignment = VerticalAlignment.Center };
            lockToggle.Checked += (s, e) => { settings.LockRandom = true; card.Opacity = 0.55; };
            lockToggle.Unchecked += (s, e) => { settings.LockRandom = false; card.Opacity = 1.0; };
            actionsRow.Children.Add(lockToggle);

            var randomizeBtn = new Button { Content = "\ud83c\udfb2", ToolTip = "Randomize this effect", Style = (Style)FindResource("ToolbarButton"), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 0) };
            randomizeBtn.Click += (s, e) => { RandomizeOneRequested?.Invoke(settings.Kind); RebuildEffectCards(); };
            actionsRow.Children.Add(randomizeBtn);

            var upBtn = new Button { Content = "\u2191", ToolTip = "Move up", Style = (Style)FindResource("ToolbarButton"), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 0) };
            upBtn.Click += (s, e) => { MoveEffect(index, -1); };
            actionsRow.Children.Add(upBtn);

            var downBtn = new Button { Content = "\u2193", ToolTip = "Move down", Style = (Style)FindResource("ToolbarButton"), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 0) };
            downBtn.Click += (s, e) => { MoveEffect(index, 1); };
            actionsRow.Children.Add(downBtn);

            stack.Children.Add(actionsRow);

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
            body.Children.Add(subRow);

            foreach (var def in EffectSchemas.SchemaFor(settings.Kind))
            {
                body.Children.Add(BuildParamRow(settings, def));
            }

            stack.Children.Add(body);

            AttachDragHandle(dragHandle, card, index);

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
