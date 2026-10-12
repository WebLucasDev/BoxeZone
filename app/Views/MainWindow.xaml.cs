using System;
using System.Numerics;
using BoxeZone.Helpers;
using BoxeZone.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
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
        private const double WidgetWidth = 300;

        private readonly DispatcherQueueTimer _stripHoverTimer;

        public MainWindow(MainViewModel viewModel)
        {
            ViewModel = viewModel;
            InitializeComponent();

            _stripHoverTimer = DispatcherQueue.CreateTimer();
            _stripHoverTimer.Interval = TimeSpan.FromMilliseconds(50);
            _stripHoverTimer.Tick += (_, _) => NarrowStripIfCursorLeft();

            // Window is not a DependencyObject, so x:Uid does not apply to its title.
            Title = new ResourceLoader().GetString("AppTitle");
            this.ConfigureAsToolWindow();

            // The invisible resize border left of the frame sits over the side tab and takes its clicks.
            Win32.ReportInvisibleFrameClicks(this.GetHwnd(), point => InvisibleFrameClicked?.Invoke(this, new PointInt32(point.X, point.Y)));

            Root.SizeChanged += (_, _) => ContentSizeChanged?.Invoke(this, EventArgs.Empty);

            // Presses that no control handled (buttons, switches, links and text boxes all do) move the widget.
            Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Root_PointerPressed), handledEventsToo: false);
            ContentFrame.Navigate(typeof(WidgetPage), viewModel, new SuppressNavigationTransitionInfo());

            // Widget <-> Settings: the window resizes at once, the new page fades in.
            ContentFrame.Navigated += (_, _) => FadeIn(ContentFrame);
        }

        public event EventHandler? ContentSizeChanged;

        public event EventHandler? StripClicked;

        public event EventHandler? StripDragEntered;

        /// <summary>A press on the invisible border around the visible frame, in screen pixels.</summary>
        public event EventHandler<PointInt32>? InvisibleFrameClicked;

        public MainViewModel ViewModel { get; }

        public MainWindowMode Mode { get; private set; }

        /// <summary>Dragging the open widget by any part that is not a control.</summary>
        public WindowDrag Drag { get; } = new();

        public void SetMode(MainWindowMode mode)
        {
            Mode = mode;
            ContentFrame.Visibility = mode == MainWindowMode.Widget ? Visibility.Visible : Visibility.Collapsed;
            DockedStrip.Visibility = mode == MainWindowMode.Strip ? Visibility.Visible : Visibility.Collapsed;
            DockedStrip.Width = StripWidth;
            CompactPanel.Visibility = mode == MainWindowMode.Compact ? Visibility.Visible : Visibility.Collapsed;

            // Measure now so the window can be resized before the next frame.
            Root.UpdateLayout();

            FadeIn(mode switch
            {
                MainWindowMode.Widget => ContentFrame,
                MainWindowMode.Strip => DockedStrip,
                _ => CompactPanel,
            });
        }

        /// <summary>
        /// Short fade (180 ms) on the content that just appeared. Only the content fades, not
        /// the container background, so the dark theme does not flash the lighter backdrop.
        /// </summary>
        private static void FadeIn(UIElement element)
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            Compositor compositor = visual.Compositor;
            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(1, 1, compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0.58f, 1)));
            fade.Duration = TimeSpan.FromMilliseconds(180);
            visual.StartAnimation("Opacity", fade);
        }

        /// <summary>Opens Settings in place of the widget (the window grows to its width).</summary>
        public void ShowSettingsPage()
        {
            if (ContentFrame.Content is not SettingsPage)
            {
                ContentFrame.Navigate(typeof(SettingsPage), ViewModel, new SuppressNavigationTransitionInfo());
            }
        }

        public void ShowWidgetPage()
        {
            if (ContentFrame.Content is not WidgetPage && ContentFrame.CanGoBack)
            {
                ContentFrame.GoBack(new SuppressNavigationTransitionInfo());
            }
        }

        /// <summary>The backdrop follows the theme of the window's root content, so it is set there.</summary>
        public void ApplyTheme(ElementTheme theme) => ((FrameworkElement)Content).RequestedTheme = theme;

        /// <summary>Visible frame size that fits the current content, in physical pixels.</summary>
        public SizeInt32 GetSizeForContent()
        {
            if (Root.ActualWidth > 0 && this.TryGetVisibleSizeForXaml(Root, Root.ActualWidth, Root.ActualHeight, out SizeInt32 size))
            {
                return size;
            }

            // Content not loaded yet: approximate, and let the next SizeChanged correct it.
            this.ResizeClientApproximately(WidgetWidth, 80);
            RectInt32 bounds = this.GetVisibleBounds();
            return new SizeInt32(bounds.Width, bounds.Height);
        }

        private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // Mouse only: the drag follows the mouse button through Win32.
            if (Mode == MainWindowMode.Widget &&
                e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse &&
                e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed)
            {
                Drag.Begin();
            }
        }

        private void DockedStrip_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            DockedStrip.Width = StripHoverWidth;
            _stripHoverTimer.Start();
        }

        private void DockedStrip_PointerExited(object sender, PointerRoutedEventArgs e) => NarrowStripIfCursorLeft();

        /// <summary>
        /// Widening the strip resizes the window under the cursor, which raises a spurious
        /// PointerExited; after it WinUI may send no PointerExited when the cursor really leaves.
        /// So the real cursor position decides, checked on exit and on a short timer while wide.
        /// </summary>
        private void NarrowStripIfCursorLeft()
        {
            Win32.GetCursorPos(out Win32.POINT cursor);
            RectInt32 bounds = this.GetVisibleBounds();
            bool isInside =
                cursor.X >= bounds.X && cursor.X < bounds.X + bounds.Width &&
                cursor.Y >= bounds.Y && cursor.Y < bounds.Y + bounds.Height;
            if (!isInside || Mode != MainWindowMode.Strip)
            {
                DockedStrip.Width = StripWidth;
                _stripHoverTimer.Stop();
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
