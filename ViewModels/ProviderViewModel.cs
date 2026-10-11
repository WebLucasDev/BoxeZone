using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BoxeZone.Services;
using BoxeZone.Services.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Storage;

namespace BoxeZone.ViewModels
{
    public enum UploadState
    {
        Idle,
        Uploading,
        Done,
        Failed,
    }

    /// <summary>A batch of dropped files finished uploading, or stopped at the first failure.</summary>
    public sealed record UploadFinishedEventArgs(
        bool Succeeded,
        IReadOnlyList<string> FileNames,
        string? Link,
        string? ErrorMessage);

    /// <summary>
    /// State of one provider: switched on/off, connected, its destination folder and the
    /// upload shown in its drop zone. Switching on a provider that is not connected starts
    /// its sign-in.
    /// </summary>
    public partial class ProviderViewModel : ObservableObject
    {
        // The browser sign-in waits for the user; give up after a while.
        private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(3);

        private static readonly char[] InvalidFolderChars = ['<', '>', ':', '"', '|', '?', '*'];

        private static readonly ResourceLoader Strings = new();

        private readonly ProviderSettings _settings;
        private readonly Action _save;
        private CancellationTokenSource? _connectCts;
        private List<StorageFile> _failedFiles = [];
        private string? _uploadedLink;
        private bool _isRestoring;

        public ProviderViewModel(string id, string name, string logoFile, IStorageProvider provider, ProviderSettings settings, Action save)
        {
            Id = id;
            Name = name;
            Logo = new Uri($"ms-appx:///Assets/Providers/{logoFile}");
            Provider = provider;
            _settings = settings;
            _save = save;
            FileName = string.Empty;
            ErrorMessage = string.Empty;
            FolderDraft = string.Empty;
            TargetFolder = settings.TargetFolder;
        }

        public event EventHandler<UploadFinishedEventArgs>? UploadFinished;

        public string Id { get; }

        public string Name { get; }

        /// <summary>Official logo (SVG) shown in the 28 px provider icon.</summary>
        public Uri Logo { get; }

        public IStorageProvider Provider { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Status), nameof(IsDropZoneVisible))]
        public partial bool IsEnabled { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Status), nameof(IsDropZoneVisible), nameof(AccountStatus), nameof(AccountAction), nameof(CanChangeAccount))]
        public partial bool IsConnecting { get; set; }

        /// <summary>Mirrors <see cref="IStorageProvider.IsConnected"/>, which is not observable.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(AccountStatus), nameof(AccountAction))]
        public partial bool IsConnected { get; set; }

        /// <summary>Why the last sign-in failed, shown as the status while the switch is off.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Status))]
        public partial string? ConnectError { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Status), nameof(IsIdle), nameof(IsUploading), nameof(IsDone), nameof(IsFailed))]
        public partial UploadState State { get; set; }

        /// <summary>Upload progress, 0 to 100.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProgressLabel))]
        public partial double Progress { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DoneLabel), nameof(FailedLabel))]
        public partial string FileName { get; set; }

        /// <summary>One-line reason shown under a failed upload.</summary>
        [ObservableProperty]
        public partial string ErrorMessage { get; set; }

        /// <summary>Destination folder, like "/BoxeZone".</summary>
        [ObservableProperty]
        public partial string TargetFolder { get; set; }

        [ObservableProperty]
        public partial bool IsEditingFolder { get; set; }

        [ObservableProperty]
        public partial string FolderDraft { get; set; }

        [ObservableProperty]
        public partial string? FolderError { get; set; }

        public bool IsDropZoneVisible => IsEnabled && !IsConnecting;

        public bool IsIdle => State == UploadState.Idle;

        public bool IsUploading => State == UploadState.Uploading;

        public bool IsDone => State == UploadState.Done;

        public bool IsFailed => State == UploadState.Failed;

        public string ProgressLabel => $"{Progress:0}%";

        public string DoneLabel => string.Format(Strings.GetString("UploadDoneFormat"), FileName);

        public string FailedLabel => string.Format(Strings.GetString("UploadFailedFormat"), FileName);

        public string Status =>
            IsConnecting ? Strings.GetString("StatusConnecting")
            : !IsEnabled ? ConnectError ?? Strings.GetString("StatusOff")
            : IsUploading ? Strings.GetString("StatusUploading")
            : Strings.GetString("StatusReady");

        /// <summary>Settings: "Conectado · e-mail" / "Não conectado".</summary>
        public string AccountStatus =>
            IsConnecting ? Strings.GetString("StatusConnecting")
            : !IsConnected ? Strings.GetString("AccountNotConnected")
            : Provider.AccountEmail is { Length: > 0 } email ? string.Format(Strings.GetString("AccountConnectedFormat"), email)
            : Strings.GetString("AccountConnected");

        public string AccountAction => Strings.GetString(IsConnected ? "AccountDisconnect" : "AccountConnect");

        public bool CanChangeAccount => !IsConnecting;

        /// <summary>
        /// At startup: reconnects from the saved session and, if that works, switches the
        /// provider back on as it was. Never opens the browser.
        /// </summary>
        public async Task RestoreAsync()
        {
            try
            {
                await Provider.RestoreAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Offline at startup or similar: the user can still sign in by switching the provider on.
                Debug.WriteLine($"{Id}: restore failed: {ex.GetType().Name}");
            }

            RefreshConnection();
            if (_settings.Enabled && IsConnected)
            {
                _isRestoring = true;
                IsEnabled = true;
                _isRestoring = false;
            }
        }

        public async Task UploadFilesAsync(IReadOnlyList<StorageFile> files)
        {
            if (IsUploading || files.Count == 0)
            {
                return;
            }

            for (int i = 0; i < files.Count; i++)
            {
                StorageFile file = files[i];
                FileName = file.Name;
                Progress = 0;
                State = UploadState.Uploading;

                try
                {
                    // Progress<T> reports on the UI thread (it captures the DispatcherQueue context).
                    var progress = new Progress<double>(value => Progress = value);
                    _uploadedLink = await Provider.UploadAsync(file, TargetFolder, progress, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // A failed upload only affects this provider's drop zone.
                    _failedFiles = files.Skip(i).ToList();
                    ErrorMessage = DescribeError(ex);
                    State = UploadState.Failed;
                    RefreshConnection();
                    UploadFinished?.Invoke(this, new UploadFinishedEventArgs(false, [file.Name], null, ErrorMessage));
                    return;
                }
            }

            _failedFiles = [];
            State = UploadState.Done;
            UploadFinished?.Invoke(this, new UploadFinishedEventArgs(true, files.Select(f => f.Name).ToList(), _uploadedLink, null));
        }

        [RelayCommand]
        private async Task RetryAsync()
        {
            if (!Provider.IsConnected)
            {
                await ConnectAsync();
                if (!Provider.IsConnected)
                {
                    return;
                }
            }

            await UploadFilesAsync(_failedFiles);
        }

        [RelayCommand]
        private void OpenUploaded()
        {
            if (_uploadedLink is not null)
            {
                Process.Start(new ProcessStartInfo(_uploadedLink) { UseShellExecute = true });
            }
        }

        /// <summary>Settings: "Conectar" signs in; "Desconectar" signs out and switches the provider off.</summary>
        [RelayCommand]
        private async Task ToggleAccountAsync()
        {
            if (IsConnected)
            {
                await Provider.DisconnectAsync();
                RefreshConnection();
                IsEnabled = false;
                ConnectError = null;
            }
            else
            {
                await ConnectAsync();
            }
        }

        [RelayCommand]
        private void EditFolder()
        {
            FolderDraft = TargetFolder;
            FolderError = null;
            IsEditingFolder = true;
        }

        [RelayCommand]
        private void SaveFolder()
        {
            string[] segments = FolderDraft.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();

            if (segments.Length == 0 || segments.Any(s => s.IndexOfAny(InvalidFolderChars) >= 0 || s.Any(char.IsControl)))
            {
                FolderError = Strings.GetString("FolderInvalid");
                return;
            }

            TargetFolder = "/" + string.Join('/', segments);
            _settings.TargetFolder = TargetFolder;
            _save();
            FolderError = null;
            IsEditingFolder = false;
        }

        [RelayCommand]
        private void CancelFolderEdit()
        {
            FolderError = null;
            IsEditingFolder = false;
        }

        partial void OnIsEnabledChanged(bool value)
        {
            if (!_isRestoring)
            {
                _settings.Enabled = value;
                _save();
            }

            if (!value)
            {
                // Switching off while the browser sign-in is open cancels it.
                _connectCts?.Cancel();
                return;
            }

            ConnectError = null;
            if (!Provider.IsConnected && !IsConnecting)
            {
                _ = ConnectAsync();
            }
        }

        private async Task ConnectAsync()
        {
            using var cts = new CancellationTokenSource(SignInTimeout);
            _connectCts = cts;
            IsConnecting = true;
            ConnectError = null;
            try
            {
                await Provider.ConnectAsync(cts.Token);
            }
            catch (Exception ex)
            {
                ConnectError = ex is OperationCanceledException ? Strings.GetString("StatusSignInCanceled") : DescribeError(ex);
            }
            finally
            {
                _connectCts = null;
                IsConnecting = false;
                RefreshConnection();
            }

            if (!Provider.IsConnected)
            {
                IsEnabled = false;
            }
        }

        private void RefreshConnection()
        {
            IsConnected = Provider.IsConnected;

            // The e-mail can change without IsConnected changing (another account).
            OnPropertyChanged(nameof(AccountStatus));
        }

        private static string DescribeError(Exception ex)
        {
            StorageErrorKind kind = ex is StorageProviderException storage ? storage.Kind : StorageErrorKind.Unknown;
            return Strings.GetString(kind switch
            {
                StorageErrorKind.NotConfigured => "ErrorNotConfigured",
                StorageErrorKind.AccessDenied => "ErrorAccessDenied",
                StorageErrorKind.SessionExpired => "ErrorSessionExpired",
                StorageErrorKind.Offline => "ErrorOffline",
                StorageErrorKind.QuotaExceeded => "ErrorQuotaExceeded",
                _ => "ErrorUnknown",
            });
        }
    }
}
