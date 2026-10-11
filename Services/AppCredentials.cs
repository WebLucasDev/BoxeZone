using System;
using System.IO;
using System.Text.Json;

namespace BoxeZone.Services
{
    public sealed record GoogleCredentials(string ClientId, string ClientSecret);

    /// <summary>
    /// Provider client IDs and secrets from appsettings.local.json, next to the .exe
    /// (copied at build time, never committed). A provider without values is "not configured".
    /// </summary>
    public sealed class AppCredentials
    {
        private const string FileName = "appsettings.local.json";

        public GoogleCredentials? Google { get; private init; }

        /// <summary>Entra ID application (client) ID for OneDrive. Public client: no secret.</summary>
        public string? MicrosoftClientId { get; private init; }

        /// <summary>Dropbox app key. PKCE flow: no secret.</summary>
        public string? DropboxAppKey { get; private init; }

        public static AppCredentials Load()
        {
            string path = Path.Combine(AppContext.BaseDirectory, FileName);
            if (!File.Exists(path))
            {
                return new AppCredentials();
            }

            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;

            string? googleId = ReadString(root, "Google", "ClientId");
            string? googleSecret = ReadString(root, "Google", "ClientSecret");

            return new AppCredentials
            {
                Google = googleId is not null && googleSecret is not null ? new GoogleCredentials(googleId, googleSecret) : null,
                MicrosoftClientId = ReadString(root, "Microsoft", "ClientId"),
                DropboxAppKey = ReadString(root, "Dropbox", "AppKey"),
            };
        }

        private static string? ReadString(JsonElement root, string section, string key)
        {
            if (root.TryGetProperty(section, out JsonElement sectionElement) &&
                sectionElement.TryGetProperty(key, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }

            return null;
        }
    }
}
