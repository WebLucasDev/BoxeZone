using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BoxeZone.Services.Storage;

namespace BoxeZone.Services
{
    /// <summary>One finished batch: what the history lists, opens and shares again.</summary>
    public sealed record HistoryEntry(string ProviderId, string Title, DateTimeOffset When, string? OpenUrl, IReadOnlyList<UploadedItem> Items);

    /// <summary>
    /// The last uploads, newest first, in %LocalAppData%\BoxeZone\history.json (names and owner
    /// links, which only open for the account owner; no tokens). Kept to <see cref="MaxEntries"/>.
    /// </summary>
    public sealed class HistoryService
    {
        public const int MaxEntries = 50;

        private readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoxeZone", "history.json");

        private readonly SemaphoreSlim _gate = new(1, 1);

        public List<HistoryEntry> Load()
        {
            try
            {
                return File.Exists(_path)
                    ? JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_path)) ?? []
                    : [];
            }
            catch (Exception ex)
            {
                // Damaged or from another version: start a new history.
                Debug.WriteLine($"Reading the history failed: {ex.GetType().Name}");
                return [];
            }
        }

        /// <summary>Serialized right away (call on the UI thread); written aside and swapped.</summary>
        public async Task SaveAsync(IReadOnlyList<HistoryEntry> entries)
        {
            string json = JsonSerializer.Serialize(entries);
            await _gate.WaitAsync();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temporary = _path + ".tmp";
                await File.WriteAllTextAsync(temporary, json);
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The history is a convenience: losing a write is not worth an error.
                Debug.WriteLine($"Saving the history failed: {ex.GetType().Name}");
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
