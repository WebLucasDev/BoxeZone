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
using Microsoft.UI.Dispatching;
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
    /// A batch of dropped files finished uploading, or stopped at the first failure.
    /// BatchId: for the notification's "Copiar link"; null on failure.
    /// </summary>
    public sealed record UploadFinishedEventArgs(
        bool Succeeded,
        IReadOnlyList<string> FileNames,
        string? Link,
        string? ErrorMessage,
        string? BatchId);

    /// <summary>
    /// State of one provider: switched on/off, connected, its destination folder and the
    /// upload shown in its drop zone. Switching on a provider that is not connected starts
    /// its sign-in.
    /// </summary>
    public partial class ProviderViewModel : ObservableObject
    {
        // The browser sign-in waits for the user, and closing the browser tab tells the app
        // nothing (no request reaches the loopback address): give up after a while.
        private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(2);

        private static readonly char[] InvalidFolderChars = ['<', '>', ':', '"', '|', '?', '*'];

        private static readonly ResourceLoader Strings = new();

        private readonly ProviderSettings _settings;
        private readonly Action _save;
        private CancellationTokenSource? _connectCts;
        private CancellationTokenSource? _uploadCts;

        // The batch being sent; files dropped while uploading join it. Each file weighs its
        // size (at least 1, so empty files still count) in the total progress.
        private readonly List<StorageFile> _batch = [];
        private readonly List<ulong> _weights = [];
        private ulong _totalWeight;
        private ulong _sentWeight;

        private readonly ShareLinkService _shareLinks;
        private readonly DispatcherQueueTimer _linkFeedbackTimer;

        private List<StorageFile> _failedFiles = [];

        // Files of the last batch that finished, for "Abrir" (last one) and "Copiar link" (all).
        private List<UploadedItem> _uploadedItems = [];
        private bool _isRestoring;

        public ProviderViewModel(string id, string name, string logoFile, IStorageProvider provider, ProviderSettings settings, Action save, ShareLinkService shareLinks)
        {
            Id = id;
            Name = name;
            Logo = new Uri($"ms-appx:///Assets/Providers/{logoFile}");
            Provider = provider;
            _settings = settings;
            _save = save;
            _shareLinks = shareLinks;
            FileName = string.Empty;
            ErrorMessage = string.Empty;
            FolderDraft = string.Empty;
            TargetFolder = settings.TargetFolder;
            CopyLinkLabel = Strings.GetString("CopyLink");

            // "Link copiado" and the failure messages go away after a moment.
            _linkFeedbackTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _linkFeedbackTimer.IsRepeating = false;
            _linkFeedbackTimer.Tick += (_, _) => ResetLinkFeedback();
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
        [NotifyPropertyChangedFor(nameof(Status), nameof(IsDropZoneVisible), nameof(AccountStatus), nameof(AccountAction))]
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

        /// <summary>Progress of the whole batch, 0 to 100.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProgressLabel))]
        public partial double Progress { get; set; }

        /// <summary>Position of the file being sent in the batch, from 1.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CountLabel))]
        public partial int FileIndex { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CountLabel), nameof(IsBatch), nameof(DoneLabel), nameof(DoneText))]
        public partial int FileCount { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DoneLabel), nameof(DoneText), nameof(FailedLabel))]
        public partial string FileName { get; set; }

        /// <summary>"Copiar link", "Copiando…" or "Link copiado".</summary>
        [ObservableProperty]
        public partial string CopyLinkLabel { get; set; }

        /// <summary>A failure of "Copiar link", shown for a moment instead of "Enviado · nome".</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DoneText), nameof(HasDoneMessage))]
        public partial string? DoneMessage { get; set; }

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

        /// <summary>Two or more files: the drop zone shows "3 de 10".</summary>
        public bool IsBatch => FileCount > 1;

        public string CountLabel => string.Format(Strings.GetString("UploadCountFormat"), FileIndex, FileCount);

        public string DoneLabel => string.Format(
            Strings.GetString("UploadDoneFormat"),
            IsBatch ? string.Format(Strings.GetString("UploadFilesFormat"), FileCount) : FileName);

        public string DoneText => DoneMessage ?? DoneLabel;

        public bool HasDoneMessage => DoneMessage is not null;

        public string FailedLabel =>string.Format(Strings.GetString("UploadFailedFormat"), FileName);

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

        /// <summary>Settings button: "Conectar", "Cancelar" while the browser sign-in is open, or "Desconectar".</summary>
        public string AccountAction => Strings.GetString(
            IsConnecting ? "AccountCancel" : IsConnected ? "AccountDisconnect" : "AccountConnect");

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

        /// <summary>
        /// Sends the files one at a time. While a batch is uploading, new files join it
        /// ("3 de 10" grows to "3 de 12") instead of starting another one.
        /// </summary>
        public async Task UploadFilesAsync(IReadOnlyList<StorageFile> files)
        {
            if (files.Count == 0)
            {
                return;
            }

            // Sizes first: from here on nothing awaits until the files are queued, so a batch
            // finishing in the meantime cannot leave them behind.
            ulong[] sizes = await GetSizesAsync(files);
            if (IsUploading)
            {
                AddToBatch(files, sizes);
                return;
            }

            _batch.Clear();
            _weights.Clear();
            _totalWeight = 0;
            _sentWeight = 0;
            AddToBatch(files, sizes);
            var uploaded = new List<UploadedItem>();

            using var cts = new CancellationTokenSource();
            _uploadCts = cts;

            // _batch can grow while a file is uploading: Count is read again on every pass.
            for (int i = 0; i < _batch.Count; i++)
            {
                StorageFile file = _batch[i];
                int index = i + 1;
                ulong weight = _weights[i];
                ulong sentBefore = _sentWeight;
                FileIndex = index;
                FileName = file.Name;
                Progress = 100.0 * sentBefore / _totalWeight;
                State = UploadState.Uploading;

                try
                {
                    // Progress<T> reports on the UI thread (it captures the DispatcherQueue context),
                    // but posted: a file's last report can arrive after the next file started.
                    // _totalWeight is read on each report, as files can still join the batch.
                    var progress = new Progress<double>(value =>
                    {
                        if (FileIndex == index && IsUploading)
                        {
                            Progress = 100.0 * (sentBefore + weight * value / 100) / _totalWeight;
                        }
                    });
                    uploaded.Add(await Provider.UploadAsync(file, TargetFolder, progress, cts.Token));
                    _sentWeight += weight;
                }
                catch (Exception) when (cts.IsCancellationRequested)
                {
                    // "Cancelar": the rest of the batch is dropped too. Providers leave no partial
                    // file behind (unfinished upload sessions never become files). No notification.
                    // Any exception after the request counts: some SDKs surface the abort as another error.
                    _uploadCts = null;
                    _failedFiles = [];
                    Progress = 0;
                    State = UploadState.Idle;
                    return;
                }
                catch (Exception ex)
                {
                    _uploadCts = null;
                    // A failed upload only affects this provider's drop zone.
                    _failedFiles = _batch.Skip(i).ToList();
                    ErrorMessage = DescribeError(ex);
                    State = UploadState.Failed;
                    RefreshConnection();
                    UploadFinished?.Invoke(this, new UploadFinishedEventArgs(false, [file.Name], null, ErrorMessage, null));
                    return;
                }
            }

            _uploadCts = null;
            _failedFiles = [];
            _uploadedItems = uploaded;
            string batchId = _shareLinks.Register(Provider, uploaded);
            State = UploadState.Done;
            UploadFinished?.Invoke(this, new UploadFinishedEventArgs(true, _batch.Select(f => f.Name).ToList(), uploaded[^1].WebUrl, null, batchId));
        }

        private void AddToBatch(IReadOnlyList<StorageFile> files, ulong[] sizes)
        {
            for (int i = 0; i < files.Count; i++)
            {
                ulong weight = Math.Max(sizes[i], 1);
                _batch.Add(files[i]);
                _weights.Add(weight);
                _totalWeight += weight;
            }

            FileCount = _batch.Count;
        }

        private static Task<ulong[]> GetSizesAsync(IReadOnlyList<StorageFile> files) =>
            Task.WhenAll(files.Select(async file =>
            {
                try
                {
                    return (await file.GetBasicPropertiesAsync()).Size;
                }
                catch (Exception)
                {
                    // Unreadable now: it weighs like an empty file, and its upload reports the real error.
                    return 0UL;
                }
            }));

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

        /// <summary>"Cancelar" while uploading: stops the current file and the rest of the batch.</summary>
        [RelayCommand]
        private void CancelUpload() => _uploadCts?.Cancel();

        [RelayCommand]
        private void OpenUploaded()
        {
            if (_uploadedItems.LastOrDefault()?.WebUrl is { Length: > 0 } link)
            {
                Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            }
        }

        /// <summary>
        /// Shares every file of the last batch and copies the links. A batch that starts
        /// meanwhile keeps its own row: the result of this copy is then not shown.
        /// </summary>
        [RelayCommand]
        private async Task CopyLinkAsync()
        {
            List<UploadedItem> items = _uploadedItems;
            if (items.Count == 0)
            {
                return;
            }

            ResetLinkFeedback();
            CopyLinkLabel = Strings.GetString("CopyLinkCopying");
            ShareResult result = await _shareLinks.CopyAsync(Provider, items, CancellationToken.None);

            if (!IsDone || items != _uploadedItems)
            {
                ResetLinkFeedback();
                return;
            }

            if (result.Succeeded)
            {
                CopyLinkLabel = Strings.GetString("CopyLinkCopied");
                ShowLinkFeedbackFor(TimeSpan.FromSeconds(2));
                return;
            }

            CopyLinkLabel = Strings.GetString("CopyLink");
            DoneMessage = ShareLinkService.DescribeFailure(result.Error!.Value, Name);
            ShowLinkFeedbackFor(TimeSpan.FromSeconds(result.Error == StorageErrorKind.MissingPermission ? 5 : 3));
            RefreshConnection();
        }

        partial void OnStateChanged(UploadState value) => ResetLinkFeedback();

        private void ShowLinkFeedbackFor(TimeSpan duration)
        {
            _linkFeedbackTimer.Interval = duration;
            _linkFeedbackTimer.Start();
        }

        private void ResetLinkFeedback()
        {
            _linkFeedbackTimer.Stop();
            CopyLinkLabel = Strings.GetString("CopyLink");
            DoneMessage = null;
        }

        /// <summary>
        /// Settings: "Conectar" signs in, "Cancelar" stops a sign-in in progress (like switching
        /// off), "Desconectar" signs out and switches the provider off. Concurrent executions are
        /// allowed so the button stays clickable while the sign-in it started is still waiting.
        /// </summary>
        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task ToggleAccountAsync()
        {
            if (IsConnecting)
            {
                _connectCts?.Cancel();
            }
            else if (IsConnected)
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
