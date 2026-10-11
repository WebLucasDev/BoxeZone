using System;
using System.Linq;
using Microsoft.Win32;

namespace BoxeZone.Services
{
    /// <summary>
    /// "Iniciar com o Windows" for an unpackaged app (StartupTask only exists for packaged
    /// ones): a value under HKCU\...\Run that starts the .exe with --startup, which opens
    /// straight to the tray.
    /// </summary>
    public static class StartupService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "BoxeZone";
        private const string StartupArgument = "--startup";

        public static bool WasLaunchedAtSignIn =>
            Environment.GetCommandLineArgs().Skip(1).Contains(StartupArgument, StringComparer.OrdinalIgnoreCase);

        /// <summary>Adds or removes the Run entry. Rewriting it on every start keeps the path right if the app moved.</summary>
        public static void Apply(bool enabled)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" {StartupArgument}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
    }
}
