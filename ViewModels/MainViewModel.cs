using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Windows.ApplicationModel.Resources;

namespace BoxeZone.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        public MainViewModel()
        {
            var strings = new ResourceLoader();
            Providers =
            [
                new ProviderViewModel("gdrive", strings.GetString("ProviderGoogleDrive"), "G"),
                new ProviderViewModel("onedrive", strings.GetString("ProviderOneDrive"), "O"),
                new ProviderViewModel("dropbox", strings.GetString("ProviderDropbox"), "D"),
            ];

            foreach (ProviderViewModel provider in Providers)
            {
                provider.PropertyChanged += Provider_PropertyChanged;
            }
        }

        public IReadOnlyList<ProviderViewModel> Providers { get; }

        /// <summary>Switched-on providers, in list order (the compact drop panel shows only these).</summary>
        public ObservableCollection<ProviderViewModel> EnabledProviders { get; } = [];

        public bool HasNoProviderEnabled => EnabledProviders.Count == 0;

        private void Provider_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ProviderViewModel.IsEnabled))
            {
                return;
            }

            EnabledProviders.Clear();
            foreach (ProviderViewModel provider in Providers.Where(p => p.IsEnabled))
            {
                EnabledProviders.Add(provider);
            }

            OnPropertyChanged(nameof(HasNoProviderEnabled));
        }
    }
}
