using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Finder.Services;
using Xamarin.Essentials;
using Xamarin.Forms;

namespace Finder.ViewModels
{
    public class MainViewModel : BaseViewModel
    {
        // ── SharedPreferences key (via Xamarin.Essentials.Preferences) ────────
        // Written by TelegramCommandHandler (/autostart on|off).
        // Read here on every app open to decide whether to auto-start the service.
        public const string PREF_AUTO_START = "auto_start_on_open";

        private readonly ILocationService _locationService;

        // ── Events ─────────────────────────────────────────────────────────────
        public event EventHandler RequestOpenSettings;
        public event EventHandler RequestViewHistory;
        public event EventHandler<string> ShowAlert;
        public event EventHandler<string> ShowSuccess;

        public MainViewModel()
        {
            Title = "Finder";
            _locationService = DependencyService.Get<ILocationService>();

            StartServiceCommand = new Command(
                async () => await ExecuteStartService(),
                () => !IsServiceRunning && !IsBusy);

            StopServiceCommand = new Command(
                async () => await ExecuteStopService(),
                () => IsServiceRunning);

            ShareLocationCommand = new Command(
                async () => await ExecuteShareLocation(),
                () => !IsBusy);

            OpenSettingsCommand = new Command(
                () => RequestOpenSettings?.Invoke(this, EventArgs.Empty));

            ViewHistoryCommand = new Command(
                () => RequestViewHistory?.Invoke(this, EventArgs.Empty));

            RefreshStatusCommand = new Command(
                async () => await CheckServiceStatus());
        }

        // ── Bindable properties ────────────────────────────────────────────────

        private bool _isServiceRunning;
        public bool IsServiceRunning
        {
            get => _isServiceRunning;
            set
            {
                if (SetProperty(ref _isServiceRunning, value))
                {
                    StatusText = value ? "● Tracking Active" : "○ Tracking Stopped";
                    StatusColor = value
                        ? Color.FromHex("#43A047")
                        : Color.FromHex("#E53935");

                    ((Command)StartServiceCommand).ChangeCanExecute();
                    ((Command)StopServiceCommand).ChangeCanExecute();
                }
            }
        }

        private string _statusText = "○ Checking status…";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        private Color _statusColor = Color.FromHex("#546E7A");
        public Color StatusColor
        {
            get => _statusColor;
            set => SetProperty(ref _statusColor, value);
        }

        private string _lastUpdateText = "No updates yet";
        public string LastUpdateText
        {
            get => _lastUpdateText;
            set => SetProperty(ref _lastUpdateText, value);
        }

        // ── Commands ───────────────────────────────────────────────────────────

        public ICommand StartServiceCommand { get; }
        public ICommand StopServiceCommand { get; }
        public ICommand ShareLocationCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand ViewHistoryCommand { get; }
        public ICommand RefreshStatusCommand { get; }

        // ── Initialization ─────────────────────────────────────────────────────

        /// <summary>
        /// Called every time MainPage appears (OnAppearing).
        ///
        /// Flow:
        ///   1. Check whether the service is currently running.
        ///   2. If the service is NOT running and auto-start is enabled,
        ///      start the service automatically — no user tap required.
        ///      The user is informed via the LastUpdateText label so the
        ///      behaviour is transparent, not silent.
        /// </summary>
        public async Task InitializeAsync()
        {
            await CheckServiceStatus();
            await TryAutoStartAsync();
        }

        /// <summary>
        /// Reads the auto-start flag (set remotely via /autostart on|off) and
        /// starts the service if the conditions are met.
        ///
        /// Conditions for auto-start:
        ///   • auto_start_on_open == true   (set via Telegram command)
        ///   • Service is not already running
        ///   • App is not currently busy with another operation
        /// </summary>
        private async Task TryAutoStartAsync()
        {
            // Read the flag written by TelegramCommandHandler via the same key
            bool autoStart = Preferences.Get(PREF_AUTO_START, false);

            if (!autoStart || IsServiceRunning || IsBusy)
                return;

            // Inform the user transparently — auto-start is never silent
            LastUpdateText = "⚙ Auto-starting service…";

            await ExecuteStartService();

            // If start succeeded, reflect it in the label
            if (IsServiceRunning)
                LastUpdateText = $"Auto-started at {DateTime.Now:HH:mm:ss}";
        }

        // ── Status check ───────────────────────────────────────────────────────

        public async Task CheckServiceStatus()
        {
            try
            {
                bool running = await _locationService.IsTrackingActive();

                IsBusy = true;
                ((Command)StartServiceCommand).ChangeCanExecute();

                IsServiceRunning = running;
                LastUpdateText = $"Last checked: {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                ShowAlert?.Invoke(this, $"Could not check service status: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
                ((Command)StartServiceCommand).ChangeCanExecute();
                ((Command)ShareLocationCommand).ChangeCanExecute();
            }
        }

        // ── Command implementations ────────────────────────────────────────────

        private async Task ExecuteStartService()
        {
            try
            {
                IsBusy = true;
                ((Command)StartServiceCommand).ChangeCanExecute();
                ((Command)ShareLocationCommand).ChangeCanExecute();

                await _locationService.StartTracking();

                IsServiceRunning = true;
                LastUpdateText = $"Started at {DateTime.Now:HH:mm:ss}";

                // Notify MainActivity to stop the app-level handler —
                // the background service now owns Telegram polling.
                MessagingCenter.Send<MainViewModel>(this, "ServiceStarted");

                ShowSuccess?.Invoke(this, "Location tracking started successfully.");
            }
            catch (Exception ex)
            {
                ShowAlert?.Invoke(this, $"Failed to start tracking: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
                ((Command)StartServiceCommand).ChangeCanExecute();
                ((Command)ShareLocationCommand).ChangeCanExecute();
            }
        }

        private async Task ExecuteStopService()
        {
            try
            {
                await _locationService.StopTracking();

                IsServiceRunning = false;
                LastUpdateText = $"Stopped at {DateTime.Now:HH:mm:ss}";

                // Notify MainActivity to start the app-level handler —
                // the service is gone so the app must handle Telegram polling.
                MessagingCenter.Send<MainViewModel>(this, "ServiceStopped");

                ShowSuccess?.Invoke(this, "Location tracking stopped.");
            }
            catch (Exception ex)
            {
                ShowAlert?.Invoke(this, $"Failed to stop tracking: {ex.Message}");
            }
        }

        private async Task ExecuteShareLocation()
        {
            try
            {
                IsBusy = true;
                ((Command)ShareLocationCommand).ChangeCanExecute();

                var location = await _locationService.GetCurrentLocation();

                if (location == null)
                {
                    ShowAlert?.Invoke(this,
                        "Could not retrieve current location. Make sure GPS is enabled.");
                    return;
                }

                string lat = location.Latitude.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                string lon = location.Longitude.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                string mapsUrl = $"https://www.google.com/maps?q={lat},{lon}";

                await Share.RequestAsync(new ShareTextRequest
                {
                    Text = mapsUrl,
                    Title = "Share My Location"
                });
            }
            catch (Exception ex)
            {
                ShowAlert?.Invoke(this, $"Share failed: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
                ((Command)ShareLocationCommand).ChangeCanExecute();
            }
        }
    }
}