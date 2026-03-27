using System;
using Finder.ViewModels;
using Finder.Views;
using Plugin.Fingerprint;
using Plugin.Fingerprint.Abstractions;
using Xamarin.Essentials;
using Xamarin.Forms;

namespace Finder.Views
{
    public partial class PasscodePage : ContentPage
    {
        private readonly PasscodeViewModel _viewModel;
        private readonly bool _isAppStartup;

        public PasscodePage(bool isAppStartup = false)
        {
            Resources = new ResourceDictionary();
            Resources.Add("InverseBoolConverter", new Finder.Converters.InverseBoolConverter());

            InitializeComponent();

            _isAppStartup = isAppStartup;
            _viewModel = new PasscodeViewModel { IsAppStartup = isAppStartup };
            BindingContext = _viewModel;

            // Configure UI based on mode
            btnChangePasscode.IsVisible = !isAppStartup;
            btnExit.IsVisible = isAppStartup;

            // Subscribe to ViewModel events
            _viewModel.AuthenticationSucceeded += OnAuthenticationSucceeded;
            _viewModel.ShowMessage += OnShowMessage;
            _viewModel.ShowError += OnShowError;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await _viewModel.LoadStateAsync();
            await CheckAndShowBiometricButton();
        }

        // ── Biometric support ──────────────────────────────────────────────

        private async System.Threading.Tasks.Task CheckAndShowBiometricButton()
        {
            try
            {
                bool deviceSupports = await CrossFingerprint.Current.IsAvailableAsync();
                if (!deviceSupports) { btnBiometric.IsVisible = false; return; }

                string biometricEnabled = await SecureStorage.GetAsync("biometric_enabled") ?? "true";
                btnBiometric.IsVisible = biometricEnabled == "true";
            }
            catch
            {
                btnBiometric.IsVisible = false;
            }
        }

        private async void OnBiometricClicked(object sender, EventArgs e)
        {
            try
            {
                var request = new AuthenticationRequestConfiguration(
                    "Finder Authentication",
                    "Use fingerprint or face to access the app");

                var result = await CrossFingerprint.Current.AuthenticateAsync(request);

                if (result.Authenticated)
                {
                    await NavigateAfterSuccess();
                }
                else
                {
                    await DisplayAlert("Authentication Failed",
                        "Biometric authentication was not successful. Please use your passcode.",
                        "OK");
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Error", $"Biometric error: {ex.Message}", "OK");
            }
        }

        // ── ViewModel event handlers ───────────────────────────────────────

        private async void OnAuthenticationSucceeded(object sender, EventArgs e)
        {
            await NavigateAfterSuccess();
        }

        private async void OnShowMessage(object sender, string payload)
        {
            // Payload format: "Title|Body"
            string[] parts = payload.Split('|');
            string title = parts.Length > 1 ? parts[0] : "Notice";
            string body = parts.Length > 1 ? parts[1] : payload;
            await DisplayAlert(title, body, "OK");
        }

        private void OnShowError(object sender, string message)
        {
            lblError.Text = message;
            lblError.IsVisible = true;

            // Auto-hide error after 3 seconds
            Device.StartTimer(TimeSpan.FromSeconds(3), () =>
            {
                lblError.IsVisible = false;
                lblError.Text = string.Empty;
                return false; // Don't repeat
            });
        }

        // ── Navigation ─────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task NavigateAfterSuccess()
        {
            if (_isAppStartup)
            {
                // App startup auth — go to main page
                Application.Current.MainPage = App.CreateNavPage(new MainPage());
            }
            else
            {
                // Settings access auth — go to settings
                await Navigation.PushAsync(new SettingsPage());
            }
        }

        // ── Button event handlers ──────────────────────────────────────────

        private async void OnChangePasscodeClicked(object sender, EventArgs e)
        {
            // Ask for current passcode
            string current = await DisplayPromptAsync(
                "Change Passcode",
                "Enter your current passcode:",
                "Confirm", "Cancel",
                maxLength: 4,
                keyboard: Keyboard.Numeric);
            if (current == null) return;

            // Ask for new passcode
            string newCode = await DisplayPromptAsync(
                "New Passcode",
                "Enter a new 4-digit passcode:",
                "Next", "Cancel",
                maxLength: 4,
                keyboard: Keyboard.Numeric);
            if (newCode == null) return;

            // Confirm new passcode
            string confirm = await DisplayPromptAsync(
                "Confirm Passcode",
                "Re-enter your new passcode:",
                "Save", "Cancel",
                maxLength: 4,
                keyboard: Keyboard.Numeric);
            if (confirm == null) return;

            bool success = await _viewModel.ChangePasscodeAsync(current, newCode, confirm);
            if (success)
            {
                // Ask about biometrics
                bool deviceSupports = await CrossFingerprint.Current.IsAvailableAsync();
                if (deviceSupports)
                {
                    bool useBiometric = await DisplayAlert(
                        "Biometric Authentication",
                        "Would you like to enable fingerprint/face unlock?",
                        "Yes", "No");
                    await _viewModel.SetBiometricEnabledAsync(useBiometric);
                    btnBiometric.IsVisible = useBiometric;
                }

                await DisplayAlert("✓ Success", "Passcode changed successfully.", "OK");
            }
        }

        private void OnExitClicked(object sender, EventArgs e)
        {
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }

        protected override bool OnBackButtonPressed()
        {
            if (_isAppStartup)
                return false; // Allow system to handle (exit)

            Navigation.PopAsync();
            return true;
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _viewModel.AuthenticationSucceeded -= OnAuthenticationSucceeded;
            _viewModel.ShowMessage -= OnShowMessage;
            _viewModel.ShowError -= OnShowError;
        }
    }
}