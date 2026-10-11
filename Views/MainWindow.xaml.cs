using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.Resources;

namespace BoxeZone.Views
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Window is not a DependencyObject, so x:Uid does not apply to its title.
            Title = new ResourceLoader().GetString("AppTitle");
        }
    }
}
