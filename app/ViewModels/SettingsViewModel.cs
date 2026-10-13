using System;
using BoxeZone.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
            CheckForUpdates = settings.CheckForUpdates;
            Theme = settings.Theme;
            Language = settings.Language;
            _languageAtStart = settings.Language;
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

        /// <summary>"Verificar versões novas": the daily look at GitHub Releases (UpdateService).</summary>
        [ObservableProperty]
        public partial bool CheckForUpdates { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSystemTheme), nameof(IsLightTheme), nameof(IsDarkTheme))]
        public partial AppTheme Theme { get; set; }

        /// <summary>Saved at once; the interface only changes after a restart ("Reiniciar").</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSystemLanguage), nameof(IsPortugueseLanguage), nameof(IsEnglishLanguage), nameof(LanguageNeedsRestart))]
        public partial AppLanguage Language { get; set; }

        private readonly AppLanguage _languageAtStart;

        public bool LanguageNeedsRestart => Language != _languageAtStart;

        public bool IsSystemLanguage
        {
            get => Language == AppLanguage.System;
            set { if (value) Language = AppLanguage.System; }
        }

        public bool IsPortugueseLanguage
        {
            get => Language == AppLanguage.Portuguese;
            set { if (value) Language = AppLanguage.Portuguese; }
        }

        public bool IsEnglishLanguage
        {
            get => Language == AppLanguage.English;
            set { if (value) Language = AppLanguage.English; }
        }

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

        private bool _updateReady;

        /// <summary>"Atualizar para a 1.4.0" once the installer is downloaded and checked; "Baixar a 1.4.0" (the release page) otherwise.</summary>
        public void ShowUpdate(UpdateInfo update, bool ready)
        {
            _updateReady = ready;
            UpdateUri = new Uri(update.Url);
            UpdateText = string.Format(_strings.GetString(ready ? "UpdateInstallFormat" : "UpdateDownloadFormat"), update.Version.ToString(3));
        }

        [RelayCommand]
        private void Update()
        {
            if (_updateReady)
            {
                App.InstallUpdate();
            }
            else if (UpdateUri is not null)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UpdateUri.AbsoluteUri) { UseShellExecute = true });
            }
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

        partial void OnCheckForUpdatesChanged(bool value) => Persist(() => Settings.CheckForUpdates = value);

        partial void OnThemeChanged(AppTheme value) => Persist(() => Settings.Theme = value);

        partial void OnLanguageChanged(AppLanguage value) => Persist(() => Settings.Language = value);

        [RelayCommand]
        private void RestartForLanguage() => App.Restart();

        private void Persist(System.Action apply)
        {
            apply();
            _settingsService.Save();
        }
    }
}
