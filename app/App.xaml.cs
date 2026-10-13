using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BoxeZone.Helpers;
using BoxeZone.Services;
using BoxeZone.Services.Storage;
using BoxeZone.Services.Upload;
using BoxeZone.ViewModels;
using BoxeZone.Views;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.AppLifecycle;

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
        private string? _readyUpdate;
        private UpdateService? _updateService;
        private DispatcherQueueTimer? _updateTimer;
        private TaskbarIcon? _trayIcon;

        public App()
        {
            InitializeComponent();
            UnhandledException += (_, e) => CrashLog.Write(e.Exception);
        }

        public static WindowDockService? DockService => ((App)Current)._dockService;

        /// <summary>
        /// Another launch of BoxeZone was redirected here (Program.Main). Called off the UI thread.
        /// "boxezone:copy-link?batch=…" (notification button) copies that batch's links without
        /// showing the widget; anything else, or a batch no longer remembered, shows the widget.
        /// </summary>
        public static void OnActivatedByAnotherLaunch(AppActivationArguments args)
        {
            var app = (App)Current;
            string? arguments = ProtocolService.GetArguments(args);
            string? batchId = ProtocolService.FindCopyLinkBatch(arguments);
            bool update = arguments?.Contains(ProtocolService.UpdateUri, StringComparison.OrdinalIgnoreCase) == true;
            app._window?.DispatcherQueue.TryEnqueue(async () =>
            {
                if (update)
                {
                    InstallUpdate();
                    return;
                }

                try
                {
                    if (batchId is not null && await app.CopyBatchLinksAsync(batchId))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex);
                }

                app._dockService?.ShowNormal();
            });
        }

        private async Task<bool> CopyBatchLinksAsync(string batchId)
        {
            if (_shareLinks is null || await _shareLinks.CopyBatchAsync(batchId) is not { } copy)
            {
                return false;
            }

            if (copy.Result.Succeeded)
            {
                _notificationService?.ShowLinkCopied(copy.Batch.Items.Select(i => i.Name).ToList());
            }
            else
            {
                _notificationService?.ShowLinkFailed(ShareLinkService.DescribeReason(copy.Result.Error!.Value, copy.Batch.Provider.DisplayName));
            }

            return true;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _settingsService = new SettingsService(DispatcherQueue.GetForCurrentThread());

            // Before any text loads: x:Uid and every ResourceLoader read the language once.
            LanguageService.Apply(_settingsService.Settings.Language);
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
                new UploadQueueStore(tokenStore),
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

            // Downloaded and checked in the background; installed only on "Atualizar".
            _readyUpdate = await _updateService.PrepareAsync(update);
            settings.ShowUpdate(update, _readyUpdate is not null);
            string version = update.Version.ToString(3);
            if (_settingsService!.Settings.NotifiedVersion != version)
            {
                if (_readyUpdate is not null)
                {
                    _notificationService?.ShowUpdateReady(version);
                }
                else
                {
                    _notificationService?.ShowUpdateAvailable(version, update.Url);
                }

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
                _notificationService.ShowUploaded(provider.Name, e.FileNames, e.Link, e.BatchId);
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

        /// <summary>
        /// "Atualizar" (Settings or the notification): runs the downloaded installer silently and
        /// closes; the installer opens the new version. Uploads in progress continue after it
        /// (the saved queue). Nothing downloaded yet: shows the widget.
        /// </summary>
        public static void InstallUpdate()
        {
            var app = (App)Current;
            if (app._readyUpdate is { } installer && File.Exists(installer))
            {
                UpdateService.Install(installer);
                app.ExitApp();
            }
            else
            {
                app._dockService?.ShowNormal();
            }
        }

        /// <summary>Starts a new BoxeZone that waits for this one to close, then closes this one (language change).</summary>
        public static void Restart()
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"{Program.RestartArgument} {Environment.ProcessId}")
            {
                UseShellExecute = false,
            });
            ((App)Current).ExitApp();
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
