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
        // The app logo (Assets/BoxeZone.svg): boxing glove on the accent square.
        private const string Logo =
            "<svg width=\"40\" height=\"40\" viewBox=\"0 0 256 256\" aria-hidden=\"true\">" +
            "<rect width=\"256\" height=\"256\" rx=\"56\" fill=\"#4CC2FF\"/>" +
            "<g transform=\"translate(-6 4) rotate(40 132 128)\" fill=\"#0F1216\">" +
            "<rect x=\"98\" y=\"180\" width=\"86\" height=\"52\" rx=\"12\"/>" +
            "<path d=\"M120 198h42M120 214h42\" stroke=\"#4CC2FF\" stroke-width=\"7\" stroke-linecap=\"round\"/>" +
            "<path d=\"M104 172C98 150 96 128 98 108C100 56 126 26 162 28C194 30 210 60 206 100C202 136 192 156 178 172Z\"/>" +
            "<path d=\"M100 172C78 162 66 138 70 116C74 94 92 86 110 92C127 99 132 118 123 134C116 147 109 159 107 172Z\" stroke=\"#4CC2FF\" stroke-width=\"9\" stroke-linejoin=\"round\"/>" +
            "<path d=\"M176 50C190 60 196 76 194 94\" fill=\"none\" stroke=\"#4CC2FF\" stroke-width=\"8\" stroke-linecap=\"round\"/>" +
            "</g></svg>";

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
            var strings = new ResourceLoader();
            string title = WebUtility.HtmlEncode(strings.GetString("AppTitle"));
            string message = WebUtility.HtmlEncode(strings.GetString("SignedInPageMessage"));
            return $"<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>{title}</title>" +
                $"<style>{Style}</style></head><body><main>{Logo}<p>{message}</p></main></body></html>";
        }
    }
}
