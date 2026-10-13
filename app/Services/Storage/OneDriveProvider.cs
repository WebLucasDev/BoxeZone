using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;

namespace BoxeZone.Services.Storage
{
    /// <summary>
    /// OneDrive through MSAL (public client, system browser + loopback, personal and work
    /// accounts) and two Microsoft Graph calls over plain HTTP: create an upload session,
    /// then PUT the file in chunks.
    /// </summary>
    public sealed class OneDriveProvider : IStorageProvider
    {
        private const string GraphRoot = "https://graph.microsoft.com/v1.0";
        private const string CacheKey = "onedrive-msal-cache";

        // Upload session chunks must be a multiple of 320 KiB; 10 MiB = 32 x 320 KiB.
        private const int ChunkSize = 320 * 1024 * 32;

        private static readonly string[] Scopes = ["https://graph.microsoft.com/Files.ReadWrite"];

        // Chunks of 10 MiB on a slow connection can take longer than the default 100 s.
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

        private readonly IPublicClientApplication? _msal;
        private readonly TokenStore _tokenStore;
        private IAccount? _account;

        public OneDriveProvider(string? clientId, TokenStore tokenStore)
        {
            _tokenStore = tokenStore;
            if (clientId is null)
            {
                return;
            }

            _msal = PublicClientApplicationBuilder.Create(clientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, "common")
                .WithRedirectUri("http://localhost")
                .Build();

            // Keep MSAL's token cache in the encrypted TokenStore.
            _msal.UserTokenCache.SetBeforeAccessAsync(async args =>
            {
                string? data = await _tokenStore.ReadAsync(CacheKey);
                if (data is not null)
                {
                    args.TokenCache.DeserializeMsalV3(Convert.FromBase64String(data));
                }
            });
            _msal.UserTokenCache.SetAfterAccessAsync(async args =>
            {
                if (args.HasStateChanged)
                {
                    await _tokenStore.WriteAsync(CacheKey, Convert.ToBase64String(args.TokenCache.SerializeMsalV3()));
                }
            });
        }

        public string Id => "onedrive";

        public string DisplayName => "OneDrive";

        public bool IsConnected => _account is not null;

        public string? AccountEmail => _account?.Username;

        public async Task RestoreAsync(CancellationToken ct)
        {
            if (_msal is not null)
            {
                await TrySignInSilentlyAsync(ct);
            }
        }

        public async Task ConnectAsync(CancellationToken ct)
        {
            if (_msal is null)
            {
                throw new StorageProviderException(StorageErrorKind.NotConfigured, "Microsoft client ID missing from appsettings.local.json.");
            }

            try
            {
                if (await TrySignInSilentlyAsync(ct))
                {
                    return;
                }

                AuthenticationResult result = await _msal.AcquireTokenInteractive(Scopes)
                    .WithUseEmbeddedWebView(false)
                    .WithSystemWebViewOptions(new SystemWebViewOptions { HtmlMessageSuccess = SignInPage.Build() })
                    .WithPrompt(Prompt.SelectAccount)
                    .ExecuteAsync(ct);
                _account = result.Account;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw ToStorageException(ex);
            }
        }

        public async Task DisconnectAsync()
        {
            if (_msal is not null && _account is not null)
            {
                await _msal.RemoveAsync(_account);
            }

            _account = null;
            _tokenStore.Delete(CacheKey);
        }

        public async Task<UploadedItem> UploadAsync(UploadJob job, IProgress<double> progress, Action<string> saveResumeState, CancellationToken ct)
        {
            string? uploadUrl = null;
            try
            {
                string itemPath = ToItemPath(job.TargetFolder, job.FileName);
                using var stream = new FileStream(job.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                long totalBytes = stream.Length;

                if (totalBytes == 0)
                {
                    // Upload sessions need at least one byte; an empty file goes in a single PUT.
                    using HttpRequestMessage put = await CreateGraphRequestAsync(
                        HttpMethod.Put, $"/me/drive/root:/{itemPath}:/content?@microsoft.graph.conflictBehavior=rename", ct);
                    put.Content = new ByteArrayContent([]);
                    progress.Report(100);
                    return ReadItem(await SendAsync(put, ct));
                }

                long offset = 0;
                if (job.ResumeState is { } savedUrl && await NextExpectedOffsetAsync(savedUrl, ct) is { } next)
                {
                    uploadUrl = savedUrl;
                    offset = next;
                }
                else
                {
                    // Graph creates the missing folders of the path; "rename" keeps existing files.
                    using HttpRequestMessage create = await CreateGraphRequestAsync(
                        HttpMethod.Post, $"/me/drive/root:/{itemPath}:/createUploadSession", ct);
                    create.Content = new StringContent(
                        "{\"item\":{\"@microsoft.graph.conflictBehavior\":\"rename\"}}", Encoding.UTF8, "application/json");
                    using JsonDocument session = JsonDocument.Parse(await SendAsync(create, ct));
                    uploadUrl = session.RootElement.GetProperty("uploadUrl").GetString()!;
                    saveResumeState(uploadUrl);
                }

                stream.Seek(offset, SeekOrigin.Begin);
                byte[] buffer = new byte[ChunkSize];
                while (true)
                {
                    int length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);

                    // The upload URL is pre-authenticated: no Authorization header.
                    using var chunk = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
                    chunk.Content = new ByteArrayContent(buffer, 0, length);
                    chunk.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, totalBytes);
                    string body = await SendAsync(chunk, ct);

                    offset += length;
                    progress.Report(Math.Min(100, offset * 100.0 / totalBytes));
                    if (offset >= totalBytes)
                    {
                        return ReadItem(body);
                    }

                    saveResumeState(uploadUrl);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The session stays: the upload continues from it when the network is back or
                // after a restart. It expires on its own if nobody does.
                throw ToStorageException(ex);
            }
            catch (OperationCanceledException)
            {
                // "Cancelar": nothing will continue this upload.
                CancelSession(uploadUrl);
                throw;
            }
        }

        /// <summary>
        /// Where a saved upload session continues: the start of its first expected range, or
        /// null when Graph no longer knows the session (finished, expired or cancelled).
        /// </summary>
        private static async Task<long?> NextExpectedOffsetAsync(string uploadUrl, CancellationToken ct)
        {
            try
            {
                using JsonDocument status = JsonDocument.Parse(await SendAsync(new HttpRequestMessage(HttpMethod.Get, uploadUrl), ct));
                string? range = status.RootElement.GetProperty("nextExpectedRanges").EnumerateArray().FirstOrDefault().GetString();
                return range is not null && long.TryParse(range.Split('-')[0], out long start) ? start : null;
            }
            catch (GraphHttpException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.BadRequest)
            {
                return null;
            }
        }

        public async Task<UploadedItem> CreateFolderAsync(string parentFolder, string name, CancellationToken ct)
        {
            try
            {
                string[] parent = parentFolder.Split('/', StringSplitOptions.RemoveEmptyEntries);
                await EnsureFolderPathAsync(parent, ct);
                using HttpRequestMessage request = await CreateGraphRequestAsync(HttpMethod.Post, ChildrenUrl(parent), ct);
                request.Content = FolderBody(name, "rename");
                return ReadItem(await SendAsync(request, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw ToStorageException(ex);
            }
        }

        /// <summary>
        /// "/children" does not create a missing parent (the destination may not exist yet in a
        /// new account): create each segment, ignoring 409 (already there).
        /// </summary>
        private async Task EnsureFolderPathAsync(string[] segments, CancellationToken ct)
        {
            for (int i = 0; i < segments.Length; i++)
            {
                using HttpRequestMessage request = await CreateGraphRequestAsync(HttpMethod.Post, ChildrenUrl(segments[..i]), ct);
                request.Content = FolderBody(segments[i], "fail");
                try
                {
                    await SendAsync(request, ct);
                }
                catch (GraphHttpException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                {
                }
            }
        }

        public async Task<IReadOnlyList<string>> ListFoldersAsync(string folder, CancellationToken ct)
        {
            try
            {
                var names = new List<string>();
                string? url = ChildrenUrl(folder.Split('/', StringSplitOptions.RemoveEmptyEntries)) + "?$select=name,folder&$top=999";
                while (url is not null)
                {
                    using HttpRequestMessage request = await CreateGraphRequestAsync(HttpMethod.Get, url, ct);
                    using JsonDocument page = JsonDocument.Parse(await SendAsync(request, ct));
                    foreach (JsonElement item in page.RootElement.GetProperty("value").EnumerateArray())
                    {
                        if (item.TryGetProperty("folder", out _) && item.GetProperty("name").GetString() is { } name)
                        {
                            names.Add(name);
                        }
                    }

                    // The next page comes as an absolute URL: keep only what follows the Graph root.
                    url = page.RootElement.TryGetProperty("@odata.nextLink", out JsonElement next) && next.GetString() is { } link
                        ? link[GraphRoot.Length..]
                        : null;
                }

                return names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch (GraphHttpException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw ToStorageException(ex);
            }
        }

        private static string ChildrenUrl(string[] folderSegments) =>
            folderSegments.Length == 0
                ? "/me/drive/root/children"
                : $"/me/drive/root:/{string.Join('/', folderSegments.Select(Uri.EscapeDataString))}:/children";

        private static StringContent FolderBody(string name, string conflictBehavior) =>
            new(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = name,
                ["folder"] = new Dictionary<string, object>(),
                ["@microsoft.graph.conflictBehavior"] = conflictBehavior,
            }), Encoding.UTF8, "application/json");

        public async Task<string> CreateShareLinkAsync(UploadedItem item, CancellationToken ct)
        {
            try
            {
                try
                {
                    return await CreateLinkAsync(item.Id, "anonymous", ct);
                }
                catch (GraphHttpException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
                {
                    // Work and school accounts can forbid anonymous links: people in the organization instead.
                    return await CreateLinkAsync(item.Id, "organization", ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw ToStorageException(ex);
            }
        }

        /// <summary>
        /// Graph returns the existing link when the item already has one of this type and scope.
        /// The item ID goes unescaped: personal OneDrive IDs contain "!", valid in a path.
        /// </summary>
        private async Task<string> CreateLinkAsync(string itemId, string scope, CancellationToken ct)
        {
            using HttpRequestMessage request = await CreateGraphRequestAsync(HttpMethod.Post, $"/me/drive/items/{itemId}/createLink", ct);
            request.Content = new StringContent($"{{\"type\":\"view\",\"scope\":\"{scope}\"}}", Encoding.UTF8, "application/json");
            using JsonDocument response = JsonDocument.Parse(await SendAsync(request, ct));
            return response.RootElement.GetProperty("link").GetProperty("webUrl").GetString()
                ?? throw new InvalidDataException("createLink returned no webUrl.");
        }

        private async Task<bool> TrySignInSilentlyAsync(CancellationToken ct)
        {
            IAccount? account = (await _msal!.GetAccountsAsync()).FirstOrDefault();
            if (account is null)
            {
                return false;
            }

            try
            {
                await _msal.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct);
                _account = account;
                return true;
            }
            catch (MsalUiRequiredException)
            {
                // Expired or revoked: the user has to sign in again.
                await DisconnectAsync();
                return false;
            }
        }

        private async Task<HttpRequestMessage> CreateGraphRequestAsync(HttpMethod method, string relativeUrl, CancellationToken ct)
        {
            if (_msal is null || _account is null)
            {
                throw new StorageProviderException(StorageErrorKind.SessionExpired, "Not connected to OneDrive.");
            }

            AuthenticationResult token = await _msal.AcquireTokenSilent(Scopes, _account).ExecuteAsync(ct);
            var request = new HttpRequestMessage(method, GraphRoot + relativeUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            return request;
        }

        private static async Task<string> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using HttpResponseMessage response = await Http.SendAsync(request, ct);
            string body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new GraphHttpException(response.StatusCode, body);
            }

            return body;
        }

        private static UploadedItem ReadItem(string driveItemJson)
        {
            using JsonDocument item = JsonDocument.Parse(driveItemJson);
            JsonElement root = item.RootElement;
            return new UploadedItem(
                root.GetProperty("id").GetString() ?? string.Empty,
                root.GetProperty("name").GetString() ?? string.Empty,
                root.GetProperty("webUrl").GetString() ?? string.Empty);
        }

        /// <summary>"/BoxeZone" + "a b.pdf" -> "BoxeZone/a%20b.pdf", for /root:/{path}: addressing.</summary>
        private static string ToItemPath(string folder, string fileName) =>
            string.Join('/', folder.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Append(fileName)
                .Select(Uri.EscapeDataString));

        /// <summary>Best effort: a session left behind expires on its own after a few days.</summary>
        private static void CancelSession(string? uploadUrl)
        {
            if (uploadUrl is not null)
            {
                _ = Http.DeleteAsync(uploadUrl).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }

        private StorageProviderException ToStorageException(Exception ex)
        {
            switch (ex)
            {
                case StorageProviderException storage:
                    return storage;

                case MsalServiceException { ErrorCode: "access_denied" }:
                    return new StorageProviderException(StorageErrorKind.AccessDenied, ex.Message, ex);

                case MsalUiRequiredException:
                case GraphHttpException { StatusCode: HttpStatusCode.Unauthorized }:
                    _account = null;
                    return new StorageProviderException(StorageErrorKind.SessionExpired, ex.Message, ex);

                case GraphHttpException { StatusCode: HttpStatusCode.InsufficientStorage }:
                    return new StorageProviderException(StorageErrorKind.QuotaExceeded, ex.Message, ex);

                case HttpRequestException:
                case TaskCanceledException: // HTTP timeout
                case MsalServiceException { ErrorCode: "request_timeout" or "service_not_available" }:
                    return new StorageProviderException(StorageErrorKind.Offline, ex.Message, ex);

                default:
                    return new StorageProviderException(StorageErrorKind.Unknown, ex.Message, ex);
            }
        }

        private sealed class GraphHttpException(HttpStatusCode statusCode, string body)
            : Exception($"Graph returned {(int)statusCode}: {body}")
        {
            public HttpStatusCode StatusCode { get; } = statusCode;
        }
    }
}
