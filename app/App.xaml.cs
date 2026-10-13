using System;
using System.IO;
using System.Threading.Tasks;
using BoxeZone.Helpers;
using BoxeZone.Services;
using BoxeZone.Services.Storage;
using BoxeZone.ViewModels;
using BoxeZone.Views;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;

namespace BoxeZone
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private MainWindow? _window;
        private TabWindow? _tabWindow;
        private WindowDockService? _dockService;
        private HotkeyService? _hotkeyService;
        private SettingsService? _settingsService;
        private NotificationService? _notificationService;
        private ShareLinkService? _shareLinks;
        private UpdateService? _updateService;
        private DispatcherQueueTimer? _updateTimer;
        private TaskbarIcon? _trayIcon;

        public App()
        {
            InitializeComponent();
            UnhandledException += (_, e) => CrashLog.Write(e.Exception);
        }

        public static WindowDockService? DockService => ((App)Current)._dockService;

        /// <summary>Another launch of BoxeZone was redirected here (Program.Main). Called off the UI thread.</summary>
        public static void OnActivatedByAnotherLaunch()
        {
            var app = (App)Current;
            app._window?.DispatcherQueue.TryEnqueue(() => app._dockService?.ShowNormal());
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _settingsService = new SettingsService(DispatcherQueue.GetForCurrentThread());
            UpdateService.ApplyInstallerChoice(_settingsService);
            AppCredentials credentials = AppCredentials.Load();
            var tokenStore = new TokenStore();

            // The shortcut lives on the tab window: it exists before the widget and never closes early.
            _tabWindow = new TabWindow();
            _hotkeyService = new HotkeyService(_tabWindow.GetHwnd());
            var settings = new SettingsViewModel(_settingsService, _hotkeyService);

            // The tab window exists first and never closes early: it owns what the app puts on the clipboard.
            _shareLinks = new ShareLinkService(_tabWindow.GetHwnd());

            var viewModel = new MainViewModel(
                _settingsService,
                settings,
                _shareLinks,
                new GoogleDriveProvider(credentials.Google, tokenStore),
                new OneDriveProvider(credentials.MicrosoftClientId, tokenStore),
                new DropboxProvider(credentials.DropboxAppKey, tokenStore));
            _window = new MainWindow(viewModel);
            _dockService = new WindowDockService(_window, _tabWindow, viewModel, _settingsService);

            // If another app already owns the shortcut, BoxeZone still works through the tray icon.
            _hotkeyService.Pressed += (_, _) => _dockService.ToggleNormalHidden();
            settings.RegisterSavedHotkey();

            ApplyTheme(settings.Theme);
            StartupService.Apply(settings.StartWithWindows);
            settings.PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(SettingsViewModel.Theme):
                        ApplyTheme(settings.Theme);
                        break;
                    case nameof(SettingsViewModel.StartWithWindows):
                        StartupService.Apply(settings.StartWithWindows);
                        break;
                }
            };

            ProtocolService.Register();
            _notificationService = new NotificationService();
            _notificationService.Register();
            foreach (ProviderViewModel provider in viewModel.Providers)
            {
                provider.UploadFinished += (sender, e) => NotifyUploadFinished((ProviderViewModel)sender!, e, settings);
            }

            CreateTrayIcon();

            // Started at sign-in ("Iniciar com o Windows"): straight to the tray.
            _dockService.Start(startHidden: StartupService.WasLaunchedAtSignIn);

            // Saved sessions reconnect in the background; the widget is usable meanwhile.
            _ = viewModel.RestoreSessionsAsync();

            // Once a day, a new GitHub Release is announced once and listed in Settings. Checked at
            // start and again while the app keeps running (offline at sign-in, days in the tray).
            _updateService = new UpdateService(_settingsService);
            _ = CheckForUpdateAsync(settings);
            _updateTimer = _window.DispatcherQueue.CreateTimer();
            _updateTimer.Interval = UpdateService.PollInterval;
            _updateTimer.Tick += (_, _) => _ = CheckForUpdateAsync(settings);
            _updateTimer.Start();
        }

        private async Task CheckForUpdateAsync(SettingsViewModel settings)
        {
            UpdateInfo? update = await _updateService!.CheckAsync();
            if (update is null)
            {
                return;
            }

            settings.ShowUpdate(update);
            string version = update.Version.ToString(3);
            if (_settingsService!.Settings.NotifiedVersion != version)
            {
                _notificationService?.ShowUpdateAvailable(version, update.Url);
                _settingsService.Settings.NotifiedVersion = version;
                _settingsService.Save();
            }
        }

        private void NotifyUploadFinished(ProviderViewModel provider, UploadFinishedEventArgs e, SettingsViewModel settings)
        {
            if (!settings.NotifyOnComplete || _notificationService is null)
            {
                return;
            }

            if (e.Succeeded)
            {
                _notificationService.ShowUploaded(provider.Name, e.FileNames, e.Link);
            }
            else
            {
                _notificationService.ShowFailed(provider.Name, e.FileNames[0], e.ErrorMessage ?? string.Empty);
            }
        }

        private void ApplyTheme(AppTheme theme)
        {
            ElementTheme elementTheme = theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
            _window?.ApplyTheme(elementTheme);
            _tabWindow?.ApplyTheme(elementTheme);
        }

        private void CreateTrayIcon()
        {
            var strings = new ResourceLoader();

            // PopupMenu mode turns the flyout into a native menu and runs each item's Command;
            // the items' Click event is never raised there.
            var openItem = new MenuFlyoutItem
            {
                Text = strings.GetString("TrayOpen"),
                Command = new RelayCommand(() => _dockService?.ShowNormal()),
            };
            var exitItem = new MenuFlyoutItem
            {
                Text = strings.GetString("TrayExit"),
                Command = new RelayCommand(ExitApp),
            };

            var menu = new MenuFlyout();
            menu.Items.Add(openItem);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(exitItem);

            _trayIcon = new TaskbarIcon
            {
                ToolTipText = strings.GetString("AppTitle"),
                Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "BoxeZone.ico")),
                ContextMenuMode = ContextMenuMode.PopupMenu,
                ContextFlyout = menu,
                NoLeftClickDelay = true,
                LeftClickCommand = new RelayCommand(() => _dockService?.ShowNormal()),
            };

            // Efficiency Mode would throttle uploads while the window is hidden.
            _trayIcon.ForceCreate(enablesEfficiencyMode: false);
        }

        private void ExitApp()
        {
            _settingsService?.SaveNow();
            _trayIcon?.Dispose();
            _hotkeyService?.Dispose();
            _dockService?.Shutdown();
            Exit();
        }
    }
}
