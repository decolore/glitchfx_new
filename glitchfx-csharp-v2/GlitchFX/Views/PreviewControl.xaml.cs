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
