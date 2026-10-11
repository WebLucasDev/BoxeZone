using System.Net;
using Microsoft.Windows.ApplicationModel.Resources;

namespace BoxeZone.Services.Storage
{
    /// <summary>Page shown in the browser once a provider sign-in completes.</summary>
    internal static class SignInPage
    {
        public static string Build()
        {
            string message = WebUtility.HtmlEncode(new ResourceLoader().GetString("SignedInPageMessage"));
            return "<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>BoxeZone</title></head>" +
                "<body style=\"font-family:'Segoe UI',sans-serif;padding:48px;color:#1b1b1b\">" +
                $"<p style=\"font-size:15px\">{message}</p></body></html>";
        }
    }
}
