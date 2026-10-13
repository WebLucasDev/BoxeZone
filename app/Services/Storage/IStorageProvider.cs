using System;
using System.Threading;
using System.Threading.Tasks;

namespace BoxeZone.Services.Storage
{
    /// <summary>
    /// A cloud storage destination. The UI only talks to providers through this interface,
    /// never to a provider SDK. Failures surface as <see cref="StorageProviderException"/>.
    /// </summary>
    public interface IStorageProvider
    {
        /// <summary>"gdrive" | "onedrive" | "dropbox"</summary>
        string Id { get; }

        string DisplayName { get; }

        bool IsConnected { get; }

        /// <summary>Shown in Settings ("Conectado · e-mail").</summary>
        string? AccountEmail { get; }

        /// <summary>Reconnects silently from a saved session, if there is one. Never opens the browser.</summary>
        Task RestoreAsync(CancellationToken ct);

        /// <summary>OAuth sign-in in the system browser.</summary>
        Task ConnectAsync(CancellationToken ct);

        Task DisconnectAsync();

        /// <summary>
        /// Uploads job.LocalPath into job.TargetFolder (created if missing). Calls saveResumeState
        /// whenever the upload can be continued from a new point; job.ResumeState continues one.
        /// </summary>
        Task<UploadedItem> UploadAsync(UploadJob job, IProgress<double> progress, Action<string> saveResumeState, CancellationToken ct);

        /// <summary>
        /// Creates a folder inside parentFolder, renamed ("Nome (1)") when the name is taken.
        /// The item carries the real name; Id and WebUrl point to the folder.
        /// </summary>
        Task<UploadedItem> CreateFolderAsync(string parentFolder, string name, CancellationToken ct);

        /// <summary>Makes the file readable by anyone with the link and returns that link.</summary>
        Task<string> CreateShareLinkAsync(UploadedItem item, CancellationToken ct);
    }

    /// <summary>A file or folder the app created: what is needed to open it (owner link) and to share it later.</summary>
    public sealed record UploadedItem(string Id, string Name, string WebUrl);

    /// <summary>A file to upload. ResumeState: the provider's own data to continue an interrupted upload (null: from zero).</summary>
    public sealed record UploadJob(string LocalPath, string FileName, string TargetFolder, long Size, DateTimeOffset LastWriteTime, string? ResumeState);

    public enum StorageErrorKind
    {
        /// <summary>No client ID/secret in appsettings.local.json.</summary>
        NotConfigured,

        /// <summary>The user declined access in the browser.</summary>
        AccessDenied,

        /// <summary>The saved session is no longer valid; the user has to sign in again.</summary>
        SessionExpired,

        /// <summary>The saved session lacks a permission added in a later version; signing in again grants it.</summary>
        MissingPermission,

        Offline,
        QuotaExceeded,
        Unknown,
    }

    public sealed class StorageProviderException(StorageErrorKind kind, string message, Exception? innerException = null)
        : Exception(message, innerException)
    {
        public StorageErrorKind Kind { get; } = kind;
    }
}
