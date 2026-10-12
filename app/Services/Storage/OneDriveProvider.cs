using System;
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
using Windows.Storage;

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

        public async Task<string> UploadAsync(StorageFile file, string targetFolder, IProgress<double> progress, CancellationToken ct)
        {
            string? uploadUrl = null;
            try
            {
                string itemPath = ToItemPath(targetFolder, file.Name);
                using Stream stream = await file.OpenStreamForReadAsync();
                long totalBytes = stream.Length;

                if (totalBytes == 0)
                {
                    // Upload sessions need at least one byte; an empty file goes in a single PUT.
                    using HttpRequestMessage put = await CreateGraphRequestAsync(
                        HttpMethod.Put, $"/me/drive/root:/{itemPath}:/content?@microsoft.graph.conflictBehavior=rename", ct);
                    put.Content = new ByteArrayContent([]);
                    progress.Report(100);
                    return ReadWebUrl(await SendAsync(put, ct));
                }

                // Graph creates the missing folders of the path; "rename" keeps existing files.
                using (HttpRequestMessage create = await CreateGraphRequestAsync(
                    HttpMethod.Post, $"/me/drive/root:/{itemPath}:/createUploadSession", ct))
                {
                    create.Content = new StringContent(
                        "{\"item\":{\"@microsoft.graph.conflictBehavior\":\"rename\"}}", Encoding.UTF8, "application/json");
                    using JsonDocument session = JsonDocument.Parse(await SendAsync(create, ct));
                    uploadUrl = session.RootElement.GetProperty("uploadUrl").GetString();
                }

                byte[] buffer = new byte[ChunkSize];
                long offset = 0;
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
                        return ReadWebUrl(body);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                CancelSession(uploadUrl);
                throw ToStorageException(ex);
            }
            catch (OperationCanceledException)
            {
                CancelSession(uploadUrl);
                throw;
            }
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

        private static string ReadWebUrl(string driveItemJson)
        {
            using JsonDocument item = JsonDocument.Parse(driveItemJson);
            return item.RootElement.GetProperty("webUrl").GetString() ?? string.Empty;
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
