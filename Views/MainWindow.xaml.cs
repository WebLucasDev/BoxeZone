using System;
using BoxeZone.Helpers;
using BoxeZone.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Graphics;
using WinRT.Interop;

namespace BoxeZone.Views
{
    public sealed partial class MainWindow : Window
    {
        private const int WidgetWidth = 300;
        private const int ScreenMargin = 16;

        private readonly IntPtr _hwnd;
        private bool _isExiting;

        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            _hwnd = WindowNative.GetWindowHandle(this);

            // Window is not a DependencyObject, so x:Uid does not apply to its title.
            Title = new ResourceLoader().GetString("AppTitle");

            // Keeping the border (but not the title bar) preserves the Windows 11
            // rounded corners, shadow and 1 px frame.
            var presenter = OverlappedPresenter.Create();
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(true, false);
            AppWindow.SetPresenter(presenter);
            AppWindow.IsShownInSwitchers = false;
            AppWindow.Closing += AppWindow_Closing;

            Root.SizeChanged += (_, _) => FitToContent();
            FitToContent();

            ContentFrame.Navigate(typeof(WidgetPage), viewModel, new SuppressNavigationTransitionInfo());
        }

        public void ShowWidget()
        {
            FitToContent();
            AppWindow.Show(true);
            Activate();
        }

        public void HideWidget() => AppWindow.Hide();

        /// <summary>Closes the window for real; any other close request only hides it.</summary>
        public void CloseForExit()
        {
            _isExiting = true;
            Close();
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_isExiting)
            {
                return;
            }

            args.Cancel = true;
            HideWidget();
        }

        /// <summary>
        /// Sizes the window to the content and anchors its visible frame to the
        /// bottom-right corner of the work area. AppWindow works in physical pixels.
        /// </summary>
        private void FitToContent()
        {
            double scale = Win32.GetDpiForWindow(_hwnd) / 96.0;

            if (Root.XamlRoot is null || Root.ActualHeight <= 0)
            {
                // Before the first layout: rough size so the window does not flash at the default size.
                AppWindow.ResizeClient(new SizeInt32((int)Math.Round(WidgetWidth * scale), (int)Math.Round(80 * scale)));
            }
            else
            {
                // Without a title bar the XAML area also covers the hidden caption, so the
                // client size is not the XAML size. Resize by the difference instead.
                double extraWidth = Root.ActualWidth - Root.XamlRoot.Size.Width;
                double extraHeight = Root.ActualHeight - Root.XamlRoot.Size.Height;
                if (Math.Abs(extraWidth) >= 0.5 || Math.Abs(extraHeight) >= 0.5)
                {
                    AppWindow.Resize(new SizeInt32(
                        AppWindow.Size.Width + (int)Math.Round(extraWidth * scale),
                        AppWindow.Size.Height + (int)Math.Round(extraHeight * scale)));
                }
            }

            RectInt32 workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            Win32.RECT insets = Win32.GetInvisibleFrameInsets(_hwnd);
            int margin = (int)Math.Round(ScreenMargin * scale);
            AppWindow.Move(new PointInt32(
                workArea.X + workArea.Width - margin + insets.Right - AppWindow.Size.Width,
                workArea.Y + workArea.Height - margin + insets.Bottom - AppWindow.Size.Height));
        }
    }
}
