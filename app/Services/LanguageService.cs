using System.Globalization;
using Microsoft.Windows.Globalization;

namespace BoxeZone.Services
{
    /// <summary>
    /// The interface language, chosen once per run before any resource loads (x:Uid and every
    /// ResourceLoader read it): Strings/pt-BR or Strings/en-US. Changing it needs a restart.
    /// </summary>
    public static class LanguageService
    {
        public const string Portuguese = "pt-BR";
        public const string English = "en-US";

        /// <summary>The language this run uses ("pt-BR" or "en-US").</summary>
        public static string Current { get; private set; } = Portuguese;

        public static void Apply(AppLanguage language)
        {
            Current = language switch
            {
                AppLanguage.Portuguese => Portuguese,
                AppLanguage.English => English,

                // Any other Windows language gets English, not the pt-BR default resources.
                _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt" ? Portuguese : English,
            };

            ApplicationLanguages.PrimaryLanguageOverride = Current;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = new CultureInfo(Current);
        }
    }
}
