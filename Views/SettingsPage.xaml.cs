using BoxeZone.Services;
using BoxeZone.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Windows.UI.Core;

namespace BoxeZone.Views
{
    /// <summary>Settings: accounts and folders, behavior, shortcut and theme.</summary>
    public sealed partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            InitializeComponent();

            // Same as the widget: no focus rectangle on the first control when the page opens.
            Loaded += (_, _) => Focus(FocusState.Programmatic);
            LosingFocus += Page_LosingFocus;
        }

        public MainViewModel ViewModel { get; private set; } = null!;

        private SettingsViewModel Settings => ViewModel.Settings;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            ViewModel = (MainViewModel)e.Parameter;
            base.OnNavigatedTo(e);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            // Leaving mid-capture must give the old shortcut back.
            if (Settings.IsCapturingHotkey)
            {
                Settings.CancelHotkeyCapture();
            }

            foreach (ProviderViewModel provider in ViewModel.Providers)
            {
                provider.CancelFolderEditCommand.Execute(null);
            }

            base.OnNavigatedFrom(e);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e) => Frame.GoBack(new SuppressNavigationTransitionInfo());

        private void CloseButton_Click(object sender, RoutedEventArgs e) => App.DockService?.Hide();

        private void HotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (Settings.IsCapturingHotkey)
            {
                Settings.CancelHotkeyCapture();
            }
            else
            {
                Settings.BeginHotkeyCapture();
            }
        }

        /// <summary>
        /// While capturing, focus stays inside the page. A click on anything that cannot take
        /// focus (a label, an empty area) moves focus to WinUI's root ScrollViewer, above the
        /// page, and the page would stop seeing the keys: only the keyboard counts, not the mouse.
        /// </summary>
        private void Page_LosingFocus(UIElement sender, LosingFocusEventArgs args)
        {
            if (Settings.IsCapturingHotkey && !IsInsidePage(args.NewFocusedElement))
            {
                args.TryCancel();
            }
        }

        private bool IsInsidePage(DependencyObject? element)
        {
            for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current == this)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>While capturing, the next key combination becomes the global shortcut.</summary>
        private void Page_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (!Settings.IsCapturingHotkey)
            {
                return;
            }

            e.Handled = true;
            VirtualKey key = e.Key;
            if (IsModifierKey(key))
            {
                return;
            }

            HotkeyModifiers modifiers = GetPressedModifiers();
            if (key == VirtualKey.Escape && modifiers == HotkeyModifiers.None)
            {
                Settings.CancelHotkeyCapture();
                return;
            }

            Settings.CompleteHotkeyCapture(modifiers, (uint)key);
        }

        private void ThemeSystem_Checked(object sender, RoutedEventArgs e) => Settings.Theme = AppTheme.System;

        private void ThemeLight_Checked(object sender, RoutedEventArgs e) => Settings.Theme = AppTheme.Light;

        private void ThemeDark_Checked(object sender, RoutedEventArgs e) => Settings.Theme = AppTheme.Dark;

        private static bool IsModifierKey(VirtualKey key) => key is
            VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
            VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
            VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
            VirtualKey.LeftWindows or VirtualKey.RightWindows;

        private static HotkeyModifiers GetPressedModifiers()
        {
            HotkeyModifiers modifiers = HotkeyModifiers.None;
            if (IsDown(VirtualKey.Control)) modifiers |= HotkeyModifiers.Control;
            if (IsDown(VirtualKey.Menu)) modifiers |= HotkeyModifiers.Alt;
            if (IsDown(VirtualKey.Shift)) modifiers |= HotkeyModifiers.Shift;
            if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) modifiers |= HotkeyModifiers.Windows;
            return modifiers;
        }

        private static bool IsDown(VirtualKey key) =>
            InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    }
}
