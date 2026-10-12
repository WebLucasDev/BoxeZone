using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using Microsoft.Win32;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace BoxeZone.Services
{
    /// <summary>
    /// Windows notifications ("Notificar ao concluir", new version) through the OS toast API.
    /// </summary>
    /// <remarks>
    /// AppNotificationManager (Windows App SDK) fails to register in a self-contained unpackaged
    /// app (0x8007007E), so the app registers its own AppUserModelID under
    /// HKCU\Software\Classes\AppUserModelId. Clicks use protocol activation: the shell opens the
    /// URI itself, so they work with the app closed (https links open in the browser,
    /// boxezone:open starts the app).
    /// </remarks>
    public sealed class NotificationService
    {
        private const string AppUserModelId = "BoxeZone.App";

        private readonly ResourceLoader _strings = new();
        private ToastNotifier? _notifier;

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

            Show(
                string.Format(_strings.GetString("NotificationUploadedFormat"), providerName),
                body,
                link ?? ProtocolService.OpenWidgetUri,
                link is null ? null : _strings.GetString("NotificationOpen"));
        }

        public void ShowFailed(string providerName, string fileName, string reason) =>
            Show(
                string.Format(_strings.GetString("NotificationFailedFormat"), providerName),
                $"{fileName} · {reason}",
                ProtocolService.OpenWidgetUri,
                null);

        public void ShowUpdateAvailable(string version, string url) =>
            Show(
                _strings.GetString("NotificationUpdateTitle"),
                string.Format(_strings.GetString("NotificationUpdateFormat"), version),
                url,
                _strings.GetString("NotificationUpdateDownload"));

        /// <param name="uri">Opened by a click on the notification (and on the button, if any).</param>
        /// <param name="buttonText">Optional button that opens the same URI.</param>
        private void Show(string title, string body, string uri, string? buttonText)
        {
            if (_notifier is null)
            {
                return;
            }

            string actions = buttonText is null
                ? string.Empty
                : $"<actions><action content=\"{Escape(buttonText)}\" arguments=\"{Escape(uri)}\" activationType=\"protocol\"/></actions>";

            var xml = new XmlDocument();
            xml.LoadXml(
                $"<toast activationType=\"protocol\" launch=\"{Escape(uri)}\"><visual><binding template=\"ToastGeneric\">" +
                $"<text>{Escape(title)}</text><text>{Escape(body)}</text>" +
                $"</binding></visual>{actions}</toast>");

            _notifier.Show(new ToastNotification(xml));
        }

        private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
    }
}
