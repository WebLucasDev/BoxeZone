using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>
    /// State of one provider: switched on/off, connected, and the upload shown in its drop zone.
    /// Switching on a provider that is not connected starts its sign-in.
    /// </summary>
    public partial class ProviderViewModel : ObservableObject
    {
        // Default destination until Settings (step 7) lets the user change it.
        private const string DefaultTargetFolder = "/BoxeZone";

        // The browser sign-in waits for the user; give up after a while.
        private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(3);

        private static readonly ResourceLoader Strings = new();

        private CancellationTokenSource? _connectCts;
        private List<StorageFile> _failedFiles = [];
        private string? _uploadedLink;

        public ProviderViewModel(string id, string name, string initial, IStorageProvider? provider)
        {
            Id = id;
            Name = name;
            Initial = initial;
            Provider = provider;
            FileName = string.Empty;
            ErrorMessage = string.Empty;
        }

        public string Id { get; }

        public string Name { get; }

        /// <summary>Letter shown in the 28 px provider icon.</summary>
        public string Initial { get; }

        /// <summary>Null while the provider is still simulated (OneDrive and Dropbox until step 6).</summary>
        public IStorageProvider? Provider { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Status), nameof(IsDropZoneVisible))]
        public partial bool IsEnabled { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Status), nameof(IsDropZoneVisible))]
        public partial bool IsConnecting { get; set; }

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

        /// <summary>Signs in silently from a saved session, at startup.</summary>
        public async Task RestoreAsync()
        {
            if (Provider is null)
            {
                return;
            }

            try
            {
                await Provider.RestoreAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Offline at startup or similar: the user can still sign in by switching the provider on.
                Debug.WriteLine($"{Id}: restore failed: {ex.GetType().Name}");
            }
        }

        public async Task UploadFilesAsync(IReadOnlyList<StorageFile> files)
        {
            if (IsUploading || files.Count == 0)
            {
                return;
            }

            if (Provider is null)
            {
                await SimulateUploadAsync(files);
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
                    _uploadedLink = await Provider.UploadAsync(file, DefaultTargetFolder, progress, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // A failed upload only affects this provider's drop zone.
                    _failedFiles = files.Skip(i).ToList();
                    ErrorMessage = DescribeError(ex);
                    State = UploadState.Failed;
                    return;
                }
            }

            _failedFiles = [];
            State = UploadState.Done;
        }

        [RelayCommand]
        private async Task RetryAsync()
        {
            if (Provider is { IsConnected: false })
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

        partial void OnIsEnabledChanged(bool value)
        {
            if (!value)
            {
                // Switching off while the browser sign-in is open cancels it.
                _connectCts?.Cancel();
                return;
            }

            ConnectError = null;
            if (Provider is { IsConnected: false } && !IsConnecting)
            {
                _ = ConnectAsync();
            }
        }

        private async Task ConnectAsync()
        {
            if (Provider is null)
            {
                return;
            }

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
            }

            if (!Provider.IsConnected)
            {
                IsEnabled = false;
            }
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

        // TEMP (until step 6): fakes the upload for providers that are not implemented yet.
        private async Task SimulateUploadAsync(IReadOnlyList<StorageFile> files)
        {
            foreach (StorageFile file in files)
            {
                FileName = file.Name;
                Progress = 0;
                State = UploadState.Uploading;

                while (Progress < 100)
                {
                    await Task.Delay(90);
                    Progress = Math.Min(100, Progress + 4 + Random.Shared.Next(7));
                }
            }

            _uploadedLink = null;
            State = UploadState.Done;
        }
    }
}
