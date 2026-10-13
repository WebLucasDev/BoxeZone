using System;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Windows.AppLifecycle;

namespace BoxeZone.Services
{
    /// <summary>
    /// Registers the boxezone: URI scheme under HKCU, so "boxezone:open" (a notification click
    /// with the app closed) launches the .exe. With the app already running, the single-instance
    /// redirect in Program.Main shows the widget, or, for "boxezone:copy-link?batch=…" (the
    /// notification's "Copiar link"), copies that batch's links. Rewritten on every start, like
    /// the Run entry, so it follows the .exe if it moves; the uninstaller deletes the key.
    /// </summary>
    public static class ProtocolService
    {
        public const string Scheme = "boxezone";
        public const string OpenWidgetUri = Scheme + ":open";

        /// <summary>The update notification's "Atualizar": installs the downloaded version.</summary>
        public const string UpdateUri = Scheme + ":update";

        private static readonly Regex CopyLinkPattern = new(@"boxezone:copy-link\?batch=([0-9a-fA-F]{32})");

        /// <summary>The notification's "Copiar link" button.</summary>
        public static string CopyLinkUri(string batchId) => $"{Scheme}:copy-link?batch={batchId}";

        /// <summary>The batch id in a "boxezone:copy-link?batch=…" launch, or null.</summary>
        public static string? FindCopyLinkBatch(string? arguments)
        {
            Match match = CopyLinkPattern.Match(arguments ?? string.Empty);
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }

        /// <summary>
        /// The command line of a launch redirected by Program.Main. The shell starts
        /// "BoxeZone.exe "boxezone:…"" (registered command), which arrives as a plain launch.
        /// </summary>
        public static string? GetArguments(AppActivationArguments args) => args.Data switch
        {
            Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch => launch.Arguments,
            Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocol => protocol.Uri?.ToString(),
            _ => null,
        };

        public static void Register()
        {
            try
            {
                string exe = Environment.ProcessPath!;
                using RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
                key.SetValue(string.Empty, "URL:Boxe Zone");
                key.SetValue("URL Protocol", string.Empty);
                using (RegistryKey icon = key.CreateSubKey("DefaultIcon"))
                {
                    icon.SetValue(string.Empty, $"\"{exe}\",0");
                }

                using RegistryKey command = key.CreateSubKey(@"shell\open\command");
                command.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
            }
            catch (Exception ex)
            {
                // Only the "open the app" click depends on it; everything else works without.
                CrashLog.Write(ex);
            }
        }
    }
}
