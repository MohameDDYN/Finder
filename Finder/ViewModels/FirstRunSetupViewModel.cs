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
    /// ViewModel for the first-run setup wizard.
    /// Collects Telegram bot credentials and tracking interval.
    /// </summary>
    public class FirstRunSetupViewModel : BaseViewModel
    {
        private readonly string _settingsFilePath;

        // ── Events ──────────────────────────────────────────────────────────
        public event EventHandler SetupCompleted;
        public event EventHandler<string> ShowAlert;

        public FirstRunSetupViewModel()
        {
            Title = "Welcome to Finder";
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

        private string _interval = "60000";
        public string Interval
        {
            get => _interval;
            set => SetProperty(ref _interval, value);
        }

        // ── Commands ───────────────────────────────────────────────────────
        public ICommand SaveCommand { get; }

        // ── Command implementation ─────────────────────────────────────────

        private async Task ExecuteSaveAsync()
        {
            // Validate inputs
            if (string.IsNullOrWhiteSpace(BotToken))
            {
                ShowAlert?.Invoke(this, "Please enter your Telegram bot token.");
                return;
            }

            if (string.IsNullOrWhiteSpace(ChatId))
            {
                ShowAlert?.Invoke(this, "Please enter your Telegram chat ID.");
                return;
            }

            if (string.IsNullOrWhiteSpace(Interval) || !int.TryParse(Interval, out int intervalMs) || intervalMs < 1000)
            {
                ShowAlert?.Invoke(this, "Please enter a valid interval (minimum 1000 ms).");
                return;
            }

            try
            {
                IsBusy = true;
                ((Command)SaveCommand).ChangeCanExecute();

                string token = BotToken.Trim();
                string chatId = ChatId.Trim();
                string interval = intervalMs.ToString();

                // Save credentials to secure storage
                await SecureStorage.SetAsync("bot_token", token);
                await SecureStorage.SetAsync("chat_id", chatId);
                await SecureStorage.SetAsync("Interval", interval);
                await SecureStorage.SetAsync("setup_completed", "true");

                // Also persist to file so the background service can read it
                var settings = new AppSettings
                {
                    BotToken = token,
                    ChatId = chatId,
                    Interval = interval
                };
                File.WriteAllText(_settingsFilePath, JsonConvert.SerializeObject(settings));

                SetupCompleted?.Invoke(this, EventArgs.Empty);
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
    }
}