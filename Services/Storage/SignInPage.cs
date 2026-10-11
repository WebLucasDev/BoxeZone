using System.Net;
using Microsoft.Windows.ApplicationModel.Resources;

namespace BoxeZone.Services.Storage
{
    /// <summary>
    /// Page shown in the browser once a provider sign-in completes: the app's logo and one
    /// line, following the browser's light/dark preference.
    /// </summary>
    internal static class SignInPage
    {
        // The logo from docs/widget.dc.html: accent square with the upload arrow.
        private const string Logo =
            "<svg width=\"40\" height=\"40\" viewBox=\"0 0 20 20\" aria-hidden=\"true\">" +
            "<rect width=\"20\" height=\"20\" rx=\"5\" fill=\"#4CC2FF\"/>" +
            "<g transform=\"translate(4 4) scale(.75)\" fill=\"none\" stroke=\"#0F1216\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">" +
            "<path d=\"M8 11V3\"/><path d=\"M4.5 6.5L8 3l3.5 3.5\"/><path d=\"M3 13h10\"/></g></svg>";

        private const string Style =
            ":root{color-scheme:light dark}" +
            "body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;" +
            "font-family:'Segoe UI Variable Text','Segoe UI',sans-serif;background:#f3f3f3;color:#1b1b1b}" +
            "main{display:flex;align-items:center;gap:16px;padding:24px 28px;border-radius:8px;background:#fff;" +
            "border:1px solid rgba(0,0,0,.08);box-shadow:0 8px 24px rgba(0,0,0,.08)}" +
            "p{margin:0;font-size:15px}" +
            "@media (prefers-color-scheme:dark){body{background:#1c2733;color:#f3f3f3}" +
            "main{background:rgba(32,34,40,.9);border-color:rgba(255,255,255,.12);box-shadow:0 16px 40px rgba(0,0,0,.4)}}";

        public static string Build()
        {
            string message = WebUtility.HtmlEncode(new ResourceLoader().GetString("SignedInPageMessage"));
            return "<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>BoxeZone</title>" +
                $"<style>{Style}</style></head><body><main>{Logo}<p>{message}</p></main></body></html>";
        }
    }
}
