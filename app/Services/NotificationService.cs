using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        public void ShowUploaded(string providerName, IReadOnlyList<string> fileNames, string? link, string? batchId)
        {
            var actions = new List<(string Text, string Uri)>();
            if (link is not null)
            {
                actions.Add((_strings.GetString("NotificationOpen"), link));
            }

            if (batchId is not null)
            {
                actions.Add((_strings.GetString("NotificationCopyLink"), ProtocolService.CopyLinkUri(batchId)));
            }

            Show(
                string.Format(_strings.GetString("NotificationUploadedFormat"), providerName),
                Summarize(fileNames),
                link ?? ProtocolService.OpenWidgetUri,
                actions);
        }

        public void ShowFailed(string providerName, string fileName, string reason) =>
            Show(
                string.Format(_strings.GetString("NotificationFailedFormat"), providerName),
                $"{fileName} · {reason}",
                ProtocolService.OpenWidgetUri,
                []);

        public void ShowUpdateAvailable(string version, string url) =>
            Show(
                _strings.GetString("NotificationUpdateTitle"),
                string.Format(_strings.GetString("NotificationUpdateFormat"), version),
                url,
                [(_strings.GetString("NotificationUpdateDownload"), url)]);

        /// <summary>The new version is downloaded and checked: "Atualizar" installs it (boxezone:update).</summary>
        public void ShowUpdateReady(string version) =>
            Show(
                _strings.GetString("NotificationUpdateTitle"),
                string.Format(_strings.GetString("NotificationUpdateReadyFormat"), version),
                ProtocolService.OpenWidgetUri,
                [(_strings.GetString("NotificationUpdateInstall"), ProtocolService.UpdateUri)]);

        /// <summary>Feedback for the notification's "Copiar link": silent and short-lived.</summary>
        public void ShowLinkCopied(IReadOnlyList<string> fileNames) =>
            Show(_strings.GetString("NotificationLinkCopied"), Summarize(fileNames), ProtocolService.OpenWidgetUri, [], silent: true, lifetime: TimeSpan.FromMinutes(1));

        public void ShowLinkFailed(string reason) =>
            Show(_strings.GetString("NotificationLinkFailed"), reason, ProtocolService.OpenWidgetUri, [], silent: true, lifetime: TimeSpan.FromMinutes(1));

        private string Summarize(IReadOnlyList<string> fileNames) =>
            fileNames.Count == 1 ? fileNames[0] : string.Format(_strings.GetString("NotificationFilesFormat"), fileNames.Count);

        /// <param name="uri">Opened by a click on the notification itself.</param>
        /// <param name="actions">Buttons, each opening its own URI.</param>
        /// <param name="lifetime">Removed from the notification center after this long.</param>
        private void Show(string title, string body, string uri, IReadOnlyList<(string Text, string Uri)> actions, bool silent = false, TimeSpan? lifetime = null)
        {
            if (_notifier is null)
            {
                return;
            }

            string buttons = actions.Count == 0
                ? string.Empty
                : "<actions>" + string.Concat(actions.Select(a =>
                    $"<action content=\"{Escape(a.Text)}\" arguments=\"{Escape(a.Uri)}\" activationType=\"protocol\"/>")) + "</actions>";
            string audio = silent ? "<audio silent=\"true\"/>" : string.Empty;

            var xml = new XmlDocument();
            xml.LoadXml(
                $"<toast activationType=\"protocol\" launch=\"{Escape(uri)}\"><visual><binding template=\"ToastGeneric\">" +
                $"<text>{Escape(title)}</text><text>{Escape(body)}</text>" +
                $"</binding></visual>{buttons}{audio}</toast>");

            var toast = new ToastNotification(xml);
            if (lifetime is { } span)
            {
                toast.ExpirationTime = DateTimeOffset.Now + span;
            }

            _notifier.Show(toast);
        }

        private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
    }
}
