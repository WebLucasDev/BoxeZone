using System;
using System.IO;
using BoxeZone.Services;
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
        private MainWindow? _window;
        private TabWindow? _tabWindow;
        private WindowDockService? _dockService;
        private TaskbarIcon? _trayIcon;

        public App()
        {
            InitializeComponent();
        }

        public static WindowDockService? DockService => ((App)Current)._dockService;

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            var viewModel = new MainViewModel();
            _window = new MainWindow(viewModel);
            _tabWindow = new TabWindow();
            _dockService = new WindowDockService(_window, _tabWindow, viewModel);
            CreateTrayIcon();
            _dockService.ShowNormal();
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
            _dockService?.Shutdown();
            Exit();
        }
    }
}
