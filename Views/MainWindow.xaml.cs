using System;
using BoxeZone.Helpers;
using BoxeZone.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace BoxeZone.Views
{
    public enum MainWindowMode
    {
        Widget,
        Strip,
        Compact,
    }

    /// <summary>
    /// The widget window. It only switches content and sizes itself to it;
    /// placement and state changes belong to WindowDockService.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private const double StripWidth = 12;
        private const double StripHoverWidth = 16;

        public MainWindow(MainViewModel viewModel)
        {
            ViewModel = viewModel;
            InitializeComponent();

            // Window is not a DependencyObject, so x:Uid does not apply to its title.
            Title = new ResourceLoader().GetString("AppTitle");
            this.ConfigureAsToolWindow();

            Root.SizeChanged += (_, _) => ContentSizeChanged?.Invoke(this, EventArgs.Empty);
            ContentFrame.Navigate(typeof(WidgetPage), viewModel, new SuppressNavigationTransitionInfo());
        }

        public event EventHandler? ContentSizeChanged;

        public event EventHandler? StripClicked;

        public event EventHandler? StripDragEntered;

        public MainViewModel ViewModel { get; }

        public MainWindowMode Mode { get; private set; }

        public void SetMode(MainWindowMode mode)
        {
            Mode = mode;
            ContentFrame.Visibility = mode == MainWindowMode.Widget ? Visibility.Visible : Visibility.Collapsed;
            DockedStrip.Visibility = mode == MainWindowMode.Strip ? Visibility.Visible : Visibility.Collapsed;
            DockedStrip.Width = StripWidth;
            CompactPanel.Visibility = mode == MainWindowMode.Compact ? Visibility.Visible : Visibility.Collapsed;

            // Measure now so the window can be resized before the next frame.
            Root.UpdateLayout();
        }

        /// <summary>Visible frame size that fits the current content, in physical pixels.</summary>
        public SizeInt32 GetSizeForContent()
        {
            if (Root.ActualWidth > 0 && this.TryGetVisibleSizeForXaml(Root, Root.ActualWidth, Root.ActualHeight, out SizeInt32 size))
            {
                return size;
            }

            // Content not loaded yet: approximate, and let the next SizeChanged correct it.
            this.ResizeClientApproximately(ContentFrame.Width, 80);
            RectInt32 bounds = this.GetVisibleBounds();
            return new SizeInt32(bounds.Width, bounds.Height);
        }

        private void DockedStrip_PointerEntered(object sender, PointerRoutedEventArgs e) => DockedStrip.Width = StripHoverWidth;

        private void DockedStrip_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            // Widening the strip resizes the window under the cursor, which raises a spurious
            // PointerExited. Only narrow it again once the cursor has really left.
            Win32.GetCursorPos(out Win32.POINT cursor);
            RectInt32 bounds = this.GetVisibleBounds();
            bool isInside =
                cursor.X >= bounds.X && cursor.X < bounds.X + bounds.Width &&
                cursor.Y >= bounds.Y && cursor.Y < bounds.Y + bounds.Height;
            if (!isInside)
            {
                DockedStrip.Width = StripWidth;
            }
        }

        private void DockedStrip_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.GetCurrentPoint(DockedStrip).Properties.IsLeftButtonPressed)
            {
                StripClicked?.Invoke(this, EventArgs.Empty);
            }
        }

        private void DockedStrip_DragEnter(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
            }

            StripDragEntered?.Invoke(this, EventArgs.Empty);
        }
    }
}
