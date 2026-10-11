using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace BoxeZone.Services
{
    /// <summary>
    /// Windows notifications ("Notificar ao concluir") through the OS toast API.
    /// </summary>
    /// <remarks>
    /// AppNotificationManager (Windows App SDK) fails to register in a self-contained
    /// unpackaged app (0x8007007E), so the app registers its own AppUserModelID under
    /// HKCU\Software\Classes\AppUserModelId with a name and icon. Clicks are handled while the
    /// app runs; a notification left in the Action Center after "Sair" does nothing.
    /// </remarks>
    public sealed class NotificationService
    {
        private const string AppUserModelId = "BoxeZone.App";
        private const string OpenAction = "open";

        private readonly ResourceLoader _strings = new();
        private readonly Action _showWidget;
        private ToastNotifier? _notifier;

        /// <param name="showWidget">Called (off the UI thread) when the notification itself is clicked.</param>
        public NotificationService(Action showWidget)
        {
            _showWidget = showWidget;
        }

        public void Register()
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
                key.SetValue("DisplayName", _strings.GetString("AppTitle"));
                key.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Assets", "BoxeZone.png"));
                _notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
            }
            catch (Exception ex)
            {
                // Notifications are a nicety; the app works without them.
                CrashLog.Write(ex);
                _notifier = null;
            }
        }

        public void ShowUploaded(string providerName, IReadOnlyList<string> fileNames, string? link)
        {
            string body = fileNames.Count == 1
                ? fileNames[0]
                : string.Format(_strings.GetString("NotificationFilesFormat"), fileNames.Count);

            string launch = link is null ? string.Empty : $"{OpenAction}|{link}";
            string actions = link is null
                ? string.Empty
                : $"<actions><action content=\"{Escape(_strings.GetString("NotificationOpen"))}\" arguments=\"{Escape(launch)}\" activationType=\"foreground\"/></actions>";

            Show(string.Format(_strings.GetString("NotificationUploadedFormat"), providerName), body, launch, actions);
        }

        public void ShowFailed(string providerName, string fileName, string reason) =>
            Show(string.Format(_strings.GetString("NotificationFailedFormat"), providerName), $"{fileName} · {reason}", string.Empty, string.Empty);

        private void Show(string title, string body, string launch, string actions)
        {
            if (_notifier is null)
            {
                return;
            }

            var xml = new XmlDocument();
            xml.LoadXml(
                $"<toast launch=\"{Escape(launch)}\"><visual><binding template=\"ToastGeneric\">" +
                $"<text>{Escape(title)}</text><text>{Escape(body)}</text>" +
                $"</binding></visual>{actions}</toast>");

            var toast = new ToastNotification(xml);
            toast.Activated += (_, args) => HandleActivation((args as ToastActivatedEventArgs)?.Arguments);
            _notifier.Show(toast);
        }

        private void HandleActivation(string? arguments)
        {
            // "open|<url>" from the notification body or the "Abrir" button.
            if (arguments is not null && arguments.StartsWith(OpenAction + "|", StringComparison.Ordinal))
            {
                string link = arguments[(OpenAction.Length + 1)..];
                Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            }
            else
            {
                _showWidget();
            }
        }

        private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
    }
}
