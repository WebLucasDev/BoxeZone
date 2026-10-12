using System;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace BoxeZone.Helpers
{
    /// <summary>
    /// Follows a mouse drag that started with a press inside a window. Reports the cursor
    /// offset from the press, in screen pixels, once it moves past the system drag threshold,
    /// so a plain click never moves anything.
    /// </summary>
    /// <remarks>
    /// The window moves under the cursor and WinUI drops pointer moves once the cursor gets
    /// ahead of it, so the cursor and the button state are read through Win32 on every frame.
    /// </remarks>
    public sealed class WindowDrag
    {
        private Win32.POINT _start;
        private Win32.POINT _last;
        private bool _isTracking;

        public event EventHandler? Started;

        public event EventHandler<PointInt32>? Moved;

        public event EventHandler? Completed;

        public bool IsDragging { get; private set; }

        /// <summary>Starts following the cursor; call it from the PointerPressed that may begin the drag.</summary>
        public void Begin()
        {
            if (_isTracking)
            {
                return;
            }

            Win32.GetCursorPos(out _start);
            _last = _start;
            _isTracking = true;
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }

        private void CompositionTarget_Rendering(object? sender, object e)
        {
            if (!Win32.IsPrimaryMouseButtonDown())
            {
                End();
                return;
            }

            Win32.GetCursorPos(out Win32.POINT cursor);
            if (cursor.X == _last.X && cursor.Y == _last.Y)
            {
                return;
            }

            _last = cursor;
            var offset = new PointInt32(cursor.X - _start.X, cursor.Y - _start.Y);

            if (!IsDragging)
            {
                (int thresholdX, int thresholdY) = Win32.GetDragThreshold();
                if (Math.Abs(offset.X) < thresholdX && Math.Abs(offset.Y) < thresholdY)
                {
                    return;
                }

                IsDragging = true;
                Started?.Invoke(this, EventArgs.Empty);
            }

            Moved?.Invoke(this, offset);
        }

        private void End()
        {
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            _isTracking = false;
            if (IsDragging)
            {
                IsDragging = false;
                Completed?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
