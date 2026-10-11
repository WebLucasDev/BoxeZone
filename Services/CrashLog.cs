using System;
using System.IO;

namespace BoxeZone.Services
{
    /// <summary>
    /// Unhandled exceptions go to %LocalAppData%\BoxeZone\crash.log (type, message, stack),
    /// so a crash leaves something to diagnose. Never write tokens or file contents here.
    /// </summary>
    public static class CrashLog
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoxeZone", "crash.log");

        public static void Write(Exception exception)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTime.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Nothing else to do while crashing.
            }
        }
    }
}
