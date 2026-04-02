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
        // Keeps Start/Stop buttons in sync when service changes via Telegram command.
        private CancellationTokenSource _pollingCts;
        private const int STATUS_POLL_INTERVAL_MS = 5000;

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

            // NOTE: StopServiceCommand intentionally does NOT check IsBusy.
            // The Stop button must remain enabled while Start is processing
            // so the user can cancel a stuck start. IsBusy only gates Start.
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

                    // Always notify both buttons when running state changes
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

        public async Task InitializeAsync()
        {
            await CheckServiceStatus();
            await TryAutoStartAsync();
        }

        // ── Status polling ─────────────────────────────────────────────────────

        /// <summary>
        /// Starts a 5-second poll loop that reads the real service state and
        /// updates buttons — keeps UI in sync with remote Telegram commands.
        /// Runs only while the page is visible. Zero battery cost — reads only
        /// a single SharedPreferences boolean.
        /// </summary>
        public void StartStatusPolling()
        {
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

                        bool running = await _locationService.IsTrackingActive();

                        if (running != IsServiceRunning)
                        {
                            Device.BeginInvokeOnMainThread(() =>
                            {
                                IsServiceRunning = running;
                                LastUpdateText = running
                                    ? $"Started remotely at {DateTime.Now:HH:mm:ss}"
                                    : $"Stopped remotely at {DateTime.Now:HH:mm:ss}";
                            });
                        }
                    }
                    catch (TaskCanceledException) { break; }
                    catch { /* never crash the app */ }
                }
            }, token);
        }

        /// <summary>Stops the polling loop. Safe to call when already stopped.</summary>
        public void StopStatusPolling()
        {
            try
            {
                _pollingCts?.Cancel();
                _pollingCts?.Dispose();
            }
            catch { }
            finally { _pollingCts = null; }
        }

        // ── Auto-start ─────────────────────────────────────────────────────────

        private async Task TryAutoStartAsync()
        {
            bool autoStart = Preferences.Get(PREF_AUTO_START, false);
            if (!autoStart || IsServiceRunning || IsBusy) return;

            LastUpdateText = "⚙ Auto-starting service…";
            await ExecuteStartService();

            if (IsServiceRunning)
                LastUpdateText = $"Auto-started at {DateTime.Now:HH:mm:ss}";
        }

        // ── Status check ───────────────────────────────────────────────────────

        public async Task CheckServiceStatus()
        {
            try
            {
                IsBusy = true;
                ((Command)StartServiceCommand).ChangeCanExecute();
                ((Command)StopServiceCommand).ChangeCanExecute();
                ((Command)ShareLocationCommand).ChangeCanExecute();

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
                // Notify ALL buttons — IsBusy changed, re-evaluate everything
                ((Command)StartServiceCommand).ChangeCanExecute();
                ((Command)StopServiceCommand).ChangeCanExecute();
                ((Command)ShareLocationCommand).ChangeCanExecute();
            }
        }

        // ── Command implementations ────────────────────────────────────────────

        private async Task ExecuteStartService()
        {
            try
            {
                IsBusy = true;
                // Notify Start (now disabled) and Share
                ((Command)StartServiceCommand).ChangeCanExecute();
                ((Command)ShareLocationCommand).ChangeCanExecute();
                // NOTE: do NOT notify StopServiceCommand here —
                // its predicate no longer checks IsBusy, so it stays
                // enabled correctly based on IsServiceRunning only.

                await _locationService.StartTracking();

                IsServiceRunning = true; // ← this notifies both Start AND Stop via the setter
                LastUpdateText = $"Started at {DateTime.Now:HH:mm:ss}";

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
                // FIX: notify ALL buttons in the finally block
                ((Command)StartServiceCommand).ChangeCanExecute();
                ((Command)StopServiceCommand).ChangeCanExecute();
                ((Command)ShareLocationCommand).ChangeCanExecute();
            }
        }

        private async Task ExecuteStopService()
        {
            try
            {
                await _locationService.StopTracking();

                IsServiceRunning = false; // ← notifies both Start AND Stop via the setter
                LastUpdateText = $"Stopped at {DateTime.Now:HH:mm:ss}";

                MessagingCenter.Send<MainViewModel>(this, "ServiceStopped");
                ShowSuccess?.Invoke(this, "Location tracking stopped.");
            }
            catch (Exception ex)
            {
                ShowAlert?.Invoke(this, $"Failed to stop tracking: {ex.Message}");
            }
            finally
            {
                // Safety net — ensure buttons are correct even on exception
                ((Command)StartServiceCommand).ChangeCanExecute();
                ((Command)StopServiceCommand).ChangeCanExecute();
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