using System;
using System.Threading;
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
        public const string PREF_AUTO_START = "auto_start_on_open";

        private readonly ILocationService _locationService;

        // ── Status polling ─────────────────────────────────────────────────────
        // Polls the real service state every 5 seconds while the page is visible.
        // This keeps the Start/Stop buttons in sync even when the service is
        // started or stopped remotely via a Telegram command.
        private CancellationTokenSource _pollingCts;
        private const int STATUS_POLL_INTERVAL_MS = 5000; // 5 seconds

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
                () => IsServiceRunning && !IsBusy);

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
        /// Checks current service status and attempts auto-start if configured.
        /// </summary>
        public async Task InitializeAsync()
        {
            await CheckServiceStatus();
            await TryAutoStartAsync();
        }

        // ── Status polling ─────────────────────────────────────────────────────

        /// <summary>
        /// Starts a lightweight background loop that reads the real service state
        /// every 5 seconds and updates IsServiceRunning accordingly.
        ///
        /// This ensures the Start/Stop buttons stay in sync when the service is
        /// started or stopped remotely via a Telegram command (/start, /stop)
        /// without any user interaction inside the app.
        ///
        /// The loop reads only a SharedPreferences boolean — no GPS, no network,
        /// negligible battery impact. It stops immediately when the page disappears.
        /// </summary>
        public void StartStatusPolling()
        {
            // Cancel any existing poll loop before starting a new one
            StopStatusPolling();

            _pollingCts = new CancellationTokenSource();
            var token = _pollingCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(STATUS_POLL_INTERVAL_MS, token);

                        if (token.IsCancellationRequested) break;

                        // Read the real service state from SharedPreferences
                        bool running = await _locationService.IsTrackingActive();

                        // Only update the UI if the state has actually changed —
                        // avoids unnecessary property-change notifications
                        if (running != IsServiceRunning)
                        {
                            // Must update UI on the main thread
                            Device.BeginInvokeOnMainThread(() =>
                            {
                                IsServiceRunning = running;

                                LastUpdateText = running
                                    ? $"Started remotely at {DateTime.Now:HH:mm:ss}"
                                    : $"Stopped remotely at {DateTime.Now:HH:mm:ss}";
                            });
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        // Normal cancellation — exit the loop cleanly
                        break;
                    }
                    catch
                    {
                        // Swallow any other error — polling must never crash the app
                    }
                }
            }, token);
        }

        /// <summary>
        /// Stops the background status polling loop.
        /// Safe to call multiple times or when already stopped.
        /// </summary>
        public void StopStatusPolling()
        {
            try
            {
                _pollingCts?.Cancel();
                _pollingCts?.Dispose();
            }
            catch { }
            finally
            {
                _pollingCts = null;
            }
        }

        // ── Auto-start ─────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the auto-start flag (set remotely via /autostart on|off) and
        /// starts the service automatically if conditions are met.
        /// </summary>
        private async Task TryAutoStartAsync()
        {
            bool autoStart = Preferences.Get(PREF_AUTO_START, false);

            if (!autoStart || IsServiceRunning || IsBusy)
                return;

            LastUpdateText = "⚙ Auto-starting service…";

            await ExecuteStartService();

            if (IsServiceRunning)
                LastUpdateText = $"Auto-started at {DateTime.Now:HH:mm:ss}";
        }

        // ── Status check ───────────────────────────────────────────────────────

        /// <summary>
        /// Reads the real service state once and updates the UI immediately.
        /// Called on page appear and by RefreshStatusCommand.
        /// </summary>
        public async Task CheckServiceStatus()
        {
            try
            {
                IsBusy = true;
                ((Command)StartServiceCommand).ChangeCanExecute();

                bool running = await _locationService.IsTrackingActive();
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

                // Notify MainActivity to stop the app-level Telegram handler —
                // the background service now owns polling.
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

                // Notify MainActivity to start the app-level Telegram handler —
                // the service is gone so the app must handle polling now.
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