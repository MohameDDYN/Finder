using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using Finder.Models;
using Newtonsoft.Json;
using Xamarin.Essentials;
using Xamarin.Forms;

namespace Finder.ViewModels
{
    /// <summary>
    /// ViewModel for SettingsPage — loads and saves all app configuration.
    /// </summary>
    public class SettingsViewModel : BaseViewModel
    {
        private readonly string _settingsFilePath;

        // ── Events ──────────────────────────────────────────────────────────
        public event EventHandler SettingsSaved;
        public event EventHandler<string> ShowAlert;
        public event EventHandler<string> ShowSuccess;

        public SettingsViewModel()
        {
            Title = "Settings";
            _settingsFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                "secure_settings.json");

            SaveCommand = new Command(async () => await ExecuteSaveAsync(), () => !IsBusy);
        }

        // ── Bindable properties ────────────────────────────────────────────

        private string _botToken;
        public string BotToken
        {
            get => _botToken;
            set => SetProperty(ref _botToken, value);
        }

        private string _chatId;
        public string ChatId
        {
            get => _chatId;
            set => SetProperty(ref _chatId, value);
        }

        private string _interval;
        public string Interval
        {
            get => _interval;
            set => SetProperty(ref _interval, value);
        }

        private bool _isPasscodeAtStartup = true;
        public bool IsPasscodeAtStartup
        {
            get => _isPasscodeAtStartup;
            set
            {
                if (SetProperty(ref _isPasscodeAtStartup, value))
                    _ = SavePasscodeStartupSettingAsync(value);
            }
        }

        private bool _isBiometricEnabled;
        public bool IsBiometricEnabled
        {
            get => _isBiometricEnabled;
            set
            {
                if (SetProperty(ref _isBiometricEnabled, value))
                    _ = SaveBiometricSettingAsync(value);
            }
        }

        private bool _isBiometricAvailable;
        public bool IsBiometricAvailable
        {
            get => _isBiometricAvailable;
            set => SetProperty(ref _isBiometricAvailable, value);
        }

        // Token display (masked)
        private string _maskedToken = "Not configured";
        public string MaskedToken
        {
            get => _maskedToken;
            set => SetProperty(ref _maskedToken, value);
        }

        // ── Commands ───────────────────────────────────────────────────────
        public ICommand SaveCommand { get; }

        // ── Initialization ─────────────────────────────────────────────────

        public async Task LoadSettingsAsync(bool biometricAvailable)
        {
            try
            {
                IsBusy = true;
                IsBiometricAvailable = biometricAvailable;

                string token = await SecureStorage.GetAsync("bot_token") ?? string.Empty;
                string chatId = await SecureStorage.GetAsync("chat_id") ?? string.Empty;
                string interval = await SecureStorage.GetAsync("Interval") ?? "60000";
                string passcodeAtStartup = await SecureStorage.GetAsync("passcode_at_startup") ?? "true";
                string biometricEnabled = await SecureStorage.GetAsync("biometric_enabled") ?? "false";

                // Fall back to file if SecureStorage is empty
                if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chatId))
                {
                    if (File.Exists(_settingsFilePath))
                    {
                        var fileSettings = JsonConvert.DeserializeObject<AppSettings>(
                            File.ReadAllText(_settingsFilePath));

                        if (fileSettings != null)
                        {
                            token = string.IsNullOrEmpty(token) ? fileSettings.BotToken : token;
                            chatId = string.IsNullOrEmpty(chatId) ? fileSettings.ChatId : chatId;
                            interval = string.IsNullOrEmpty(interval) || interval == "60000"
                                ? fileSettings.Interval : interval;
                        }
                    }
                }

                BotToken = token;
                ChatId = chatId;
                Interval = interval;
                MaskedToken = MaskTokenDisplay(token);

                _isPasscodeAtStartup = passcodeAtStartup == "true";
                _isBiometricEnabled = biometricEnabled == "true";

                // Raise property changes without triggering save
                OnPropertyChanged(nameof(IsPasscodeAtStartup));
                OnPropertyChanged(nameof(IsBiometricEnabled));
            }
            catch (Exception ex)
            {
                ShowAlert?.Invoke(this, $"Failed to load settings: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ── Command implementations ────────────────────────────────────────

        private async Task ExecuteSaveAsync()
        {
            if (string.IsNullOrWhiteSpace(BotToken) || string.IsNullOrWhiteSpace(ChatId))
            {
                ShowAlert?.Invoke(this, "Bot token and Chat ID are required.");
                return;
            }

            if (!int.TryParse(Interval, out int intervalMs) || intervalMs < 1000)
            {
                ShowAlert?.Invoke(this, "Interval must be a number ≥ 1000 milliseconds.");
                return;
            }

            try
            {
                IsBusy = true;
                ((Command)SaveCommand).ChangeCanExecute();

                string token = BotToken.Trim();
                string chatId = ChatId.Trim();
                string iv = intervalMs.ToString();

                await SecureStorage.SetAsync("bot_token", token);
                await SecureStorage.SetAsync("chat_id", chatId);
                await SecureStorage.SetAsync("Interval", iv);

                // Mark setup complete (or reset if credentials are cleared)
                if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(chatId))
                    await SecureStorage.SetAsync("setup_completed", "true");

                // Persist to file for background service
                var settings = new AppSettings
                {
                    BotToken = token,
                    ChatId = chatId,
                    Interval = iv
                };
                File.WriteAllText(_settingsFilePath, JsonConvert.SerializeObject(settings));

                MaskedToken = MaskTokenDisplay(token);
                ShowSuccess?.Invoke(this, "Settings saved successfully.");
                SettingsSaved?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                ShowAlert?.Invoke(this, $"Failed to save settings: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
                ((Command)SaveCommand).ChangeCanExecute();
            }
        }

        private async Task SavePasscodeStartupSettingAsync(bool enabled)
        {
            try
            {
                await SecureStorage.SetAsync("passcode_at_startup", enabled ? "true" : "false");
            }
            catch (Exception ex)
            {
                ShowAlert?.Invoke(this, $"Failed to save security setting: {ex.Message}");
            }
        }

        private async Task SaveBiometricSettingAsync(bool enabled)
        {
            try
            {
                await SecureStorage.SetAsync("biometric_enabled", enabled ? "true" : "false");
            }
            catch (Exception ex)
            {
                ShowAlert?.Invoke(this, $"Failed to save biometric setting: {ex.Message}");
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private string MaskTokenDisplay(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length < 8) return "Not configured";
            return token.Substring(0, 6) + "•••" + token.Substring(token.Length - 4);
        }
    }
}