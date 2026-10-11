using System.Threading.Tasks;
using Google.Apis.Json;
using Google.Apis.Util.Store;

namespace BoxeZone.Services.Storage
{
    /// <summary>
    /// Lets the Google auth library keep its tokens in the encrypted <see cref="TokenStore"/>
    /// instead of FileDataStore, which writes them as plain JSON.
    /// </summary>
    public sealed class GoogleTokenDataStore(TokenStore store) : IDataStore
    {
        private const string KeyPrefix = "gdrive-";

        public Task StoreAsync<T>(string key, T value) =>
            store.WriteAsync(GetKey<T>(key), NewtonsoftJsonSerializer.Instance.Serialize(value));

        public Task DeleteAsync<T>(string key)
        {
            store.Delete(GetKey<T>(key));
            return Task.CompletedTask;
        }

        public async Task<T> GetAsync<T>(string key)
        {
            string? json = await store.ReadAsync(GetKey<T>(key));
            return json is null ? default! : NewtonsoftJsonSerializer.Instance.Deserialize<T>(json);
        }

        public Task ClearAsync()
        {
            store.DeleteAll(KeyPrefix);
            return Task.CompletedTask;
        }

        // Same key shape as Google's FileDataStore: one entry per stored type and user.
        private static string GetKey<T>(string key) => $"{KeyPrefix}{typeof(T).FullName}-{key}";
    }
}
