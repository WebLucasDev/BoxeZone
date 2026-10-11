using System;
using System.IO;
using BoxeZone.Helpers;
using BoxeZone.Services;
using BoxeZone.Services.Storage;
using BoxeZone.ViewModels;
using BoxeZone.Views;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
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
        // Default global shortcut: Ctrl+Alt+D (configurable in step 7).
        private const HotkeyModifiers DefaultHotkeyModifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt;
        private const uint DefaultHotkeyKey = 0x44; // VK 'D'

        private MainWindow? _window;
        private TabWindow? _tabWindow;
        private WindowDockService? _dockService;
        private HotkeyService? _hotkeyService;
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
            AppCredentials credentials = AppCredentials.Load();
            var tokenStore = new TokenStore();
            var viewModel = new MainViewModel(
                new GoogleDriveProvider(credentials.Google, tokenStore),
                new OneDriveProvider(credentials.MicrosoftClientId, tokenStore),
                new DropboxProvider(credentials.DropboxAppKey, tokenStore));
            _window = new MainWindow(viewModel);
            _tabWindow = new TabWindow();
            _dockService = new WindowDockService(_window, _tabWindow, viewModel);

            // If another app already owns the shortcut, BoxeZone still works through the tray icon.
            _hotkeyService = new HotkeyService(_window.GetHwnd());
            _hotkeyService.Pressed += (_, _) => _dockService.ToggleNormalHidden();
            _hotkeyService.Register(DefaultHotkeyModifiers, DefaultHotkeyKey);

            CreateTrayIcon();
            _dockService.ShowNormal();

            // Saved sessions reconnect in the background; the widget is usable meanwhile.
            _ = viewModel.RestoreSessionsAsync();
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
            _trayIcon?.Dispose();
            _hotkeyService?.Dispose();
            _dockService?.Shutdown();
            Exit();
        }
    }
}
