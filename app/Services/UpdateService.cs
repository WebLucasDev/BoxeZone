using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace BoxeZone.Services
{
    public sealed record UpdateInfo(Version Version, string Url);

    /// <summary>
    /// Looks at the latest GitHub Release at most once a day and remembers what it found, so the
    /// Settings line survives restarts. Network errors are silent and retried on the next start.
    /// </summary>
    public sealed class UpdateService
    {
        private const string LatestReleaseUrl = "https://api.github.com/repos/WebLucasDev/BoxeZone/releases/latest";
        private const string InstallerKeyPath = @"Software\BoxeZone";
        private const string DisableUpdateCheckValue = "DisableUpdateCheck";

        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

        /// <summary>
        /// How often a running app calls <see cref="CheckAsync"/>. The app lives in the tray for
        /// days, and the check at sign-in often runs before the network is up; requests still
        /// happen at most once a day.
        /// </summary>
        public static TimeSpan PollInterval { get; } = TimeSpan.FromHours(1);
        private static readonly HttpClient Http = CreateClient();

        private readonly SettingsService _settingsService;

        public UpdateService(SettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        /// <summary>This build's version, as Major.Minor.Build.</summary>
        public static Version CurrentVersion { get; } =
            Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

        /// <summary>
        /// The installer's "Verificar versões novas" box, left unchecked, leaves a one-time flag
        /// (HKCU\Software\BoxeZone\DisableUpdateCheck = 1). It becomes the saved preference on the
        /// next start and is removed, so the Settings switch stays the only control afterwards.
        /// </summary>
        public static void ApplyInstallerChoice(SettingsService settingsService)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(InstallerKeyPath, writable: true);
                if (key?.GetValue(DisableUpdateCheckValue) is int flag)
                {
                    if (flag == 1)
                    {
                        settingsService.Settings.CheckForUpdates = false;
                        settingsService.SaveNow();
                    }

                    key.DeleteValue(DisableUpdateCheckValue, throwOnMissingValue: false);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
                // The switch in Settings still works.
            }
        }

        /// <summary>The newest release if it is newer than this build; otherwise null (also when checking is off).</summary>
        public async Task<UpdateInfo?> CheckAsync()
        {
            AppSettings settings = _settingsService.Settings;
            if (!settings.CheckForUpdates)
            {
                return null;
            }

            bool isDue = settings.LastUpdateCheck is not { } last || DateTimeOffset.UtcNow - last >= CheckInterval;
            if (isDue)
            {
                try
                {
                    using HttpResponseMessage response = await Http.GetAsync(LatestReleaseUrl);

                    // 404: no release published yet. Counts as a successful check.
                    if (response.IsSuccessStatusCode)
                    {
                        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                        string? tag = json.RootElement.TryGetProperty("tag_name", out JsonElement tagElement) ? tagElement.GetString() : null;
                        string? url = json.RootElement.TryGetProperty("html_url", out JsonElement urlElement) ? urlElement.GetString() : null;
                        if (TryParseTag(tag, out Version version) && url is not null)
                        {
                            settings.LatestVersion = version.ToString(3);
                            settings.LatestUrl = url;
                        }
                    }

                    if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        settings.LastUpdateCheck = DateTimeOffset.UtcNow;
                        _settingsService.Save();
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
                {
                    // Offline, rate-limited or unexpected answer: try again on the next start.
                }
            }

            return Version.TryParse(settings.LatestVersion, out Version? latest)
                && Normalize(latest) > CurrentVersion
                && settings.LatestUrl is not null
                ? new UpdateInfo(Normalize(latest), settings.LatestUrl)
                : null;
        }

        /// <summary>"v1.2.0", "1.2" or "v1.2.0-rc.1" â†’ 1.2.0. False for anything else.</summary>
        public static bool TryParseTag(string? tag, out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(tag))
            {
                return false;
            }

            string core = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
            if (!Version.TryParse(core, out Version? parsed))
            {
                return false;
            }

            version = Normalize(parsed);
            return true;
        }

        private static Version Normalize(Version version) =>
            new(version.Major, version.Minor, Math.Max(version.Build, 0));

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BoxeZone");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }
    }
}
