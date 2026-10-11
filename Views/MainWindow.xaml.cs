using System;
using System.Numerics;
using BoxeZone.Helpers;
using BoxeZone.ViewModels;
using Microsoft.UI.Composition;
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

        public MainWindow(MainViewModel viewModel)
        {
            ViewModel = viewModel;
            InitializeComponent();

            // Window is not a DependencyObject, so x:Uid does not apply to its title.
            Title = new ResourceLoader().GetString("AppTitle");
            this.ConfigureAsToolWindow();

            Root.SizeChanged += (_, _) => ContentSizeChanged?.Invoke(this, EventArgs.Empty);
            ContentFrame.Navigate(typeof(WidgetPage), viewModel, new SuppressNavigationTransitionInfo());

            // Widget <-> Settings: the window resizes at once, the new page fades in.
            ContentFrame.Navigated += (_, _) => FadeIn(ContentFrame);
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
