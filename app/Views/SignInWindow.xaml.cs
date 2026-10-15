using System;
using System.Threading;
using System.Threading.Tasks;
using BoxeZone.Helpers;
using BoxeZone.Services.Storage;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace BoxeZone.Views
{
    /// <summary>
    /// E-mail and password sign-in, for providers without browser sign-in (MEGA). Each
    /// "Entrar" runs the provider's sign-in; the window stays open, with the reason, until it
    /// works. The password only passes through here: it is never saved.
    /// </summary>
    public sealed partial class SignInWindow : Window
    {
        private const double ContentWidth = 360;

        private static readonly ResourceLoader Strings = new();

        private readonly Func<SignInCredentials, CancellationToken, Task<SignInOutcome>> _signIn;
        private readonly TaskCompletionSource _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationToken _ct;
        private bool _busy;

        public SignInWindow(string providerName, Uri logo, Func<SignInCredentials, CancellationToken, Task<SignInOutcome>> signIn, ElementTheme theme)
        {
            _signIn = signIn;
            InitializeComponent();

            Title = string.Format(Strings.GetString("SignInTitleFormat"), providerName);
            TitleText.Text = Title;
            NoteText.Text = string.Format(Strings.GetString("SignInNoteFormat"), providerName);
            LogoSource.UriSource = logo;
            Root.RequestedTheme = theme;

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarArea);
            AppWindow.TitleBar.PreferredTheme = theme switch
            {
                ElementTheme.Light => TitleBarTheme.Light,
                ElementTheme.Dark => TitleBarTheme.Dark,
                _ => TitleBarTheme.UseDefaultAppMode,
            };

            var presenter = OverlappedPresenter.Create();
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;

            // The widget is always on top: without this, the window could open behind it.
            presenter.IsAlwaysOnTop = true;
            AppWindow.SetPresenter(presenter);
            AppWindow.SetIcon("Assets/BoxeZone.ico");

            Closed += (_, _) => _result.TrySetCanceled();
            Root.Loaded += (_, _) => EmailBox.Focus(FocusState.Programmatic);
        }

        /// <summary>Shows the window; completes once signed in, canceled when closed or <paramref name="ct"/> is canceled.</summary>
        public async Task RunAsync(CancellationToken ct)
        {
            _ct = ct;
            using CancellationTokenRegistration closeOnCancel = ct.Register(() => DispatcherQueue.TryEnqueue(Close));

            FitToContent(center: true);
            Activate();
            await _result.Task;
        }

        private async void SignInButton_Click(object sender, RoutedEventArgs e) => await SubmitAsync();

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

        private async void Field_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await SubmitAsync();
            }
        }

        private async Task SubmitAsync()
        {
            string email = EmailBox.Text.Trim();
            string password = PasswordBox.Password;
            string? code = CodePanel.Visibility == Visibility.Visible ? CodeBox.Text.Trim() : null;
            if (_busy || email.Length == 0 || password.Length == 0 || code is { Length: 0 })
            {
                return;
            }

            SetBusy(true);
            ShowError(null);
            SignInOutcome outcome;
            try
            {
                outcome = await _signIn(new SignInCredentials(email, password, code), _ct);
            }
            catch (OperationCanceledException)
            {
                // Closing: the window goes away.
                return;
            }
            finally
            {
                SetBusy(false);
            }

            switch (outcome)
            {
                case SignInOutcome.SignedIn:
                    PasswordBox.Password = string.Empty;
                    _result.TrySetResult();
                    Close();
                    return;
                case SignInOutcome.CodeRequired:
                    CodePanel.Visibility = Visibility.Visible;
                    CodeBox.Focus(FocusState.Programmatic);
                    break;
                case SignInOutcome.WrongCode:
                    ShowError(Strings.GetString("SignInWrongCode"));
                    CodeBox.SelectAll();
                    CodeBox.Focus(FocusState.Programmatic);
                    break;
                case SignInOutcome.WrongCredentials:
                    ShowError(Strings.GetString("SignInWrongCredentials"));
                    PasswordBox.SelectAll();
                    PasswordBox.Focus(FocusState.Programmatic);
                    break;
                case SignInOutcome.Offline:
                    ShowError(Strings.GetString("ErrorOffline"));
                    break;
                default:
                    ShowError(Strings.GetString("SignInFailed"));
                    break;
            }

            FitToContent(center: false);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            BusyRing.IsActive = busy;
            EmailBox.IsEnabled = !busy;
            PasswordBox.IsEnabled = !busy;
            CodeBox.IsEnabled = !busy;
            SignInButton.IsEnabled = !busy;
        }

        private void ShowError(string? message)
        {
            ErrorText.Text = message ?? string.Empty;
            ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// The window takes the content's height (the code field and the error line add to it);
        /// the first time, centered on the screen with the mouse, where the user just clicked.
        /// </summary>
        private void FitToContent(bool center)
        {
            Root.Measure(new Size(ContentWidth, double.PositiveInfinity));
            double scale = this.GetScale();
            var size = new SizeInt32((int)Math.Ceiling(ContentWidth * scale), (int)Math.Ceiling(Root.DesiredSize.Height * scale));
            AppWindow.ResizeClient(size);

            if (center)
            {
                Win32.GetCursorPos(out Win32.POINT cursor);
                RectInt32 work = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary).WorkArea;
                SizeInt32 outer = AppWindow.Size;
                AppWindow.Move(new PointInt32(work.X + (work.Width - outer.Width) / 2, work.Y + (work.Height - outer.Height) / 2));
            }
        }
    }
}
