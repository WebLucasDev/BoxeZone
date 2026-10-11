using System;
using System.IO;
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
        private TaskbarIcon? _trayIcon;

        public App()
        {
            InitializeComponent();
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
            AppCredentials credentials = AppCredentials.Load();
            var tokenStore = new TokenStore();

            // The shortcut lives on the tab window: it exists before the widget and never closes early.
            _tabWindow = new TabWindow();
            _hotkeyService = new HotkeyService(_tabWindow.GetHwnd());
            var settings = new SettingsViewModel(_settingsService, _hotkeyService);

            var viewModel = new MainViewModel(
                _settingsService,
                settings,
                new GoogleDriveProvider(credentials.Google, tokenStore),
                new OneDriveProvider(credentials.MicrosoftClientId, tokenStore),
                new DropboxProvider(credentials.DropboxAppKey, tokenStore));
            _window = new MainWindow(viewModel);
            _dockService = new WindowDockService(_window, _tabWindow, viewModel, _settingsService);

            // If another app already owns the shortcut, BoxeZone still works through the tray icon.
            _hotkeyService.Pressed += (_, _) => _dockService.ToggleNormalHidden();
            settings.RegisterSavedHotkey();

            ApplyTheme(settings.Theme);
            settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.Theme))
                {
                    ApplyTheme(settings.Theme);
                }
            };

            CreateTrayIcon();
            _dockService.Start();

            // Saved sessions reconnect in the background; the widget is usable meanwhile.
            _ = viewModel.RestoreSessionsAsync();
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

            var openItem = new MenuFlyoutItem { Text = strings.GetString("TrayOpen") };
            openItem.Click += (_, _) => _dockService?.ShowNormal();
            var exitItem = new MenuFlyoutItem { Text = strings.GetString("TrayExit") };
            exitItem.Click += (_, _) => ExitApp();

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
