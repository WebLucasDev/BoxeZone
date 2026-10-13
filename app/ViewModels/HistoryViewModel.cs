using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BoxeZone.Services;
using BoxeZone.Services.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.ApplicationModel.Resources;

namespace BoxeZone.ViewModels
{
    /// <summary>The "Histórico" page: the last uploads, newest first, with "Abrir" and "Copiar link".</summary>
    public partial class HistoryViewModel : ObservableObject
    {
        private readonly HistoryService _history;
        private readonly ShareLinkService _shareLinks;
        private readonly IReadOnlyList<ProviderViewModel> _providers;
        private readonly List<HistoryEntry> _entries;

        public HistoryViewModel(HistoryService history, ShareLinkService shareLinks, IReadOnlyList<ProviderViewModel> providers)
        {
            _history = history;
            _shareLinks = shareLinks;
            _providers = providers;
            _entries = history.Load();
            foreach (HistoryEntry entry in _entries)
            {
                if (ToItem(entry) is { } item)
                {
                    Items.Add(item);
                }
            }

            foreach (ProviderViewModel provider in providers)
            {
                provider.UploadFinished += Provider_UploadFinished;
            }
        }

        public ObservableCollection<HistoryItemViewModel> Items { get; } = [];

        public bool IsEmpty => Items.Count == 0;

        [RelayCommand]
        private async Task ClearAsync()
        {
            _entries.Clear();
            Items.Clear();
            OnPropertyChanged(nameof(IsEmpty));
            await _history.SaveAsync(_entries);
        }

        private async void Provider_UploadFinished(object? sender, UploadFinishedEventArgs e)
        {
            if (!e.Succeeded || sender is not ProviderViewModel provider || e.Items is not { Count: > 0 } items)
            {
                return;
            }

            var entry = new HistoryEntry(provider.Id, e.Summary, DateTimeOffset.Now, e.Link, items);
            _entries.Insert(0, entry);
            if (_entries.Count > HistoryService.MaxEntries)
            {
                _entries.RemoveRange(HistoryService.MaxEntries, _entries.Count - HistoryService.MaxEntries);
            }

            if (ToItem(entry) is { } item)
            {
                Items.Insert(0, item);
                while (Items.Count > HistoryService.MaxEntries)
                {
                    Items.RemoveAt(Items.Count - 1);
                }
            }

            OnPropertyChanged(nameof(IsEmpty));
            await _history.SaveAsync(_entries);
        }

        private HistoryItemViewModel? ToItem(HistoryEntry entry) =>
            _providers.FirstOrDefault(p => p.Id == entry.ProviderId) is { } provider
                ? new HistoryItemViewModel(entry, provider, _shareLinks)
                : null;
    }

    /// <summary>One line of the history.</summary>
    public partial class HistoryItemViewModel : ObservableObject
    {
        private static readonly ResourceLoader Strings = new();

        private readonly HistoryEntry _entry;
        private readonly ShareLinkService _shareLinks;
        private readonly DispatcherQueueTimer _feedbackTimer;

        public HistoryItemViewModel(HistoryEntry entry, ProviderViewModel provider, ShareLinkService shareLinks)
        {
            _entry = entry;
            _shareLinks = shareLinks;
            Provider = provider;
            CopyLinkLabel = Strings.GetString("CopyLink");
            _feedbackTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _feedbackTimer.IsRepeating = false;
            _feedbackTimer.Tick += (_, _) => CopyLinkLabel = Strings.GetString("CopyLink");
        }

        public ProviderViewModel Provider { get; }

        public Uri Logo => Provider.Logo;

        public string Title => _entry.Title;

        /// <summary>"Hoje, 14:32", "Ontem, 09:10" or "12/10, 18:05".</summary>
        public string WhenText
        {
            get
            {
                DateTime when = _entry.When.LocalDateTime;
                string time = when.ToString("HH:mm");
                return when.Date == DateTime.Today ? string.Format(Strings.GetString("HistoryTodayFormat"), time)
                    : when.Date == DateTime.Today.AddDays(-1) ? string.Format(Strings.GetString("HistoryYesterdayFormat"), time)
                    : $"{when:dd/MM}, {time}";
            }
        }

        public string Subtitle => $"{Provider.Name} · {WhenText}";

        public bool CanOpen => !string.IsNullOrEmpty(_entry.OpenUrl);

        /// <summary>"Copiar link", "Copiando…", "Link copiado" or the failure, for a moment.</summary>
        [ObservableProperty]
        public partial string CopyLinkLabel { get; set; }

        [RelayCommand]
        private void Open()
        {
            if (CanOpen)
            {
                Process.Start(new ProcessStartInfo(_entry.OpenUrl!) { UseShellExecute = true });
            }
        }

        [RelayCommand]
        private async Task CopyLinkAsync()
        {
            _feedbackTimer.Stop();
            CopyLinkLabel = Strings.GetString("CopyLinkCopying");
            ShareResult result = await _shareLinks.CopyAsync(Provider.Provider, _entry.Items, CancellationToken.None);
            CopyLinkLabel = result.Succeeded
                ? Strings.GetString("CopyLinkCopied")
                : ShareLinkService.DescribeReason(result.Error!.Value, Provider.Name);
            _feedbackTimer.Interval = TimeSpan.FromSeconds(result.Succeeded ? 2 : 4);
            _feedbackTimer.Start();
        }
    }
}
