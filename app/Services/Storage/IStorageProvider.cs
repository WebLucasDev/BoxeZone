using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

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

        /// <summary>Uploads the file into <paramref name="targetFolder"/> (created if missing).</summary>
        Task<UploadedItem> UploadAsync(StorageFile file, string targetFolder, IProgress<double> progress, CancellationToken ct);

        /// <summary>Makes the file readable by anyone with the link and returns that link.</summary>
        Task<string> CreateShareLinkAsync(UploadedItem item, CancellationToken ct);
    }

    /// <summary>A file the app uploaded: what is needed to open it (owner link) and to share it later.</summary>
    public sealed record UploadedItem(string Id, string Name, string WebUrl);

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
