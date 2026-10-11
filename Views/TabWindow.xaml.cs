using BoxeZone.Helpers;
using BoxeZone.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Graphics;

namespace BoxeZone.Views
{
    /// <summary>
    /// The side tab of the widget, in a window of its own because it sticks out of the
    /// widget's rectangle. It is 15 px visible plus 8 px tucked behind the widget.
    /// </summary>
    public sealed partial class TabWindow : Window
    {
        public const double VisibleWidth = 15;
        public const double TabHeight = 80;

        public TabWindow()
        {
            InitializeComponent();

            Title = new ResourceLoader().GetString("AppTitle");
            this.ConfigureAsToolWindow();
            Win32.SetNoActivate(this.GetHwnd());

            Handle.SizeChanged += (_, _) => FitToContent();
            FitToContent();
        }

        public TabHandle TabHandle => Handle;

        private void FitToContent()
        {
            if (this.TryGetVisibleSizeForXaml(Handle, Handle.Width, Handle.Height, out SizeInt32 size))
            {
                RectInt32 bounds = this.GetVisibleBounds();
                this.SetVisibleBounds(new RectInt32(bounds.X, bounds.Y, size.Width, size.Height));
            }
            else
            {
                this.ResizeClientApproximately(Handle.Width, Handle.Height);
            }
        }
    }
}
