using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CG.Web.MegaApiClient;
using Windows.Networking.Connectivity;

namespace BoxeZone.Services.Storage
{
    /// <summary>
    /// MEGA through MegaApiClient (a community library: MEGA's own SDK is C++ only). MEGA has
    /// no OAuth: the e-mail and password are typed in the app's sign-in window and used once;
    /// only the session (its ID and the account's master key) is kept, in the TokenStore.
    /// Everything in the account is encrypted, so the server cannot look a folder up by path:
    /// the whole tree is listed (GetNodes) and kept here.
    /// </summary>
    public sealed class MegaProvider(TokenStore tokenStore, ICredentialPrompt prompt) : IStorageProvider
    {
        private const string SessionKey = "mega-session";
        private const string WebFolderUrl = "https://mega.nz/fm/";

        // The library has no timeout of its own: a dropped connection would hang a request forever.
        private static readonly int RequestTimeoutMs = (int)TimeSpan.FromMinutes(2).TotalMilliseconds;

        // A listing older than this is fetched again for "Procurar…" (folders made on the web).
        private static readonly TimeSpan BrowseRefresh = TimeSpan.FromSeconds(30);

        private readonly Lock _nodesLock = new();
        private MegaApiClient? _client;

        // Replaced, never changed in place: a reader keeps a consistent snapshot.
        private IReadOnlyList<INode>? _nodes;
        private DateTime _nodesFetched;

        public string Id => "mega";

        public string DisplayName => "MEGA";

        public bool IsConnected => _client is not null;

        public string? AccountEmail { get; private set; }

        public bool UsesBrowserSignIn => false;

        public async Task RestoreAsync(CancellationToken ct)
        {
            await TrySignInSilentlyAsync();
        }

        public async Task ConnectAsync(CancellationToken ct)
        {
            try
            {
                if (await TrySignInSilentlyAsync())
                {
                    return;
                }

                await prompt.RunAsync(DisplayName, new Uri("ms-appx:///Assets/Providers/mega.svg"), SignInAsync, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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
                    // Ends the session on MEGA's side too.
                    await _client.LogoutAsync();
                }
                catch (Exception)
                {
                    // Offline or already ended: forgetting the local session is what matters.
                }
            }

            ForgetSession();
        }

        public async Task<UploadedItem> UploadAsync(UploadJob job, IProgress<double> progress, Action<string> saveResumeState, CancellationToken ct)
        {
            MegaApiClient client = Client();

            try
            {
                // The library sends a file in one go: an interrupted upload starts over (no ResumeState).
                for (int attempt = 0; ; attempt++)
                {
                    INode folder = (await FindFolderAsync(client, job.TargetFolder, create: true, ct))!;
                    string name = UniqueName(await NodesAsync(client, fresh: false, ct), folder, job.FileName);
                    try
                    {
                        using var stream = new FileStream(job.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        INode file = await client.UploadAsync(
                            stream,
                            name,
                            folder,
                            new ClampedProgress(progress),
                            job.LastWriteTime.UtcDateTime,
                            ct);
                        AddNode(file);
                        progress.Report(100);
                        return new UploadedItem(file.Id, file.Name, WebFolderUrl + folder.Id);
                    }
                    catch (ApiException ex) when (ex.ApiResultCode == ApiResultCode.ResourceNotExists && attempt == 0)
                    {
                        // The folder was deleted on the web since the tree was read: read it again.
                        ClearNodes();
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw ToStorageException(ex);
            }
        }

        public async Task<UploadedItem> CreateFolderAsync(string parentFolder, string name, CancellationToken ct)
        {
            MegaApiClient client = Client();

            try
            {
                INode parent = (await FindFolderAsync(client, parentFolder, create: true, ct))!;
                string unique = UniqueName(await NodesAsync(client, fresh: false, ct), parent, name);
                INode folder = await client.CreateFolderAsync(unique, parent);
                AddNode(folder);
                return new UploadedItem(folder.Id, folder.Name, WebFolderUrl + folder.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw ToStorageException(ex);
            }
        }

        public async Task<IReadOnlyList<string>> ListFoldersAsync(string folder, CancellationToken ct)
        {
            MegaApiClient client = Client();

            try
            {
                bool stale;
                lock (_nodesLock)
                {
                    stale = DateTime.UtcNow - _nodesFetched > BrowseRefresh;
                }

                IReadOnlyList<INode> nodes = await NodesAsync(client, stale, ct);
                INode? parent = await FindFolderAsync(client, folder, create: false, ct);
                if (parent is null)
                {
                    return [];
                }

                return nodes.Where(n => n.Type == NodeType.Directory && n.ParentId == parent.Id)
                    .Select(n => n.Name)
                    .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw ToStorageException(ex);
            }
        }

        public async Task<string> CreateShareLinkAsync(UploadedItem item, CancellationToken ct)
        {
            MegaApiClient client = Client();

            try
            {
                INode? node = (await NodesAsync(client, fresh: false, ct)).FirstOrDefault(n => n.Id == item.Id)
                    ?? (await NodesAsync(client, fresh: true, ct)).FirstOrDefault(n => n.Id == item.Id)
                    ?? throw new InvalidDataException("The item is no longer in the MEGA account.");

                // Exports the node: anyone with the link (it carries the decryption key) can open it.
                ct.ThrowIfCancellationRequested();
                return (await client.GetDownloadLinkAsync(node)).ToString();
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw ToStorageException(ex);
            }
        }

        /// <summary>One "Entrar" of the sign-in window.</summary>
        private async Task<SignInOutcome> SignInAsync(SignInCredentials credentials, CancellationToken ct)
        {
            MegaApiClient client = CreateClient();
            try
            {
                MegaApiClient.LogonSessionToken session = await client
                    .LoginAsync(credentials.Email, credentials.Password, string.IsNullOrEmpty(credentials.Code) ? null : credentials.Code)
                    .WaitAsync(ct);

                await SaveSessionAsync(new SavedSession(credentials.Email, session.SessionId, Convert.ToBase64String(session.MasterKey)));
                UseClient(client, credentials.Email);
                return SignInOutcome.SignedIn;
            }
            catch (Exception ex) when (Unwrap(ex) is ApiException api)
            {
                return api.ApiResultCode switch
                {
                    // Asked for the code; with one typed, a wrong or expired code.
                    ApiResultCode.TwoFactorAuthenticationError when credentials.Code is null => SignInOutcome.CodeRequired,
                    ApiResultCode.TwoFactorAuthenticationError or ApiResultCode.RequestFailedPermanetly or ApiResultCode.ResourceExpired
                        when credentials.Code is not null => SignInOutcome.WrongCode,
                    ApiResultCode.ResourceNotExists or ApiResultCode.BadArguments => SignInOutcome.WrongCredentials,
                    _ => SignInOutcome.Failed,
                };
            }
            catch (Exception ex) when (Unwrap(ex) is NotSupportedException)
            {
                // Prelogin of an address with no account.
                return SignInOutcome.WrongCredentials;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsNetworkFailure(Unwrap(ex)))
            {
                return SignInOutcome.Offline;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                return SignInOutcome.Failed;
            }
        }

        /// <summary>
        /// Uses the saved session. False when there is none or MEGA says it ended (signed out
        /// elsewhere, password changed); throws when it could not be checked (no network).
        /// </summary>
        private async Task<bool> TrySignInSilentlyAsync()
        {
            if (await ReadSessionAsync() is not { } saved)
            {
                return false;
            }

            MegaApiClient client = CreateClient();
            await client.LoginAsync(new MegaApiClient.LogonSessionToken(saved.SessionId, Convert.FromBase64String(saved.MasterKey)));
            try
            {
                // Logging in from a saved session asks nothing of MEGA: this checks it.
                await client.GetAccountInformationAsync();
            }
            catch (Exception ex) when (Unwrap(ex) is ApiException { ApiResultCode: ApiResultCode.BadSessionId })
            {
                ForgetSession();
                return false;
            }

            UseClient(client, saved.Email);
            return true;
        }

        private void UseClient(MegaApiClient client, string email)
        {
            ClearNodes();
            _client = client;
            AccountEmail = email;
        }

        private static MegaApiClient CreateClient() =>
            new(new Options(computeApiRequestRetryWaitDelay: RetryDelay), new MegaWebClient(RequestTimeoutMs));

        /// <summary>
        /// The library's own retries (busy server, failed upload): 10 tries over about 10 s,
        /// not 17 over minutes, so a lost connection soon shows as "Aguardando conexão…".
        /// </summary>
        private static bool RetryDelay(int attempt, out TimeSpan delay)
        {
            delay = TimeSpan.FromMilliseconds(100 * Math.Pow(1.5, attempt - 1));
            return attempt <= 10;
        }

        private MegaApiClient Client() =>
            _client ?? throw new StorageProviderException(StorageErrorKind.SessionExpired, "Not connected to MEGA.");

        /// <summary>The account's tree, from memory unless <paramref name="fresh"/> or not read yet.</summary>
        private async Task<IReadOnlyList<INode>> NodesAsync(MegaApiClient client, bool fresh, CancellationToken ct)
        {
            lock (_nodesLock)
            {
                if (!fresh && _nodes is not null)
                {
                    return _nodes;
                }
            }

            ct.ThrowIfCancellationRequested();
            List<INode> nodes = (await client.GetNodesAsync()).ToList();
            lock (_nodesLock)
            {
                _nodes = nodes;
                _nodesFetched = DateTime.UtcNow;
            }

            return nodes;
        }

        private void AddNode(INode node)
        {
            lock (_nodesLock)
            {
                if (_nodes is not null)
                {
                    _nodes = [.. _nodes, node];
                }
            }
        }

        private void ClearNodes()
        {
            lock (_nodesLock)
            {
                _nodes = null;
            }
        }

        /// <summary>
        /// "/BoxeZone/a" from the Cloud Drive root, folder by folder. Missing folders are created
        /// when <paramref name="create"/>, otherwise the result is null.
        /// </summary>
        private async Task<INode?> FindFolderAsync(MegaApiClient client, string path, bool create, CancellationToken ct)
        {
            IReadOnlyList<INode> nodes = await NodesAsync(client, fresh: false, ct);
            INode current = nodes.First(n => n.Type == NodeType.Root);
            foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                INode? child = nodes.FirstOrDefault(n => n.Type == NodeType.Directory && n.ParentId == current.Id && n.Name == segment);
                if (child is null)
                {
                    if (!create)
                    {
                        return null;
                    }

                    ct.ThrowIfCancellationRequested();
                    child = await client.CreateFolderAsync(segment, current);
                    AddNode(child);
                    nodes = await NodesAsync(client, fresh: false, ct);
                }

                current = child;
            }

            return current;
        }

        /// <summary>MEGA accepts two items with the same name; the app gives "Nome (1)" like the other providers.</summary>
        private static string UniqueName(IReadOnlyList<INode> nodes, INode folder, string name)
        {
            var taken = nodes.Where(n => n.ParentId == folder.Id && n.Type is NodeType.File or NodeType.Directory)
                .Select(n => n.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!taken.Contains(name))
            {
                return name;
            }

            string stem = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);
            for (int i = 1; ; i++)
            {
                string candidate = $"{stem} ({i}){extension}";
                if (!taken.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        private async Task<SavedSession?> ReadSessionAsync()
        {
            string? json = await tokenStore.ReadAsync(SessionKey);
            return json is null ? null : JsonSerializer.Deserialize<SavedSession>(json);
        }

        private Task SaveSessionAsync(SavedSession session) =>
            tokenStore.WriteAsync(SessionKey, JsonSerializer.Serialize(session));

        /// <summary>The library calls the network synchronously inside Task.Run: errors come wrapped.</summary>
        private static Exception Unwrap(Exception ex)
        {
            while (ex is AggregateException { InnerException: { } inner })
            {
                ex = inner;
            }

            return ex;
        }

        private static bool IsNetworkFailure(Exception ex) =>
            ex is HttpRequestException or TaskCanceledException or IOException
            || NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.InternetAccess;

        private StorageProviderException ToStorageException(Exception ex)
        {
            Exception inner = Unwrap(ex);
            return inner switch
            {
                StorageProviderException storage => storage,
                ApiException { ApiResultCode: ApiResultCode.BadSessionId } =>
                    Forget(new StorageProviderException(StorageErrorKind.SessionExpired, inner.Message, inner)),
                ApiException { ApiResultCode: ApiResultCode.QuotaExceeded } =>
                    new StorageProviderException(StorageErrorKind.QuotaExceeded, inner.Message, inner),

                // The library gives up on an upload after its retries without saying why.
                UploadException or ApiException { ApiResultCode: ApiResultCode.RequestFailedRetry } when IsNetworkFailure(inner) =>
                    new StorageProviderException(StorageErrorKind.Offline, inner.Message, inner),
                HttpRequestException or TaskCanceledException =>
                    new StorageProviderException(StorageErrorKind.Offline, inner.Message, inner),
                _ => new StorageProviderException(StorageErrorKind.Unknown, inner.Message, inner),
            };
        }

        private StorageProviderException Forget(StorageProviderException ex)
        {
            ForgetSession();
            return ex;
        }

        private void ForgetSession()
        {
            _client = null;
            AccountEmail = null;
            ClearNodes();
            tokenStore.Delete(SessionKey);
        }

        /// <summary>A retried upload counts its bytes again: never past 100.</summary>
        private sealed class ClampedProgress(IProgress<double> inner) : IProgress<double>
        {
            public void Report(double value) => inner.Report(Math.Min(100, value));
        }

        /// <summary>What the TokenStore keeps: the e-mail (shown in Settings) and the session, never the password.</summary>
        private sealed record SavedSession(string Email, string SessionId, string MasterKey);
    }
}
