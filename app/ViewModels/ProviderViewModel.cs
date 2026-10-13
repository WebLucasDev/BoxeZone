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
using BoxeZone.Services.Upload;
using System.IO;

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

        // The batch being sent (dropped folders and files); what is dropped while uploading
        // joins it. Kept after a failure so "Tentar de novo" continues it.
        private UploadQueueState? _queue;

        private readonly ShareLinkService _shareLinks;
        private readonly UploadQueueStore _queueStore;
        private readonly DispatcherQueue _dispatcher;
        private DateTime _lastResumeSave;
        private readonly DispatcherQueueTimer _linkFeedbackTimer;

        // The last batch that finished: for "Abrir" and "Copiar link" (dropped folders and
        // loose files, in drop order), and the dropped folder's name when it was just one folder.
        private List<UploadedItem> _uploadedItems = [];
        private UploadedItem? _openTarget;
        private string? _doneTitle;
        private bool _isRestoring;

        public ProviderViewModel(string id, string name, string logoFile, IStorageProvider provider, ProviderSettings settings, Action save, ShareLinkService shareLinks, UploadQueueStore queueStore)
        {
            Id = id;
            Name = name;
            Logo = new Uri($"ms-appx:///Assets/Providers/{logoFile}");
            Provider = provider;
            _settings = settings;
            _save = save;
            _shareLinks = shareLinks;
            _queueStore = queueStore;
            FileName = string.Empty;
            ErrorMessage = string.Empty;
            FolderDraft = string.Empty;
            TargetFolder = settings.TargetFolder;
            CopyLinkLabel = Strings.GetString("CopyLink");

            // "Link copiado" and the failure messages go away after a moment.
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            _linkFeedbackTimer = _dispatcher.CreateTimer();
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
        [NotifyPropertyChangedFor(nameof(CountLabel), nameof(IsBatch), nameof(DoneLabel))]
        public partial int FileCount { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DoneLabel), nameof(FailedLabel))]
        public partial string FileName { get; set; }

        /// <summary>"Copiar link", "Copiando…" or "Link copiado".</summary>
        [ObservableProperty]
        public partial string CopyLinkLabel { get; set; }

        /// <summary>A failure of "Copiar link", shown for a moment in place of the "Enviado" row.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasDoneMessage))]
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

        /// <summary>Continuing a batch saved before the app closed, until its first progress.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProgressLabel))]
        public partial bool IsResuming { get; set; }

        /// <summary>No network mid-upload: the upload waits and goes on when it is back.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProgressLabel))]
        public partial bool IsWaitingForNetwork { get; set; }

        public string ProgressLabel =>
            IsWaitingForNetwork ? Strings.GetString("UploadWaitingForNetwork")
            : IsResuming ? Strings.GetString("UploadResuming")
            : $"{Progress:0}%";

        /// <summary>Two or more files: the drop zone shows "3 de 10".</summary>
        public bool IsBatch => FileCount > 1;

        public string CountLabel => string.Format(Strings.GetString("UploadCountFormat"), FileIndex, FileCount);

        public string DoneLabel => string.Format(
            Strings.GetString("UploadDoneFormat"),
            _doneTitle ?? (IsBatch ? string.Format(Strings.GetString("UploadFilesFormat"), FileCount) : FileName));

        public bool HasDoneMessage => DoneMessage is not null;

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
                await ResumeSavedQueueAsync();
            }
        }

        /// <summary>
        /// Continues the batch saved when the app closed mid-upload ("Retomando…"). Only with
        /// the provider on and connected; otherwise the saved batch waits.
        /// </summary>
        public async Task ResumeSavedQueueAsync()
        {
            if (IsUploading || _queue is not null || !IsEnabled || !Provider.IsConnected)
            {
                return;
            }

            UploadQueueState? saved = await _queueStore.LoadAsync(Id);
            if (saved is null || IsUploading || _queue is not null)
            {
                return;
            }

            if (!saved.Files.Any(f => f.Status == QueuedFileStatus.Pending))
            {
                await _queueStore.DeleteAsync(Id);
                return;
            }

            _queue = saved;
            IsResuming = true;
            await RunQueueAsync();
        }

        /// <summary>
        /// Dropped files and folders. Folders are walked off the UI thread and recreated at the
        /// destination. While a batch is uploading, what is dropped joins it ("3 de 10" grows).
        /// </summary>
        public async Task EnqueueAsync(IReadOnlyList<string> paths)
        {
            ScanResult scan = await FolderScanner.ScanAsync(paths);
            if (scan.IsEmpty)
            {
                return;
            }

            // From here on nothing awaits until the items are queued, so a batch finishing in
            // the meantime cannot leave them behind.
            if (IsUploading && _queue is not null)
            {
                AddToQueue(_queue, scan);
                FileCount = CountActive(_queue);
                await SaveQueueAsync();
                return;
            }

            _queue = new UploadQueueState { TargetFolder = TargetFolder };
            AddToQueue(_queue, scan);
            await SaveQueueAsync();
            await RunQueueAsync();
        }

        private static void AddToQueue(UploadQueueState queue, ScanResult scan)
        {
            foreach (object item in scan.Items)
            {
                int order = queue.NextOrder++;
                if (item is ScannedFolder folder)
                {
                    queue.Folders.Add(new QueuedFolder { LocalPath = folder.LocalPath, Name = folder.Name, Order = order });
                    foreach (ScannedFile file in folder.Files)
                    {
                        queue.Files.Add(ToQueued(file, queue.Folders.Count - 1, order));
                    }
                }
                else if (item is ScannedFile file)
                {
                    queue.Files.Add(ToQueued(file, -1, order));
                }
            }
        }

        private static QueuedFile ToQueued(ScannedFile file, int folderIndex, int order) => new()
        {
            LocalPath = file.LocalPath,
            FileName = Path.GetFileName(file.LocalPath),
            FolderIndex = folderIndex,
            RelativeFolder = file.RelativeFolder,
            Order = order,
            Size = file.Size,
            LastWriteTime = file.LastWriteTime,
        };

        /// <summary>
        /// Sends the queue's pending files one at a time. The queue can grow while a file is
        /// uploading, so Count is read again on every pass.
        /// </summary>
        private async Task RunQueueAsync()
        {
            if (_queue is not { } queue || IsUploading)
            {
                return;
            }

            using var cts = new CancellationTokenSource();
            _uploadCts = cts;

            for (int i = 0; i < queue.Files.Count; i++)
            {
                QueuedFile file = queue.Files[i];
                if (file.Status != QueuedFileStatus.Pending)
                {
                    continue;
                }

                int index = queue.Files.Take(i + 1).Count(f => f.Status != QueuedFileStatus.Skipped);
                long weight = Weight(file);
                long sentBefore = queue.Files.Where(f => f.Status == QueuedFileStatus.Done).Sum(Weight);
                FileIndex = index;
                FileCount = CountActive(queue);
                FileName = file.FileName;
                Progress = 100.0 * sentBefore / TotalWeight(queue);
                State = UploadState.Uploading;

                try
                {
                    string target = await TargetFolderForAsync(queue, file, cts.Token);
                    if (!File.Exists(file.LocalPath))
                    {
                        // Moved or deleted since the drop: "Tentar de novo" goes on without it.
                        file.Status = QueuedFileStatus.Skipped;
                        Fail(file, Strings.GetString("ErrorFileMissing"));
                        return;
                    }

                    var info = new FileInfo(file.LocalPath);
                    if (info.Length != file.Size || new DateTimeOffset(info.LastWriteTimeUtc) != file.LastWriteTime)
                    {
                        // Changed since the drop: whatever was sent of it no longer matches.
                        file.Size = info.Length;
                        file.LastWriteTime = info.LastWriteTimeUtc;
                        file.ResumeState = null;
                        weight = Weight(file);
                    }

                    // Progress<T> reports on the UI thread (it captures the DispatcherQueue context),
                    // but posted: a file's last report can arrive after the next file started.
                    // The total is read on each report, as files can still join the batch.
                    var progress = new Progress<double>(value =>
                    {
                        if (FileIndex == index && IsUploading && ReferenceEquals(_queue, queue))
                        {
                            // Bytes are moving again: the network is back.
                            IsResuming = false;
                            IsWaitingForNetwork = false;
                            Progress = 100.0 * (sentBefore + weight * value / 100) / TotalWeight(queue);
                        }
                    });

                    // Each try takes the latest resume point: after a network drop, it goes on from there.
                    file.Uploaded = await WhileOfflineAsync(
                        () => Provider.UploadAsync(
                            new UploadJob(file.LocalPath, file.FileName, target, file.Size, file.LastWriteTime, file.ResumeState),
                            progress,
                            state => OnResumeState(queue, file, state),
                            cts.Token),
                        cts.Token);
                    file.Status = QueuedFileStatus.Done;
                    file.ResumeState = null;
                    IsResuming = false;
                    await SaveQueueAsync();
                }
                catch (Exception) when (cts.IsCancellationRequested)
                {
                    // "Cancelar": the rest of the batch is dropped too. Providers leave no partial
                    // file behind (unfinished upload sessions never become files). No notification.
                    // Any exception after the request counts: some SDKs surface the abort as another error.
                    _uploadCts = null;
                    _queue = null;
                    IsResuming = false;
                    await _queueStore.DeleteAsync(Id);
                    Progress = 0;
                    State = UploadState.Idle;
                    return;
                }
                catch (Exception ex)
                {
                    // A failed upload only affects this provider's drop zone; the queue stays
                    // for "Tentar de novo".
                    Fail(file, DescribeError(ex));
                    return;
                }
            }

            await FinishAsync(queue);
        }

        /// <summary>A loose file goes to the destination; a folder's file, inside the folder created for it.</summary>
        private async Task<string> TargetFolderForAsync(UploadQueueState queue, QueuedFile file, CancellationToken ct)
        {
            if (file.FolderIndex < 0)
            {
                return queue.TargetFolder;
            }

            QueuedFolder folder = queue.Folders[file.FolderIndex];
            if (folder.Created is null)
            {
                folder.Created = await WhileOfflineAsync(() => Provider.CreateFolderAsync(queue.TargetFolder, folder.Name, ct), ct);
                await SaveQueueAsync();
            }

            return JoinPath(queue.TargetFolder, folder.Created.Name, file.RelativeFolder);
        }

        /// <summary>From the provider, maybe off the UI thread: keep the latest point, save at most every 2 s.</summary>
        private void OnResumeState(UploadQueueState queue, QueuedFile file, string state) =>
            _dispatcher.TryEnqueue(async () =>
            {
                if (!ReferenceEquals(_queue, queue) || file.Status != QueuedFileStatus.Pending)
                {
                    return;
                }

                file.ResumeState = state;
                if (DateTime.UtcNow - _lastResumeSave >= TimeSpan.FromSeconds(2))
                {
                    _lastResumeSave = DateTime.UtcNow;
                    await SaveQueueAsync();
                }
            });

        private async Task SaveQueueAsync()
        {
            if (_queue is not { } queue)
            {
                return;
            }

            try
            {
                await _queueStore.SaveAsync(Id, queue);
            }
            catch (Exception ex)
            {
                // The upload goes on; only resuming after a restart is at stake.
                Debug.WriteLine($"{Id}: saving the upload queue failed: {ex.GetType().Name}");
            }
        }

        private void Fail(QueuedFile file, string message)
        {
            _uploadCts = null;
            IsResuming = false;

            // The queue stays saved: "Tentar de novo", or the next start, continues it.
            _ = SaveQueueAsync();
            FileName = file.FileName;
            ErrorMessage = message;
            State = UploadState.Failed;
            RefreshConnection();
            UploadFinished?.Invoke(this, new UploadFinishedEventArgs(false, [file.FileName], null, ErrorMessage, null));
        }

        private async Task FinishAsync(UploadQueueState queue)
        {
            _uploadCts = null;
            _queue = null;
            IsResuming = false;
            await _queueStore.DeleteAsync(Id);

            // What "Copiar link" shares: each dropped folder as one link, each loose file as its own.
            _uploadedItems = queue.Folders.Where(f => f.Created is not null).Select(f => (f.Order, Item: f.Created!))
                .Concat(queue.Files.Where(f => f.FolderIndex < 0 && f.Uploaded is not null).Select(f => (f.Order, Item: f.Uploaded!)))
                .OrderBy(x => x.Order)
                .Select(x => x.Item)
                .ToList();

            List<QueuedFile> sent = queue.Files.Where(f => f.Status == QueuedFileStatus.Done).ToList();
            if (_uploadedItems.Count == 0 || sent.Count == 0)
            {
                Progress = 0;
                State = UploadState.Idle;
                return;
            }

            bool singleFolder = queue.Folders.Count == 1 && queue.Files.All(f => f.FolderIndex == 0);
            _doneTitle = singleFolder ? queue.Folders[0].Created!.Name : null;
            _openTarget = singleFolder ? queue.Folders[0].Created : sent[^1].Uploaded;
            FileCount = sent.Count;
            FileName = sent[^1].FileName;
            OnPropertyChanged(nameof(DoneLabel));

            string batchId = _shareLinks.Register(Provider, _uploadedItems);
            State = UploadState.Done;
            IReadOnlyList<string> names = singleFolder ? [_doneTitle!] : sent.Select(f => f.FileName).ToList();
            UploadFinished?.Invoke(this, new UploadFinishedEventArgs(true, names, _openTarget?.WebUrl, null, batchId));
        }

        /// <summary>Retries while the provider reports no network ("Aguardando conexão…"), without limit.</summary>
        private async Task<T> WhileOfflineAsync<T>(Func<Task<T>> action, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    T result = await action();
                    IsWaitingForNetwork = false;
                    return result;
                }
                catch (StorageProviderException ex) when (ex.Kind == StorageErrorKind.Offline && !ct.IsCancellationRequested)
                {
                    IsWaitingForNetwork = true;
                    await SaveQueueAsync();
                    await NetworkWaiter.WaitAsync(attempt, ct);
                }
            }
        }

        private static long Weight(QueuedFile file) => Math.Max(file.Size, 1);

        private static double TotalWeight(UploadQueueState queue) =>
            queue.Files.Where(f => f.Status != QueuedFileStatus.Skipped).Sum(Weight);

        private static int CountActive(UploadQueueState queue) =>
            queue.Files.Count(f => f.Status != QueuedFileStatus.Skipped);

        /// <summary>"/BoxeZone" + "Fotos (1)" + "a/b" -> "/BoxeZone/Fotos (1)/a/b".</summary>
        private static string JoinPath(params string[] parts) =>
            "/" + string.Join('/', parts.SelectMany(p => p.Split('/', StringSplitOptions.RemoveEmptyEntries)));

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

            // The queue goes on from the file that failed (or past a file that disappeared).
            await RunQueueAsync();
        }

        /// <summary>"Cancelar" while uploading: stops the current file and the rest of the batch.</summary>
        [RelayCommand]
        private void CancelUpload() => _uploadCts?.Cancel();

        [RelayCommand]
        private void OpenUploaded()
        {
            // One dropped folder: the folder; otherwise the last file sent.
            if (_openTarget?.WebUrl is { Length: > 0 } link)
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

        partial void OnStateChanged(UploadState value)
        {
            ResetLinkFeedback();
            if (value != UploadState.Uploading)
            {
                IsWaitingForNetwork = false;
                IsResuming = false;
            }
        }

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
                // A batch in progress (or saved) belongs to this account: drop it.
                _uploadCts?.Cancel();
                _queue = null;
                await _queueStore.DeleteAsync(Id);
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
            else if (Provider.IsConnected && !_isRestoring)
            {
                // Switched back on with a batch saved while it was off.
                _ = ResumeSavedQueueAsync();
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
            else if (IsEnabled)
            {
                _ = ResumeSavedQueueAsync();
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
