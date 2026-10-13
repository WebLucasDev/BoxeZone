using BoxeZone.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace BoxeZone.Views
{
    /// <summary>"Histórico": the last uploads, with "Abrir" and "Copiar link".</summary>
    public sealed partial class HistoryPage : Page
    {
        public HistoryPage()
        {
            InitializeComponent();

            // Same as the other pages: no focus rectangle on the first control when it opens.
            Loaded += (_, _) => Focus(FocusState.Programmatic);
        }

        public MainViewModel ViewModel { get; private set; } = null!;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            ViewModel = (MainViewModel)e.Parameter;
            base.OnNavigatedTo(e);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) => Frame.GoBack(new SuppressNavigationTransitionInfo());

        private void CloseButton_Click(object sender, RoutedEventArgs e) => App.DockService?.Hide();
    }
}
