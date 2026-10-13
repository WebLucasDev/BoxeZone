using System;
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

        [STAThread]
        private static void Main()
        {
            AppInstance instance = AppInstance.FindOrRegisterForKey(InstanceKey);
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
    }
}
