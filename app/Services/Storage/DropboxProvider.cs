using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dropbox.Api;
using Dropbox.Api.Files;
using Dropbox.Api.Sharing;

namespace BoxeZone.Services.Storage
{
    /// <summary>
    /// Dropbox through OAuth with PKCE (app key only, no secret): the system browser signs in
    /// and redirects to a local listener. Uploads always use an upload session, so even small
    /// files report progress (the single-call upload reports none).
    /// </summary>
    public sealed class DropboxProvider(string? appKey, TokenStore tokenStore) : IStorageProvider
    {
        // Must match a Redirect URI registered in the Dropbox app console exactly.
        private const string RedirectUri = "http://localhost:53682/";
        private const string RefreshTokenKey = "dropbox-refresh-token";
        private const int ChunkSize = 8 * 1024 * 1024;

        // sharing.write only serves "Copiar link"; it must also be enabled in the App Console.
        private static readonly string[] Scopes = ["files.content.write", "account_info.read", "sharing.write"];

        private DropboxClient? _client;

        public string Id => "dropbox";

        public string DisplayName => "Dropbox";

        public bool IsConnected => _client is not null;

        public string? AccountEmail { get; private set; }

        public async Task RestoreAsync(CancellationToken ct)
        {
            if (appKey is not null)
            {
                await TrySignInSilentlyAsync();
            }
        }

        public async Task ConnectAsync(CancellationToken ct)
        {
            if (appKey is null)
            {
                throw new StorageProviderException(StorageErrorKind.NotConfigured, "Dropbox app key missing from appsettings.local.json.");
            }

            try
            {
                if (await TrySignInSilentlyAsync())
                {
                    return;
                }

                var flow = new PKCEOAuthFlow();
                string state = Guid.NewGuid().ToString("N");
                Uri authorizeUri = flow.GetAuthorizeUri(
                    OAuthResponseType.Code,
                    appKey,
                    RedirectUri,
                    state: state,
                    tokenAccessType: TokenAccessType.Offline,
                    scopeList: Scopes,
                    includeGrantedScopes: IncludeGrantedScopes.None);

                string code = await WaitForAuthorizationCodeAsync(authorizeUri, state, ct);
                OAuth2Response response = await flow.ProcessCodeFlowAsync(code, appKey, RedirectUri, null);

                await tokenStore.WriteAsync(RefreshTokenKey, response.RefreshToken);
                await UseRefreshTokenAsync(response.RefreshToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw ToStorageException(ex);
            }
        }

        public async Task DisconnectAsync()
        {
            if (_client is not null)
            {
                try
                {
                    await _client.Auth.TokenRevokeAsync();
                }
                catch (Exception)
                {
                    // Offline or already revoked: forgetting the local session is what matters.
                }
            }

            ForgetSession();
        }

        public async Task<UploadedItem> UploadAsync(UploadJob job, IProgress<double> progress, Action<string> saveResumeState, CancellationToken ct)
        {
            DropboxClient client = _client
                ?? throw new StorageProviderException(StorageErrorKind.SessionExpired, "Not connected to Dropbox.");

            try
            {
                string folder = ToDropboxPath(job.TargetFolder);
                using var stream = new FileStream(job.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                long totalBytes = stream.Length;
                byte[] buffer = new byte[ChunkSize];

                // Start the session with the first chunk, append the rest, then commit with an empty body.
                int length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
                UploadSessionStartResult session;
                using (var first = new MemoryStream(buffer, 0, length))
                {
                    session = await client.Files.UploadSessionStartAsync(body: first);
                }

                ulong offset = (ulong)length;
                Report(progress, offset, totalBytes);

                while ((long)offset < totalBytes)
                {
                    ct.ThrowIfCancellationRequested();
                    length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
                    using var chunk = new MemoryStream(buffer, 0, length);
                    await client.Files.UploadSessionAppendV2Async(new UploadSessionCursor(session.SessionId, offset), body: chunk);
                    offset += (ulong)length;
                    Report(progress, offset, totalBytes);
                }

                // "Add" + autorename: an existing file with the same name gets a " (1)" copy instead of being replaced.
                FileMetadata metadata = await client.Files.UploadSessionFinishAsync(
                    new UploadSessionCursor(session.SessionId, offset),
                    new CommitInfo($"{folder.TrimEnd('/')}/{job.FileName}", WriteMode.Add.Instance, autorename: true),
                    body: new MemoryStream());

                progress.Report(100);
                return new UploadedItem(metadata.Id, metadata.Name, BuildWebLink(folder, metadata.Name));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw ToStorageException(ex);
            }
        }

        public async Task<UploadedItem> CreateFolderAsync(string parentFolder, string name, CancellationToken ct)
        {
            DropboxClient client = _client
                ?? throw new StorageProviderException(StorageErrorKind.SessionExpired, "Not connected to Dropbox.");

            try
            {
                ct.ThrowIfCancellationRequested();

                // Dropbox creates missing parents; autorename gives "Nome (1)" when the name is taken.
                CreateFolderResult result = await client.Files.CreateFolderV2Async($"{ToDropboxPath(parentFolder).TrimEnd('/')}/{name}", autorename: true);
                FolderMetadata folder = result.Metadata;
                return new UploadedItem(folder.Id, folder.Name, "https://www.dropbox.com/home" + EscapePath(folder.PathDisplay));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw ToStorageException(ex);
            }
        }

        public async Task<string> CreateShareLinkAsync(UploadedItem item, CancellationToken ct)
        {
            DropboxClient client = _client
                ?? throw new StorageProviderException(StorageErrorKind.SessionExpired, "Not connected to Dropbox.");

            try
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return (await client.Sharing.CreateSharedLinkWithSettingsAsync(item.Id)).Url;
                }
                catch (ApiException<CreateSharedLinkWithSettingsError> ex) when (ex.ErrorResponse.IsSharedLinkAlreadyExists)
                {
                    // Second click: the error carries the existing link. Listing links needs
                    // sharing.read, which the app does not ask for; it is only a last resort.
                    if (ex.ErrorResponse.AsSharedLinkAlreadyExists.Value?.AsMetadata?.Value?.Url is { } url)
                    {
                        return url;
                    }

                    ListSharedLinksResult existing = await client.Sharing.ListSharedLinksAsync(item.Id, directOnly: true);
                    return existing.Links.FirstOrDefault()?.Url
                        ?? throw new InvalidDataException("Dropbox reported an existing link but listed none.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw ToStorageException(ex);
            }
        }

        private async Task<bool> TrySignInSilentlyAsync()
        {
            string? refreshToken = await tokenStore.ReadAsync(RefreshTokenKey);
            if (refreshToken is null)
            {
                return false;
            }

            try
            {
                await UseRefreshTokenAsync(refreshToken);
                return true;
            }
            catch (AuthException)
            {
                // Revoked or expired: the user has to sign in again.
                ForgetSession();
                return false;
            }
        }

        private async Task UseRefreshTokenAsync(string refreshToken)
        {
            var client = new DropboxClient(refreshToken, appKey, new DropboxClientConfig("BoxeZone"));
            AccountEmail = (await client.Users.GetCurrentAccountAsync()).Email;
            _client = client;
        }

        /// <summary>Opens the authorize page and waits for Dropbox to redirect back to the local listener.</summary>
        private static async Task<string> WaitForAuthorizationCodeAsync(Uri authorizeUri, string state, CancellationToken ct)
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add(RedirectUri);
            listener.Start();
            using CancellationTokenRegistration stopOnCancel = ct.Register(listener.Stop);

            Process.Start(new ProcessStartInfo(authorizeUri.ToString()) { UseShellExecute = true });

            while (true)
            {
                HttpListenerContext context = await listener.GetContextAsync().WaitAsync(ct);
                string? code = context.Request.QueryString["code"];
                string? error = context.Request.QueryString["error"];

                // Ignore anything else the browser asks for (favicon and such).
                if (code is null && error is null)
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    context.Response.Close();
                    continue;
                }

                byte[] page = Encoding.UTF8.GetBytes(SignInPage.Build());
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.OutputStream.WriteAsync(page, ct);
                context.Response.Close();

                if (error is not null)
                {
                    throw new StorageProviderException(
                        error == "access_denied" ? StorageErrorKind.AccessDenied : StorageErrorKind.Unknown,
                        $"Dropbox authorization failed: {error}");
                }

                if (context.Request.QueryString["state"] != state)
                {
                    throw new StorageProviderException(StorageErrorKind.Unknown, "Dropbox authorization state mismatch.");
                }

                return code!;
            }
        }

        /// <summary>Owner link ("Abrir"): the folder on the web with the file in preview.</summary>
        private static string BuildWebLink(string folder, string fileName) =>
            $"https://www.dropbox.com/home{EscapePath(folder)}?preview={Uri.EscapeDataString(fileName)}";

        private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

        /// <summary>"BoxeZone/a/" -> "/BoxeZone/a"; the root is "/".</summary>
        private static string ToDropboxPath(string folder) =>
            "/" + string.Join('/', folder.Split('/', StringSplitOptions.RemoveEmptyEntries));

        private static void Report(IProgress<double> progress, ulong sent, long total)
        {
            if (total > 0)
            {
                progress.Report(Math.Min(100, sent * 100.0 / total));
            }
        }

        private StorageProviderException ToStorageException(Exception ex) =>
            ex switch
            {
                StorageProviderException storage => storage,

                // A session from before sharing.write: keep it (uploads still work), ask to sign in again.
                AuthException { ErrorResponse.IsMissingScope: true } =>
                    new StorageProviderException(StorageErrorKind.MissingPermission, ex.Message, ex),
                AuthException => Forget(new StorageProviderException(StorageErrorKind.SessionExpired, ex.Message, ex)),
                _ when ex.Message.Contains("insufficient_space", StringComparison.Ordinal) =>
                    new StorageProviderException(StorageErrorKind.QuotaExceeded, ex.Message, ex),
                HttpRequestException or TaskCanceledException =>
                    new StorageProviderException(StorageErrorKind.Offline, ex.Message, ex),
                _ => new StorageProviderException(StorageErrorKind.Unknown, ex.Message, ex),
            };

        private StorageProviderException Forget(StorageProviderException ex)
        {
            ForgetSession();
            return ex;
        }

        private void ForgetSession()
        {
            _client?.Dispose();
            _client = null;
            AccountEmail = null;
            tokenStore.Delete(RefreshTokenKey);
        }
    }
}
