using BoxeZone.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace BoxeZone.Views
{
    /// <summary>The widget: header, provider rows with drop zones, footer.</summary>
    public sealed partial class WidgetPage : Page
    {
        public WidgetPage()
        {
            InitializeComponent();

            // WinUI gives the first control keyboard focus (with a focus rectangle) when the
            // window activates. Park focus on the page instead; Tab still reaches the controls.
            Loaded += (_, _) => Focus(FocusState.Programmatic);
        }

        public MainViewModel ViewModel { get; private set; } = null!;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            ViewModel = (MainViewModel)e.Parameter;
            base.OnNavigatedTo(e);
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => App.DockService?.Dock();

        private void CloseButton_Click(object sender, RoutedEventArgs e) => App.DockService?.Hide();

        private void HistoryLink_Click(object sender, RoutedEventArgs e) =>
            Frame.Navigate(typeof(HistoryPage), ViewModel, new SuppressNavigationTransitionInfo());

        private void SettingsLink_Click(object sender, RoutedEventArgs e) =>
            Frame.Navigate(typeof(SettingsPage), ViewModel, new SuppressNavigationTransitionInfo());
    }
}
