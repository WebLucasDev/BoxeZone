using System;
using System.Threading;
using System.Threading.Tasks;

namespace BoxeZone.Services.Storage
{
    /// <summary>
    /// Sign-in for a provider without browser sign-in (MEGA has no OAuth): a window of the app
    /// asks for the e-mail and the password, and the verification code when the account asks for one.
    /// </summary>
    public interface ICredentialPrompt
    {
        /// <summary>
        /// Shows the sign-in window and runs <paramref name="signIn"/> on each "Entrar" until it
        /// reports <see cref="SignInOutcome.SignedIn"/>. Throws <see cref="OperationCanceledException"/>
        /// when the user closes the window or <paramref name="ct"/> is canceled.
        /// </summary>
        Task RunAsync(string providerName, Uri logo, Func<SignInCredentials, CancellationToken, Task<SignInOutcome>> signIn, CancellationToken ct);
    }

    /// <summary>What the user typed. Code: the verification code (two-factor authentication), when asked.</summary>
    public sealed record SignInCredentials(string Email, string Password, string? Code);

    public enum SignInOutcome
    {
        SignedIn,
        WrongCredentials,

        /// <summary>The account has two-factor authentication: the window asks for the code.</summary>
        CodeRequired,
        WrongCode,
        Offline,
        Failed,
    }
}
