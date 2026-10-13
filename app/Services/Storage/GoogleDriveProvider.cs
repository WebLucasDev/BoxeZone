using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Microsoft.Win32;
using DriveFile = Google.Apis.Drive.v3.Data.File;
using Permission = Google.Apis.Drive.v3.Data.Permission;

namespace BoxeZone.Services.Storage
{
    /// <summary>
    /// Google Drive through the "Desktop app" OAuth flow (system browser + loopback) and the
    /// drive.file scope: the app only sees the files and folders it created itself.
    /// </summary>
    public sealed class GoogleDriveProvider(GoogleCredentials? credentials, TokenStore tokenStore) : IStorageProvider
    {
        private const string UserKey = "user";
        private const string FolderMimeType = "application/vnd.google-apps.folder";
        private const string FallbackContentType = "application/octet-stream";

        // Resumable upload in 8 MB chunks (must be a multiple of 256 KB).
        private const int ChunkSize = ResumableUpload.MinimumChunkSize * 32;

        private static readonly string[] Scopes = [DriveService.Scope.DriveFile];

        private readonly GoogleTokenDataStore _dataStore = new(tokenStore);

        // Folder path ("/BoxeZone") -> Drive folder ID, to avoid looking it up on every upload.
        private readonly Dictionary<string, string> _folderIds = [];

        private UserCredential? _credential;
        private DriveService? _drive;

        public string Id => "gdrive";

        public string DisplayName => "Google Drive";

        public bool IsConnected => _drive is not null;

        public string? AccountEmail { get; private set; }

        public async Task RestoreAsync(CancellationToken ct)
        {
            if (credentials is null || await _dataStore.GetAsync<TokenResponse>(UserKey) is null)
            {
                return;
            }

            try
            {
                // With a saved refresh token this does not open the browser.
                await SignInAsync(ct);
            }
            catch (StorageProviderException ex) when (ex.Kind == StorageErrorKind.SessionExpired)
            {
                // Revoked or expired: stay disconnected until the user signs in again.
            }
        }

        public Task ConnectAsync(CancellationToken ct)
        {
            if (credentials is null)
            {
                throw new StorageProviderException(StorageErrorKind.NotConfigured, "Google client ID/secret missing from appsettings.local.json.");
            }

            return SignInAsync(ct);
        }

        public async Task DisconnectAsync()
        {
            if (_credential is not null)
            {
                try
                {
                    await _credential.RevokeTokenAsync(CancellationToken.None);
                }
                catch (Exception)
                {
                    // Offline or already revoked: forgetting the local session is what matters.
                }
            }

            await ForgetSessionAsync();
        }

        public async Task<UploadedItem> UploadAsync(UploadJob job, IProgress<double> progress, Action<string> saveResumeState, CancellationToken ct)
        {
            DriveService drive = _drive
                ?? throw new StorageProviderException(StorageErrorKind.SessionExpired, "Not connected to Google Drive.");

            try
            {
                string folderId = await GetOrCreateFolderAsync(drive, job.TargetFolder, ct);

                using var stream = new FileStream(job.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                long totalBytes = stream.Length;

                FilesResource.CreateMediaUpload NewRequest()
                {
                    var metadata = new DriveFile { Name = job.FileName, Parents = [folderId] };
                    FilesResource.CreateMediaUpload request = drive.Files.Create(metadata, stream, ContentTypeFor(job.FileName));
                    request.Fields = "id, name, webViewLink";
                    request.ChunkSize = ChunkSize;
                    request.ProgressChanged += upload =>
                    {
                        if (totalBytes > 0)
                        {
                            progress.Report(Math.Min(100, upload.BytesSent * 100.0 / totalBytes));
                        }
                    };

                    // The resumable session URI: with it, a new request continues where this one stopped.
                    request.UploadSessionData += data => saveResumeState(data.UploadUri.AbsoluteUri);
                    return request;
                }

                FilesResource.CreateMediaUpload request = NewRequest();
                IUploadProgress result;
                if (job.ResumeState is { } sessionUri)
                {
                    // The library asks Google how many bytes arrived and seeks the stream there.
                    result = await request.ResumeAsync(new Uri(sessionUri), ct);
                    if (result.Status != UploadStatus.Completed && result.Exception is GoogleApiException)
                    {
                        // Google refused the session (expired after a week, or unknown): from zero.
                        stream.Position = 0;
                        request = NewRequest();
                        result = await request.UploadAsync(ct);
                    }
                }
                else
                {
                    result = await request.UploadAsync(ct);
                }

                if (result.Status != UploadStatus.Completed)
                {
                    throw result.Exception ?? new IOException("Upload did not complete.");
                }

                progress.Report(100);
                DriveFile created = request.ResponseBody;
                return new UploadedItem(created.Id, created.Name ?? job.FileName, created.WebViewLink);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The folder may have been deleted or the session lost; look it up again next time.
                _folderIds.Clear();
                throw await ToStorageExceptionAsync(ex);
            }
        }

        public async Task<UploadedItem> CreateFolderAsync(string parentFolder, string name, CancellationToken ct)
        {
            DriveService drive = _drive
                ?? throw new StorageProviderException(StorageErrorKind.SessionExpired, "Not connected to Google Drive.");

            try
            {
                string parentId = await GetOrCreateFolderAsync(drive, parentFolder, ct);
                string candidate = name;
                for (int n = 1; await FindFolderAsync(drive, parentId, candidate, ct) is not null; n++)
                {
                    candidate = $"{name} ({n})";
                }

                FilesResource.CreateRequest create = drive.Files.Create(new DriveFile
                {
                    Name = candidate,
                    MimeType = FolderMimeType,
                    Parents = [parentId],
                });
                create.Fields = "id, name, webViewLink";
                DriveFile folder = await create.ExecuteAsync(ct);
                return new UploadedItem(folder.Id, folder.Name, folder.WebViewLink);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _folderIds.Clear();
                throw await ToStorageExceptionAsync(ex);
            }
        }

        public async Task<string> CreateShareLinkAsync(UploadedItem item, CancellationToken ct)
        {
            DriveService drive = _drive
                ?? throw new StorageProviderException(StorageErrorKind.SessionExpired, "Not connected to Google Drive.");

            try
            {
                // drive.file lets the app share the files it created. Creating the same
                // "anyone" permission again does not add a second one.
                PermissionsResource.CreateRequest request = drive.Permissions.Create(
                    new Permission { Type = "anyone", Role = "reader" }, item.Id);
                request.Fields = "id";
                await request.ExecuteAsync(ct);
                return item.WebUrl;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw await ToStorageExceptionAsync(ex);
            }
        }

        private async Task SignInAsync(CancellationToken ct)
        {
            try
            {
                _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    new ClientSecrets { ClientId = credentials!.ClientId, ClientSecret = credentials.ClientSecret },
                    Scopes,
                    UserKey,
                    ct,
                    _dataStore,
                    new LocalServerCodeReceiver(SignInPage.Build()));

                var drive = new DriveService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = _credential,
                    ApplicationName = "BoxeZone",
                });

                AboutResource.GetRequest about = drive.About.Get();
                about.Fields = "user(emailAddress)";
                AccountEmail = (await about.ExecuteAsync(ct)).User?.EmailAddress;
                _drive = drive;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw await ToStorageExceptionAsync(ex);
            }
        }

        /// <summary>Finds each folder of the path under the Drive root, creating the missing ones.</summary>
        private async Task<string> GetOrCreateFolderAsync(DriveService drive, string path, CancellationToken ct)
        {
            string normalized = "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries));
            if (_folderIds.TryGetValue(normalized, out string? cachedId))
            {
                return cachedId;
            }

            string parentId = "root";
            foreach (string name in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (await FindFolderAsync(drive, parentId, name, ct) is { } existingId)
                {
                    parentId = existingId;
                    continue;
                }

                FilesResource.CreateRequest create = drive.Files.Create(new DriveFile
                {
                    Name = name,
                    MimeType = FolderMimeType,
                    Parents = [parentId],
                });
                create.Fields = "id";
                parentId = (await create.ExecuteAsync(ct)).Id;
            }

            _folderIds[normalized] = parentId;
            return parentId;
        }

        public async Task<IReadOnlyList<string>> ListFoldersAsync(string folder, CancellationToken ct)
        {
            DriveService drive = _drive
                ?? throw new StorageProviderException(StorageErrorKind.SessionExpired, "Not connected to Google Drive.");

            try
            {
                // Walk the path without creating anything: a missing folder simply has no subfolders.
                string parentId = "root";
                foreach (string name in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (await FindFolderAsync(drive, parentId, name, ct) is not { } id)
                    {
                        return [];
                    }

                    parentId = id;
                }

                var names = new List<string>();
                string? pageToken = null;
                do
                {
                    FilesResource.ListRequest list = drive.Files.List();
                    list.Q = $"mimeType = '{FolderMimeType}' and '{parentId}' in parents and trashed = false";
                    list.Fields = "nextPageToken, files(name)";
                    list.Spaces = "drive";
                    list.PageSize = 1000;
                    list.PageToken = pageToken;
                    var page = await list.ExecuteAsync(ct);
                    names.AddRange(page.Files.Select(f => f.Name));
                    pageToken = page.NextPageToken;
                }
                while (pageToken is not null);

                return names.Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw await ToStorageExceptionAsync(ex);
            }
        }

        /// <summary>The ID of a folder the app can see with this name under the parent, or null.</summary>
        private static async Task<string?> FindFolderAsync(DriveService drive, string parentId, string name, CancellationToken ct)
        {
            FilesResource.ListRequest list = drive.Files.List();
            list.Q = $"mimeType = '{FolderMimeType}' and name = '{EscapeQuery(name)}' and '{parentId}' in parents and trashed = false";
            list.Fields = "files(id)";
            list.Spaces = "drive";
            return (await list.ExecuteAsync(ct)).Files.FirstOrDefault()?.Id;
        }

        /// <summary>The MIME type Windows associates with the extension (Drive uses it for previews).</summary>
        private static string ContentTypeFor(string fileName)
        {
            string extension = Path.GetExtension(fileName);
            using RegistryKey? key = extension.Length > 0 ? Registry.ClassesRoot.OpenSubKey(extension) : null;
            return key?.GetValue("Content Type") as string ?? FallbackContentType;
        }

        private async Task<StorageProviderException> ToStorageExceptionAsync(Exception ex)
        {
            switch (ex)
            {
                case StorageProviderException storage:
                    return storage;

                case TokenResponseException { Error.Error: "access_denied" }:
                    return new StorageProviderException(StorageErrorKind.AccessDenied, ex.Message, ex);

                case TokenResponseException:
                case GoogleApiException { HttpStatusCode: HttpStatusCode.Unauthorized }:
                    await ForgetSessionAsync();
                    return new StorageProviderException(StorageErrorKind.SessionExpired, ex.Message, ex);

                case GoogleApiException api when api.Error?.Errors?.Any(e => e.Reason is "storageQuotaExceeded" or "quotaExceeded") == true:
                    return new StorageProviderException(StorageErrorKind.QuotaExceeded, ex.Message, ex);

                case HttpRequestException:
                case IOException { InnerException: HttpRequestException }:
                case TaskCanceledException: // HTTP timeout
                    return new StorageProviderException(StorageErrorKind.Offline, ex.Message, ex);

                default:
                    return new StorageProviderException(StorageErrorKind.Unknown, ex.Message, ex);
            }
        }

        private async Task ForgetSessionAsync()
        {
            await _dataStore.ClearAsync();
            _credential = null;
            _drive?.Dispose();
            _drive = null;
            AccountEmail = null;
            _folderIds.Clear();
        }

        // Drive query strings are single-quoted; backslash and quote must be escaped.
        private static string EscapeQuery(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
    }
}
