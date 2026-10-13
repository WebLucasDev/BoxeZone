using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;

namespace BoxeZone.Services
{
    public enum AppTheme
    {
        System,
        Light,
        Dark,
    }

    /// <summary>System: Portuguese when Windows is in Portuguese, English otherwise.</summary>
    public enum AppLanguage
    {
        System,
        Portuguese,
        English,
    }

    public sealed class ProviderSettings
    {
        public bool Enabled { get; set; }

        public string TargetFolder { get; set; } = AppSettings.DefaultTargetFolder;
    }

    public sealed class HotkeySettings
    {
        public HotkeyModifiers Modifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Alt;

        public uint Key { get; set; } = 0x44; // 'D'
    }

    /// <summary>Last window placement, in physical pixels of the visible frame.</summary>
    public sealed class WindowSettings
    {
        public bool WasDocked { get; set; }

        public int X { get; set; }

        public int Y { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public int DockCenterY { get; set; }
    }

    public sealed class AppSettings
    {
        public const string DefaultTargetFolder = "/BoxeZone";

        /// <summary>Keyed by provider ID ("gdrive", "onedrive", "dropbox").</summary>
        public Dictionary<string, ProviderSettings> Providers { get; set; } = [];

        public bool StartWithWindows { get; set; }

        public bool CollapseAfterUpload { get; set; }

        public bool NotifyOnComplete { get; set; } = true;

        public HotkeySettings Hotkey { get; set; } = new();

        public AppTheme Theme { get; set; } = AppTheme.System;

        /// <summary>Applied at start (LanguageService); a change waits for a restart.</summary>
        public AppLanguage Language { get; set; } = AppLanguage.System;

        /// <summary>Null until the widget has been placed once (first run starts in the default corner).</summary>
        public WindowSettings? Window { get; set; }

        /// <summary>"Verificar versões novas": the daily look at GitHub Releases. Off, the app contacts nothing but the user's providers.</summary>
        public bool CheckForUpdates { get; set; } = true;

        /// <summary>Last successful look at GitHub Releases (at most one a day).</summary>
        public DateTimeOffset? LastUpdateCheck { get; set; }

        /// <summary>Newest release seen ("1.2.0") and its page; null until one exists.</summary>
        public string? LatestVersion { get; set; }

        public string? LatestUrl { get; set; }

        /// <summary>The release's tag ("v1.4.0"): where its installer and SHA256SUMS.txt are downloaded from.</summary>
        public string? LatestTag { get; set; }

        /// <summary>Version already announced by notification, so each one is announced once.</summary>
        public string? NotifiedVersion { get; set; }

        public ProviderSettings GetProvider(string id)
        {
            if (!Providers.TryGetValue(id, out ProviderSettings? settings))
            {
                settings = new ProviderSettings();
                Providers[id] = settings;
            }

            return settings;
        }
    }

    /// <summary>
    /// Preferences as JSON in %LocalAppData%\BoxeZone\settings.json (ApplicationData does not
    /// work unpackaged). Changes are written shortly after the last one, so dragging the
    /// window does not hit the disk on every move.
    /// </summary>
    public sealed class SettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoxeZone", "settings.json");

        private readonly DispatcherQueueTimer _saveTimer;

        public SettingsService(DispatcherQueue dispatcherQueue)
        {
            Settings = Load();
            _saveTimer = dispatcherQueue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(500);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => SaveNow();
        }

        public AppSettings Settings { get; }

        /// <summary>Schedules a write; several changes in a row produce one write.</summary>
        public void Save()
        {
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public void SaveNow()
        {
            _saveTimer.Stop();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Write to a temporary file first so a crash mid-write never leaves a broken file.
            string temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(Settings, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }

        private AppSettings Load()
        {
            try
            {
                if (File.Exists(_path))
                {
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
                }
            }
            catch (JsonException)
            {
                // Unreadable file: start over with defaults rather than refusing to start.
            }

            return new AppSettings();
        }
    }
}
