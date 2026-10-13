using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BoxeZone.Services.Upload
{
    /// <summary>
    /// The batch in progress of each provider, in the TokenStore (encrypted): it holds file paths
    /// and upload session addresses (OneDrive's is pre-authenticated). Deleted when the batch
    /// ends, is cancelled or the account is disconnected.
    /// </summary>
    public sealed class UploadQueueStore(TokenStore tokenStore)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task<UploadQueueState?> LoadAsync(string providerId)
        {
            try
            {
                string? json = await tokenStore.ReadAsync(Key(providerId));
                return json is null ? null : JsonSerializer.Deserialize<UploadQueueState>(json);
            }
            catch (Exception)
            {
                // Damaged (the PC went off mid-write), from another version or another Windows
                // user: the batch is lost, nothing else.
                await DeleteAsync(providerId);
                return null;
            }
        }

        /// <summary>Call on the UI thread, where the queue changes: it is serialized right away.</summary>
        public async Task SaveAsync(string providerId, UploadQueueState state)
        {
            string json = JsonSerializer.Serialize(state);
            await _gate.WaitAsync();
            try
            {
                await tokenStore.WriteAsync(Key(providerId), json);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Through the same gate as saves, so a save still running cannot bring the queue back.</summary>
        public async Task DeleteAsync(string providerId)
        {
            await _gate.WaitAsync();
            try
            {
                tokenStore.Delete(Key(providerId));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held by an antivirus for a moment: at worst the batch resumes at the next start.
                Debug.WriteLine($"Deleting the upload queue of {providerId} failed: {ex.GetType().Name}");
            }
            finally
            {
                _gate.Release();
            }
        }

        private static string Key(string providerId) => $"upload-queue-{providerId}";
    }
}
