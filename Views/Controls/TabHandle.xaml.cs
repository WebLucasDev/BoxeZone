using System;
using BoxeZone.Helpers;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace BoxeZone.Views.Controls
{
    /// <summary>
    /// Side tab used to drag the widget. Reports the cursor offset from where the drag
    /// started in screen pixels.
    /// </summary>
    /// <remarks>
    /// The tab window is only 23 px wide, and WinUI stops sending pointer moves once the
    /// cursor leaves it, so the drag follows the cursor and the button state through Win32
    /// on every frame instead.
    /// </remarks>
    public sealed partial class TabHandle : UserControl
    {
        private Win32.POINT _dragStart;
        private Win32.POINT _lastCursor;
        private bool _isDragging;
        private bool _isPointerOver;

        public TabHandle()
        {
            InitializeComponent();
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
        }

        public event EventHandler? DragStarted;

        public event EventHandler<PointInt32>? DragMoved;

        public event EventHandler? DragCompleted;

        protected override void OnPointerEntered(PointerRoutedEventArgs e)
        {
            _isPointerOver = true;
            UpdateState();
        }

        protected override void OnPointerExited(PointerRoutedEventArgs e)
        {
            _isPointerOver = false;
            UpdateState();
        }

        protected override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            if (_isDragging || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            Win32.GetCursorPos(out _dragStart);
            _lastCursor = _dragStart;
            _isDragging = true;
            UpdateState();
            CompositionTarget.Rendering += CompositionTarget_Rendering;
            DragStarted?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void CompositionTarget_Rendering(object? sender, object e)
        {
            if (!Win32.IsPrimaryMouseButtonDown())
            {
                EndDrag();
                return;
            }

            Win32.GetCursorPos(out Win32.POINT cursor);
            if (cursor.X == _lastCursor.X && cursor.Y == _lastCursor.Y)
            {
                return;
            }

            _lastCursor = cursor;
            DragMoved?.Invoke(this, new PointInt32(cursor.X - _dragStart.X, cursor.Y - _dragStart.Y));
        }

        private void EndDrag()
        {
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _isDragging = false;
            UpdateState();
            DragCompleted?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateState()
        {
            string state = _isDragging ? "Pressed" : _isPointerOver ? "PointerOver" : "Normal";
            VisualStateManager.GoToState(this, state, true);
        }
    }
}
