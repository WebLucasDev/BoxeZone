using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace BoxeZone.Views.Controls
{
    /// <summary>
    /// Side tab of the widget. A click collapses the widget to the docked strip; moving the
    /// widget is done by dragging its body (see <c>WindowDrag</c>).
    /// </summary>
    public sealed partial class TabHandle : UserControl
    {
        private bool _isPointerOver;

        public TabHandle()
        {
            InitializeComponent();
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
        }

        public event EventHandler? Clicked;

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

        /// <summary>
        /// Acts on press, like the docked strip: the tab window never activates
        /// (WS_EX_NOACTIVATE), and the release did not reliably reach it.
        /// </summary>
        protected override void OnPointerPressed(PointerRoutedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }

            e.Handled = true;
            VisualStateManager.GoToState(this, "Pressed", false);
            Clicked?.Invoke(this, EventArgs.Empty);
            UpdateState();
        }

        private void UpdateState() => VisualStateManager.GoToState(this, _isPointerOver ? "PointerOver" : "Normal", true);
    }
}
