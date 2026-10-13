using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BoxeZone.Helpers;
using Microsoft.Windows.AppLifecycle;

namespace BoxeZone
{
    /// <summary>
    /// Entry point, replacing the XAML-generated Main (DISABLE_XAML_GENERATED_MAIN in the
    /// .csproj) so only one BoxeZone runs: launching it again shows the running one.
    /// </summary>
    public static class Program
    {
        private const string InstanceKey = "BoxeZone";

        /// <summary>"--after &lt;pid&gt;": a restart (language change); the old instance is still closing.</summary>
        public const string RestartArgument = "--after";

        [STAThread]
        private static void Main()
        {
            bool restarting = WaitForPreviousInstance();
            AppInstance instance = AppInstance.FindOrRegisterForKey(InstanceKey);

            // A restart must never hand itself over to the instance it replaces (that one is
            // closing and would never answer): wait until the key is free instead.
            for (int attempt = 0; restarting && !instance.IsCurrent && attempt < 60; attempt++)
            {
                Thread.Sleep(500);
                instance = AppInstance.FindOrRegisterForKey(InstanceKey);
            }
            if (!instance.IsCurrent)
            {
                // Hand this launch to the running instance and quit. The call runs off the
                // STA thread so the cross-process wait cannot deadlock on it.
                // This process got the user's launch, so it may let the running one take the foreground.
                Win32.AllowSetForegroundWindow(instance.ProcessId);
                AppActivationArguments args = AppInstance.GetCurrent().GetActivatedEventArgs();
                Task.Run(() => instance.RedirectActivationToAsync(args).AsTask()).Wait();
                return;
            }

            instance.Activated += (_, e) => App.OnActivatedByAnotherLaunch(e);
            XamlGeneratedProgram.XamlGeneratedMain();
        }

        /// <summary>
        /// Without this wait, the new process would find the old one still registered and hand
        /// itself over to it (single instance), and nothing would restart.
        /// </summary>
        /// <returns>True when this launch is a restart.</returns>
        private static bool WaitForPreviousInstance()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, RestartArgument);
            if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out int processId))
            {
                return false;
            }

            try
            {
                using Process previous = Process.GetProcessById(processId);
                previous.WaitForExit(30_000);
            }
            catch (ArgumentException)
            {
                // Already gone.
            }

            return true;
        }
    }
}
