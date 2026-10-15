using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using BoxeZone.Services;
using BoxeZone.Services.Storage;
using BoxeZone.Services.Upload;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Windows.ApplicationModel.Resources;

namespace BoxeZone.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        public MainViewModel(
            SettingsService settingsService,
            SettingsViewModel settings,
            ShareLinkService shareLinks,
            UploadQueueStore queueStore,
            IStorageProvider googleDrive,
            IStorageProvider oneDrive,
            IStorageProvider dropbox,
            IStorageProvider mega)
        {
            Settings = settings;
            var strings = new ResourceLoader();
            AppSettings saved = settingsService.Settings;
            Providers =
            [
                new ProviderViewModel("gdrive", strings.GetString("ProviderGoogleDrive"), "google-drive.svg", googleDrive, saved.GetProvider("gdrive"), settingsService.Save, shareLinks, queueStore),
                new ProviderViewModel("onedrive", strings.GetString("ProviderOneDrive"), "onedrive.svg", oneDrive, saved.GetProvider("onedrive"), settingsService.Save, shareLinks, queueStore),
                new ProviderViewModel("dropbox", strings.GetString("ProviderDropbox"), "dropbox.svg", dropbox, saved.GetProvider("dropbox"), settingsService.Save, shareLinks, queueStore),
                new ProviderViewModel("mega", strings.GetString("ProviderMega"), "mega.svg", mega, saved.GetProvider("mega"), settingsService.Save, shareLinks, queueStore),
            ];

            foreach (ProviderViewModel provider in Providers)
            {
                provider.PropertyChanged += Provider_PropertyChanged;
            }

            History = new HistoryViewModel(new HistoryService(), shareLinks, Providers);
        }

        public SettingsViewModel Settings { get; }

        /// <summary>The "Histórico" page.</summary>
        public HistoryViewModel History { get; }

        public IReadOnlyList<ProviderViewModel> Providers { get; }

        /// <summary>Switched-on providers, in list order (the compact drop panel shows only these).</summary>
        public ObservableCollection<ProviderViewModel> EnabledProviders { get; } = [];

        public bool HasNoProviderEnabled => EnabledProviders.Count == 0;

        /// <summary>
        /// Reconnects every provider with a saved session, without opening the browser, and
        /// switches back on the ones that were on.
        /// </summary>
        public Task RestoreSessionsAsync() => Task.WhenAll(Providers.Select(p => p.RestoreAsync()));

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
