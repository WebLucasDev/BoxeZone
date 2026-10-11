using BoxeZone.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace BoxeZone.Views.Controls
{
    /// <summary>One provider in Settings: account status, connect/disconnect, destination folder.</summary>
    public sealed partial class AccountCard : UserControl
    {
        public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(ProviderViewModel),
            typeof(AccountCard),
            new PropertyMetadata(null, (d, _) => ((AccountCard)d).Bindings.Update()));

        public AccountCard()
        {
            InitializeComponent();
        }

        public ProviderViewModel? ViewModel
        {
            get => (ProviderViewModel?)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        private void ChangeFolderButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.EditFolderCommand.Execute(null);

            // The box only becomes visible after the next layout pass.
            DispatcherQueue.TryEnqueue(() =>
            {
                FolderBox.Focus(FocusState.Programmatic);
                FolderBox.SelectAll();
            });
        }

        private void FolderBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                ViewModel?.SaveFolderCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == VirtualKey.Escape)
            {
                ViewModel?.CancelFolderEditCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
