using System;
using System.Threading;
using System.Threading.Tasks;
using BoxeZone.Services.Storage;
using Microsoft.UI.Xaml;

namespace BoxeZone.Views
{
    /// <summary>Opens a <see cref="SignInWindow"/> in the app's current theme.</summary>
    public sealed class SignInPrompt(Func<ElementTheme> theme) : ICredentialPrompt
    {
        public Task RunAsync(string providerName, Uri logo, Func<SignInCredentials, CancellationToken, Task<SignInOutcome>> signIn, CancellationToken ct) =>
            new SignInWindow(providerName, logo, signIn, theme()).RunAsync(ct);
    }
}
