using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BoxeZone.Helpers;
using BoxeZone.Services.Storage;
using Microsoft.Windows.ApplicationModel.Resources;

namespace BoxeZone.Services
{
    /// <summary>Error is null when the links were created and copied.</summary>
    public sealed record ShareResult(StorageErrorKind? Error)
    {
        public bool Succeeded => Error is null;
    }

    public sealed record SharedBatch(IStorageProvider Provider, IReadOnlyList<UploadedItem> Items);

    public sealed record BatchCopy(SharedBatch Batch, ShareResult Result);

    /// <summary>
    /// "Copiar link": shares the files of a batch (anyone with the link can view) and copies
    /// the links, one per line. Remembers the latest batches so the notification button can
    /// copy them later, while the app keeps running.
    /// </summary>
    public sealed class ShareLinkService(IntPtr clipboardOwner)
    {
        private const int MaxBatches = 20;
        private const int ClipboardAttempts = 10;

        private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(50);
        private static readonly ResourceLoader Strings = new();

        // Oldest first.
        private readonly List<(string Id, SharedBatch Batch)> _batches = [];

        public string Register(IStorageProvider provider, IReadOnlyList<UploadedItem> items)
        {
            string id = Guid.NewGuid().ToString("N");
            _batches.Add((id, new SharedBatch(provider, items)));
            if (_batches.Count > MaxBatches)
            {
                _batches.RemoveAt(0);
            }

            return id;
        }

        public async Task<ShareResult> CopyAsync(IStorageProvider provider, IReadOnlyList<UploadedItem> items, CancellationToken ct)
        {
            try
            {
                var links = new List<string>(items.Count);
                foreach (UploadedItem item in items)
                {
                    links.Add(await provider.CreateShareLinkAsync(item, ct));
                }

                string text = string.Join("\r\n", links);
                for (int attempt = 1; !Win32.TrySetClipboardText(clipboardOwner, text); attempt++)
                {
                    if (attempt == ClipboardAttempts)
                    {
                        // Another app kept the clipboard open the whole time.
                        return new ShareResult(StorageErrorKind.Unknown);
                    }

                    await Task.Delay(ClipboardRetryDelay, ct);
                }

                return new ShareResult(null);
            }
            catch (StorageProviderException ex)
            {
                return new ShareResult(ex.Kind);
            }
        }

        /// <summary>The notification's "Copiar link". Null when the batch is no longer remembered.</summary>
        public async Task<BatchCopy?> CopyBatchAsync(string batchId)
        {
            SharedBatch? batch = _batches.LastOrDefault(b => b.Id == batchId).Batch;
            if (batch is null)
            {
                return null;
            }

            return new BatchCopy(batch, await CopyAsync(batch.Provider, batch.Items, CancellationToken.None));
        }

        public static string DescribeFailure(StorageErrorKind kind, string providerName) =>
            kind == StorageErrorKind.MissingPermission
                ? string.Format(Strings.GetString("CopyLinkReconnectFormat"), providerName)
                : Strings.GetString("CopyLinkFailed");
    }
}
