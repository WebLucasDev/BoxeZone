using System;
using BoxeZone.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Windows.ApplicationModel.Resources;

namespace BoxeZone.ViewModels
{
    /// <summary>
    /// Behavior options, theme and global shortcut from the Settings screen. Every change is
    /// saved right away; the window and the app react through PropertyChanged.
    /// </summary>
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly SettingsService _settingsService;
        private readonly HotkeyService _hotkeyService;
        private readonly ResourceLoader _strings = new();

        public SettingsViewModel(SettingsService settingsService, HotkeyService hotkeyService)
        {
            _settingsService = settingsService;
            _hotkeyService = hotkeyService;

            AppSettings settings = settingsService.Settings;
            StartWithWindows = settings.StartWithWindows;
            CollapseAfterUpload = settings.CollapseAfterUpload;
            NotifyOnComplete = settings.NotifyOnComplete;
            Theme = settings.Theme;
            HotkeyText = HotkeyService.Format(settings.Hotkey.Modifiers, settings.Hotkey.Key);
        }

        private AppSettings Settings => _settingsService.Settings;

        /// <summary>Saved here; applied in step 8 (Run registry key).</summary>
        [ObservableProperty]
        public partial bool StartWithWindows { get; set; }

        [ObservableProperty]
        public partial bool CollapseAfterUpload { get; set; }

        /// <summary>Saved here; applied in step 8 (Windows notifications).</summary>
        [ObservableProperty]
        public partial bool NotifyOnComplete { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSystemTheme), nameof(IsLightTheme), nameof(IsDarkTheme))]
        public partial AppTheme Theme { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HotkeyBadgeText))]
        public partial string HotkeyText { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HotkeyBadgeText), nameof(HotkeyButtonText))]
        public partial bool IsCapturingHotkey { get; set; }

        public string HotkeyBadgeText => IsCapturingHotkey ? _strings.GetString("HotkeyCapturing") : HotkeyText;

        public string HotkeyButtonText => _strings.GetString(IsCapturingHotkey ? "HotkeyCancel" : "HotkeyChange");

        /// <summary>This build, like "1.0.0".</summary>
        public string VersionText => UpdateService.CurrentVersion.ToString(3);

        /// <summary>"Baixar a 1.2.0" when a newer release exists; null otherwise.</summary>
        [ObservableProperty]
        public partial string? UpdateText { get; set; }

        [ObservableProperty]
        public partial Uri? UpdateUri { get; set; }

        public void ShowUpdate(UpdateInfo update)
        {
            UpdateUri = new Uri(update.Url);
            UpdateText = string.Format(_strings.GetString("UpdateDownloadFormat"), update.Version.ToString(3));
        }

        /// <summary>Why the last shortcut could not be used (taken by another app, no modifier...).</summary>
        [ObservableProperty]
        public partial string? HotkeyError { get; set; }

        // Two-way targets for the three theme radio buttons.
        public bool IsSystemTheme
        {
            get => Theme == AppTheme.System;
            set { if (value) Theme = AppTheme.System; }
        }

        public bool IsLightTheme
        {
            get => Theme == AppTheme.Light;
            set { if (value) Theme = AppTheme.Light; }
        }

        public bool IsDarkTheme
        {
            get => Theme == AppTheme.Dark;
            set { if (value) Theme = AppTheme.Dark; }
        }

        /// <summary>Releases the current shortcut so pressing it is captured instead of toggling the window.</summary>
        public void BeginHotkeyCapture()
        {
            _hotkeyService.Unregister();
            HotkeyError = null;
            IsCapturingHotkey = true;
        }

        public void CompleteHotkeyCapture(HotkeyModifiers modifiers, uint virtualKey)
        {
            if (modifiers == HotkeyModifiers.None)
            {
                HotkeyError = _strings.GetString("HotkeyNeedsModifier");
                return;
            }

            IsCapturingHotkey = false;
            if (_hotkeyService.Register(modifiers, virtualKey))
            {
                Settings.Hotkey = new HotkeySettings { Modifiers = modifiers, Key = virtualKey };
                _settingsService.Save();
                HotkeyText = HotkeyService.Format(modifiers, virtualKey);
                HotkeyError = null;
            }
            else
            {
                HotkeyError = string.Format(_strings.GetString("HotkeyTakenFormat"), HotkeyService.Format(modifiers, virtualKey));
                RegisterSavedHotkey();
            }
        }

        public void CancelHotkeyCapture()
        {
            IsCapturingHotkey = false;
            HotkeyError = null;
            RegisterSavedHotkey();
        }

        /// <summary>Registers the saved shortcut. False if another app already uses it.</summary>
        public bool RegisterSavedHotkey()
        {
            bool registered = _hotkeyService.Register(Settings.Hotkey.Modifiers, Settings.Hotkey.Key);
            if (!registered)
            {
                HotkeyError = string.Format(_strings.GetString("HotkeyTakenFormat"), HotkeyText);
            }

            return registered;
        }

        partial void OnStartWithWindowsChanged(bool value) => Persist(() => Settings.StartWithWindows = value);


        partial void OnCollapseAfterUploadChanged(bool value) => Persist(() => Settings.CollapseAfterUpload = value);

        partial void OnNotifyOnCompleteChanged(bool value) => Persist(() => Settings.NotifyOnComplete = value);

        partial void OnThemeChanged(AppTheme value) => Persist(() => Settings.Theme = value);

        private void Persist(System.Action apply)
        {
            apply();
            _settingsService.Save();
        }
    }
}
