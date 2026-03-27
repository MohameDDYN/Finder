using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Xamarin.Essentials;
using Xamarin.Forms;

namespace Finder.ViewModels
{
    /// <summary>
    /// ViewModel for PasscodePage — handles digit input, verification,
    /// lockout logic, and passcode change flow.
    /// </summary>
    public class PasscodeViewModel : BaseViewModel
    {
        // ── Constants ──────────────────────────────────────────────────────
        private const string DefaultPasscode = "1234";
        private const string PasscodeKey = "settings_passcode";
        private const string BiometricKey = "biometric_enabled";
        private const string LockoutUntilKey = "lockout_until";
        private const string FailedAttemptsKey = "failed_attempts";
        private const int MaxAttempts = 5;
        private const int LockoutMinutes = 5;

        // ── Events raised to the View ──────────────────────────────────────
        public event EventHandler AuthenticationSucceeded;
        public event EventHandler<string> ShowMessage;    // title|body
        public event EventHandler<string> ShowError;

        // ── Internal state ─────────────────────────────────────────────────
        private string _enteredPasscode = string.Empty;
        private int _failedAttempts;
        private DateTime? _lockoutUntil;

        public bool IsAppStartup { get; set; }

        public PasscodeViewModel()
        {
            DigitCommand = new Command<string>(OnDigitPressed);
            DeleteCommand = new Command(OnDeletePressed);
        }

        // ── Bindable properties ────────────────────────────────────────────

        // Dot colors reflect the number of digits entered
        private Color _dot1Color = Color.FromHex("#CFD8DC");
        public Color Dot1Color { get => _dot1Color; private set => SetProperty(ref _dot1Color, value); }

        private Color _dot2Color = Color.FromHex("#CFD8DC");
        public Color Dot2Color { get => _dot2Color; private set => SetProperty(ref _dot2Color, value); }

        private Color _dot3Color = Color.FromHex("#CFD8DC");
        public Color Dot3Color { get => _dot3Color; private set => SetProperty(ref _dot3Color, value); }

        private Color _dot4Color = Color.FromHex("#CFD8DC");
        public Color Dot4Color { get => _dot4Color; private set => SetProperty(ref _dot4Color, value); }

        private bool _isLockedOut;
        public bool IsLockedOut
        {
            get => _isLockedOut;
            set => SetProperty(ref _isLockedOut, value);
        }

        private string _lockoutMessage;
        public string LockoutMessage
        {
            get => _lockoutMessage;
            set
            {
                SetProperty(ref _lockoutMessage, value);
                OnPropertyChanged(nameof(HasLockoutMessage));
            }
        }
        public bool HasLockoutMessage => !string.IsNullOrEmpty(LockoutMessage);

        // ── Commands ───────────────────────────────────────────────────────
        public ICommand DigitCommand { get; }
        public ICommand DeleteCommand { get; }

        // ── Initialization ─────────────────────────────────────────────────

        public async Task LoadStateAsync()
        {
            await LoadLockoutStateAsync();
            CheckLockoutStatus();
        }

        private async Task LoadLockoutStateAsync()
        {
            try
            {
                string lockoutStr = await SecureStorage.GetAsync(LockoutUntilKey);
                if (!string.IsNullOrEmpty(lockoutStr) && DateTime.TryParse(lockoutStr, out DateTime lockoutTime))
                    _lockoutUntil = lockoutTime;

                string attemptsStr = await SecureStorage.GetAsync(FailedAttemptsKey);
                if (!string.IsNullOrEmpty(attemptsStr) && int.TryParse(attemptsStr, out int attempts))
                    _failedAttempts = attempts;
            }
            catch
            {
                _lockoutUntil = null;
                _failedAttempts = 0;
            }
        }

        public void CheckLockoutStatus()
        {
            if (_lockoutUntil.HasValue && DateTime.Now < _lockoutUntil.Value)
            {
                int remaining = (int)(_lockoutUntil.Value - DateTime.Now).TotalMinutes + 1;
                LockoutMessage = $"Too many failed attempts.\nTry again in {remaining} minute(s).";
                IsLockedOut = true;
            }
            else if (_lockoutUntil.HasValue)
            {
                // Lockout expired — reset
                _lockoutUntil = null;
                _failedAttempts = 0;
                LockoutMessage = null;
                IsLockedOut = false;
            }
        }

        // ── Digit input ────────────────────────────────────────────────────

        private async void OnDigitPressed(string digit)
        {
            if (_enteredPasscode.Length >= 4 || IsLockedOut) return;

            _enteredPasscode += digit;
            UpdateDotColors();

            if (_enteredPasscode.Length == 4)
            {
                // Short delay for visual feedback before verification
                await Task.Delay(150);
                await VerifyPasscodeAsync();
            }
        }

        private void OnDeletePressed()
        {
            if (_enteredPasscode.Length > 0)
            {
                _enteredPasscode = _enteredPasscode.Substring(0, _enteredPasscode.Length - 1);
                UpdateDotColors();
            }
        }

        private void UpdateDotColors()
        {
            var active = Color.FromHex("#43A047");
            var inactive = Color.FromHex("#CFD8DC");

            Dot1Color = _enteredPasscode.Length >= 1 ? active : inactive;
            Dot2Color = _enteredPasscode.Length >= 2 ? active : inactive;
            Dot3Color = _enteredPasscode.Length >= 3 ? active : inactive;
            Dot4Color = _enteredPasscode.Length >= 4 ? active : inactive;
        }

        private void ResetInput()
        {
            _enteredPasscode = string.Empty;
            UpdateDotColors();
        }

        // ── Verification ───────────────────────────────────────────────────

        private async Task VerifyPasscodeAsync()
        {
            try
            {
                if (_lockoutUntil.HasValue && DateTime.Now < _lockoutUntil.Value)
                {
                    ResetInput();
                    return;
                }

                string savedPasscode = await SecureStorage.GetAsync(PasscodeKey);
                if (string.IsNullOrEmpty(savedPasscode))
                    savedPasscode = DefaultPasscode;

                if (_enteredPasscode == savedPasscode)
                {
                    // ✅ Correct — reset counters
                    _failedAttempts = 0;
                    LockoutMessage = null;
                    IsLockedOut = false;
                    await SaveLockoutStateAsync();
                    ResetInput();
                    AuthenticationSucceeded?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    // ❌ Wrong passcode
                    _failedAttempts++;

                    if (_failedAttempts >= MaxAttempts)
                    {
                        _lockoutUntil = DateTime.Now.AddMinutes(LockoutMinutes);
                        await SaveLockoutStateAsync();
                        LockoutMessage = $"Too many failed attempts.\nTry again in {LockoutMinutes} minute(s).";
                        IsLockedOut = true;
                        ShowMessage?.Invoke(this, $"Locked Out|Too many failed attempts. Please try again in {LockoutMinutes} minutes.");
                    }
                    else
                    {
                        int remaining = MaxAttempts - _failedAttempts;
                        await SaveLockoutStateAsync();
                        ShowError?.Invoke(this, $"Incorrect passcode. {remaining} attempt(s) remaining.");
                    }

                    ResetInput();
                }
            }
            catch (Exception ex)
            {
                ShowError?.Invoke(this, $"Authentication error: {ex.Message}");
                ResetInput();
            }
        }

        private async Task SaveLockoutStateAsync()
        {
            try
            {
                if (_lockoutUntil.HasValue)
                    await SecureStorage.SetAsync(LockoutUntilKey, _lockoutUntil.Value.ToString("o"));
                else
                    SecureStorage.Remove(LockoutUntilKey);

                await SecureStorage.SetAsync(FailedAttemptsKey, _failedAttempts.ToString());
            }
            catch { /* Silent fail */ }
        }

        // ── Passcode change flow (called from View) ────────────────────────

        public async Task<bool> ChangePasscodeAsync(string currentInput, string newPasscode, string confirmPasscode)
        {
            string savedPasscode = await SecureStorage.GetAsync(PasscodeKey) ?? DefaultPasscode;

            if (currentInput != savedPasscode)
            {
                ShowError?.Invoke(this, "Incorrect current passcode.");
                return false;
            }

            if (newPasscode.Length != 4)
            {
                ShowError?.Invoke(this, "New passcode must be exactly 4 digits.");
                return false;
            }

            if (newPasscode != confirmPasscode)
            {
                ShowError?.Invoke(this, "Passcodes do not match.");
                return false;
            }

            await SecureStorage.SetAsync(PasscodeKey, newPasscode);
            return true;
        }

        public async Task SetBiometricEnabledAsync(bool enabled)
        {
            await SecureStorage.SetAsync(BiometricKey, enabled ? "true" : "false");
        }
    }
}