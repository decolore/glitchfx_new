using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace GlitchFX.Views
{
    /// <summary>
    /// Preview viewport. Mirrors the core interaction of Python's
    /// ui/preview_view.py (drag to reposition the transform/text overlay);
    /// simplified to drag-to-move + one resize handle for this first pass
    /// instead of the original's full 8-handle resize box.
    /// </summary>
    public partial class PreviewControl : UserControl
    {
        public event Action<double, double>? OffsetDragged; // dx, dy in normalized [-1,1] canvas space

        /// <summary>
        /// Fired when the user clicks anywhere on the preview while no video
        /// is loaded yet (PreviewImage.Source == null), so the owner can open
        /// the same file picker as the toolbar's Load button. Once a video is
        /// loaded, clicks instead go to the existing overlay drag handlers
        /// (OverlayCanvas_MouseLeftButtonDown etc.) for repositioning the text
        /// overlay/transform, so this never fires once PreviewImage has a frame.
        /// </summary>
        public event Action? LoadVideoRequested;

        private bool _dragging;
        private Point _dragStart;

        public PreviewControl()
        {
            InitializeComponent();
        }

        public void ShowFrame(BitmapSource frame)
        {
            EmptyStateText.Visibility = Visibility.Collapsed;
            PreviewImage.Source = frame;
        }

        public void ShowSelectionBox(bool visible)
        {
            SelectionBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // MouseLeftButtonDown bubbles up from OverlayCanvas (which sits on top
        // and handles it first for drag-to-reposition, without marking it
        // Handled) to this root Grid, so this still fires even when the click
        // lands on the canvas rather than directly on empty Grid space.
        private void RootGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (PreviewImage.Source == null)
            {
                LoadVideoRequested?.Invoke();
            }
        }

        private void OverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _dragStart = e.GetPosition(OverlayCanvas);
            OverlayCanvas.CaptureMouse();
        }

        private void OverlayCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var pos = e.GetPosition(OverlayCanvas);
            double dx = (pos.X - _dragStart.X) / Math.Max(OverlayCanvas.ActualWidth, 1);
            double dy = (pos.Y - _dragStart.Y) / Math.Max(OverlayCanvas.ActualHeight, 1);
            OffsetDragged?.Invoke(dx, dy);
            _dragStart = pos;
        }

        private void OverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            OverlayCanvas.ReleaseMouseCapture();
        }
    }
}
