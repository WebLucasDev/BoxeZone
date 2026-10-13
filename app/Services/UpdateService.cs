using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

        // The installer is ~60 MB: the 10 s limit of the API client is far too short for it.
        private static readonly HttpClient Downloads = CreateDownloadClient();

        private readonly SettingsService _settingsService;

        // A slow download must not be started again by the next hourly check.
        private bool _preparing;

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

            // A last check in the future (the clock was corrected backwards) counts as due.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            bool isDue = settings.LastUpdateCheck is not { } last || last > now || now - last >= CheckInterval;
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
                            settings.LatestTag = tag;
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

        /// <summary>
        /// Downloads the new version's installer for this machine (x64 or ARM64) into
        /// %LocalAppData%\BoxeZone\updates and checks it against the SHA256SUMS.txt published in
        /// the same release. Returns its path when it is ready to run; null when it cannot be
        /// verified (no checksums in that release, download failed, hash differs).
        /// </summary>
        public async Task<string?> PrepareAsync(UpdateInfo update)
        {
            AppSettings settings = _settingsService.Settings;
            if (!settings.CheckForUpdates || _preparing)
            {
                return null;
            }

            string version = update.Version.ToString(3);
            string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            string assetName = $"BoxeZone-Setup-{arch}.exe";
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoxeZone", "updates");
            string path = Path.Combine(folder, $"BoxeZone-Setup-{version}-{arch}.exe");

            // Already downloaded and verified: no network (it is checked again before it runs).
            if (settings.ReadyUpdateVersion == version && settings.ReadyUpdateHash is not null && File.Exists(path))
            {
                return path;
            }

            // A failed attempt is retried the next day, like the check itself (privacy policy:
            // GitHub at most once a day), never every hour.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (settings.LastPrepareVersion == version && settings.LastPrepareAttempt is { } last && last <= now && now - last < CheckInterval)
            {
                return null;
            }

            settings.LastPrepareVersion = version;
            settings.LastPrepareAttempt = now;
            settings.ReadyUpdateVersion = null;
            settings.ReadyUpdateHash = null;
            _settingsService.Save();

            string tag = Uri.EscapeDataString(settings.LatestTag ?? $"v{version}");
            string releaseBase = $"https://github.com/WebLucasDev/BoxeZone/releases/download/{tag}/";
            _preparing = true;
            try
            {
                string sums = await Downloads.GetStringAsync(releaseBase + "SHA256SUMS.txt");
                string? expected = sums.Split('\n')
                    .Select(line => line.Trim().Split("  "))
                    .FirstOrDefault(parts => parts.Length == 2 && parts[1] == assetName)?[0];
                if (expected is null)
                {
                    return null;
                }

                Directory.CreateDirectory(folder);
                foreach (string old in Directory.EnumerateFiles(folder))
                {
                    TryDelete(old);
                }

                string partial = path + ".part";
                using (HttpResponseMessage response = await Downloads.GetAsync(releaseBase + assetName, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    await using FileStream file = File.Create(partial);
                    await response.Content.CopyToAsync(file);
                }

                if (await HashOfAsync(partial) != expected)
                {
                    // Corrupted or not the published file: never run it.
                    TryDelete(partial);
                    return null;
                }

                File.Move(partial, path, overwrite: true);
                settings.ReadyUpdateVersion = version;
                settings.ReadyUpdateHash = expected;
                _settingsService.Save();
                return path;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            {
                // Offline or no checksums in this release: the "Baixar" link still works.
                return null;
            }
            finally
            {
                _preparing = false;
            }
        }

        /// <summary>
        /// Runs the downloaded installer silently, after checking it once more against the hash
        /// it was verified with. It closes BoxeZone (taskkill), installs over it and opens it again
        /// (/RELAUNCH=1: the [Run] entry for the app's own updates). False when it did not start
        /// (changed or removed since, or blocked by an antivirus): the caller offers the release page.
        /// </summary>
        public async Task<bool> InstallAsync(string installerPath)
        {
            AppSettings settings = _settingsService.Settings;
            try
            {
                if (settings.ReadyUpdateHash is not { } expected || !File.Exists(installerPath) || await HashOfAsync(installerPath) != expected)
                {
                    settings.ReadyUpdateVersion = null;
                    settings.ReadyUpdateHash = null;
                    _settingsService.Save();
                    return false;
                }

                Process.Start(new ProcessStartInfo(installerPath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /RELAUNCH=1") { UseShellExecute = false });
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                CrashLog.Write(ex);
                return false;
            }
        }

        private static async Task<string> HashOfAsync(string path)
        {
            await using FileStream file = File.OpenRead(path);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(file));
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        /// <summary>"v1.2.0", "1.2" or "v1.2.0-rc.1" -> 1.2.0. False for anything else.</summary>
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

        private static HttpClient CreateDownloadClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BoxeZone");
            return client;
        }
    }
}
