using System;
using Microsoft.Win32;

namespace BoxeZone.Services
{
    /// <summary>
    /// Registers the boxezone: URI scheme under HKCU, so "boxezone:open" (a notification click
    /// with the app closed) launches the .exe. With the app already running, the single-instance
    /// redirect in Program.Main shows the widget. Rewritten on every start, like the Run entry,
    /// so it follows the .exe if it moves; the uninstaller deletes the key.
    /// </summary>
    public static class ProtocolService
    {
        public const string Scheme = "boxezone";
        public const string OpenWidgetUri = Scheme + ":open";

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
