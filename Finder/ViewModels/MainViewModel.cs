using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Finder.Services;
using Xamarin.Essentials;
using Xamarin.Forms;

namespace Finder.ViewModels
{
    /// <summary>
    /// ViewModel for MainPage.
    /// Manages tracking service state, status polling, and
    /// in-app update progress display driven by MessagingCenter.
    ///
    /// Compatible with C# 7.3 (netstandard2.0 / Xamarin.Forms / VS2019).
    /// No switch expressions, no relational patterns, no range operators.
    /// </summary>
    public class MainViewModel : BaseViewModel
    {
        // ── Public constant — read by TelegramCommandHandler ──────────────────
        public const string PREF_AUTO_START = "auto_start_on_open";

        // ── Status polling interval ───────────────────────────────────────────
        private const int STATUS_POLL_INTERVAL_MS = 5000;

        // ── MessagingCenter message keys ──────────────────────────────────────
        // These keys MUST match exactly what TelegramCommandHandler.cs sends.
        public const string MSG_UPDATE_STARTED = "UpdateStarted";
        public const string MSG_UPDATE_PROGRESS = "UpdateProgress";
        public const string MSG_UPDATE_COMPLETE = "UpdateComplete";
        public const string MSG_UPDATE_FAILED = "UpdateFailed";
        public const string MSG_UPDATE_INSTALLING = "UpdateInstalling";

        // ── Private fields ────────────────────────────────────────────────────
        private readonly ILocationService _locationService;
        private CancellationTokenSource _pollingCts;

        // ── Events ────────────────────────────────────────────────────────────
        public event EventHandler RequestOpenSettings;
        public event EventHandler RequestViewHistory;
        public event EventHandler<string> ShowAlert;
        public event EventHandler<string> ShowSuccess;

        // ─────────────────────────────────────────────────────────────────────
        // Constructor
        // ─────────────────────────────────────────────────────────────────────

        public MainViewModel()
        {
            Title = "Finder";
            _locationService = DependencyService.Get<ILocationService>();

            StartServiceCommand = new Command(
                async () => await ExecuteStartService(),
                () => !IsServiceRunning && !IsBusy);

            // NOTE: StopServiceCommand intentionally does NOT check IsBusy.
            // The Stop button must remain enabled while Start is processing.
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

            // Subscribe to update progress messages from the Android layer
            SubscribeToUpdateMessages();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Tracking state properties
        // ─────────────────────────────────────────────────────────────────────

        private bool _isServiceRunning;
        public bool IsServiceRunning
        {
            get { return _isServiceRunning; }
            set
            {
                if (SetProperty(ref _isServiceRunning, value))
                {
                    StatusText = value
                        ? "● Tracking Active"
                        : "○ Tracking Stopped";

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
            get { return _statusText; }
            set { SetProperty(ref _statusText, value); }
        }

        private Color _statusColor = Color.FromHex("#546E7A");
        public Color StatusColor
        {
            get { return _statusColor; }
            set { SetProperty(ref _statusColor, value); }
        }

        private string _lastUpdateText = "No updates yet";
        public string LastUpdateText
        {
            get { return _lastUpdateText; }
            set { SetProperty(ref _lastUpdateText, value); }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Update progress properties
        // All bound to the Update Progress Card in MainPage.xaml.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// True while an /update command is being processed.
        /// Controls visibility of the entire Update Progress Card.
        /// </summary>
        private bool _isUpdating;
        public bool IsUpdating
        {
            get { return _isUpdating; }
            set { SetProperty(ref _isUpdating, value); }
        }

        /// <summary>
        /// True only during the download phase (progress 0–99%).
        /// Controls the ActivityIndicator spinner next to the card title.
        /// </summary>
        private bool _isDownloading;
        public bool IsDownloading
        {
            get { return _isDownloading; }
            set { SetProperty(ref _isDownloading, value); }
        }

        /// <summary>
        /// Integer 0–100 shown in the centre percentage label.
        /// </summary>
        private int _updateProgressPercent;
        public int UpdateProgressPercent
        {
            get { return _updateProgressPercent; }
            set
            {
                if (SetProperty(ref _updateProgressPercent, value))
                {
                    // Keep the ProgressBar fraction in sync (C# 7.3-safe)
                    UpdateProgressFraction = value / 100.0;
                }
            }
        }

        /// <summary>
        /// Double 0.0–1.0 bound directly to ProgressBar.Progress.
        /// Updated automatically when UpdateProgressPercent changes.
        /// </summary>
        private double _updateProgressFraction;
        public double UpdateProgressFraction
        {
            get { return _updateProgressFraction; }
            set { SetProperty(ref _updateProgressFraction, value); }
        }

        /// <summary>
        /// Primary status line inside the update card.
        /// e.g. "Downloading update… 45%" / "Installing…" / "✅ Done!"
        /// </summary>
        private string _updateStatusText = string.Empty;
        public string UpdateStatusText
        {
            get { return _updateStatusText; }
            set { SetProperty(ref _updateStatusText, value); }
        }

        /// <summary>
        /// Secondary detail line inside the update card.
        /// e.g. "v1.0.1 → v1.0.2" / "Tap Install on the device"
        /// </summary>
        private string _updateSubStatusText = string.Empty;
        public string UpdateSubStatusText
        {
            get { return _updateSubStatusText; }
            set { SetProperty(ref _updateSubStatusText, value); }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Commands
        // ─────────────────────────────────────────────────────────────────────

        public ICommand StartServiceCommand { get; }
        public ICommand StopServiceCommand { get; }
        public ICommand ShareLocationCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand ViewHistoryCommand { get; }
        public ICommand RefreshStatusCommand { get; }

        // ─────────────────────────────────────────────────────────────────────
        // Initialization
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Called every time MainPage appears (OnAppearing).
        /// Checks current service status and attempts auto-start if configured.
        /// </summary>
        public async Task InitializeAsync()
        {
            await CheckServiceStatus();
            await TryAutoStartAsync();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Status polling
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Starts a lightweight 5-second polling loop that reads the real service
        /// state and updates IsServiceRunning — keeps buttons in sync with remote
        /// Telegram /start and /stop commands while the page is visible.
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
                                    ? string.Format("Started remotely at {0:HH:mm:ss}",
                                        DateTime.Now)
                                    : string.Format("Stopped remotely at {0:HH:mm:ss}",
                                        DateTime.Now);
                            });
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        break; // Normal cancellation — exit cleanly
                    }
                    catch
                    {
                        // Swallow all other errors — polling must never crash
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

        // ─────────────────────────────────────────────────────────────────────
        // Update progress — MessagingCenter subscriptions
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Subscribes to all update-related messages sent by TelegramCommandHandler.
        ///
        /// Message flow:
        ///   UpdateStarted    → show card, reset to 0%
        ///   UpdateProgress   → update bar and percentage (payload = "45")
        ///   UpdateInstalling → show "Ready to install!" stage
        ///   UpdateComplete   → show "Done!" then auto-hide after 4 seconds
        ///   UpdateFailed     → show error message then auto-hide after 5 seconds
        /// </summary>
        private void SubscribeToUpdateMessages()
        {
            // ── Download started ──────────────────────────────────────────────
            MessagingCenter.Subscribe<object, string>(
                this, MSG_UPDATE_STARTED, (sender, versionInfo) =>
                {
                    Device.BeginInvokeOnMainThread(() =>
                    {
                        IsUpdating = true;
                        IsDownloading = true;
                        UpdateProgressPercent = 0;
                        UpdateProgressFraction = 0.0;
                        UpdateStatusText = "⬇️  Downloading update…";
                        UpdateSubStatusText = versionInfo; // e.g. "v1.0.1 → v1.0.2"
                    });
                });

            // ── Download progress tick ────────────────────────────────────────
            // Payload is the integer percent as a string: "0" … "100"
            MessagingCenter.Subscribe<object, string>(
                this, MSG_UPDATE_PROGRESS, (sender, progressStr) =>
                {
                    int pct;
                    if (!int.TryParse(progressStr, out pct)) return;

                    Device.BeginInvokeOnMainThread(() =>
                    {
                        UpdateProgressPercent = pct;
                        UpdateStatusText =
                            string.Format("⬇️  Downloading update… {0}%", pct);

                        // Plain if/else — C# 7.3 compatible
                        // (replaces the C# 9.0 switch expression with relational patterns)
                        if (pct == 0)
                            UpdateSubStatusText = "Connecting to server…";
                        else if (pct < 10)
                            UpdateSubStatusText = "Starting download…";
                        else if (pct < 50)
                            UpdateSubStatusText = "Downloading APK…";
                        else if (pct < 90)
                            UpdateSubStatusText = "Almost there…";
                        else if (pct == 100)
                            UpdateSubStatusText = "Download complete!";
                        // else: keep current sub-status text unchanged
                    });
                });

            // ── Install stage ─────────────────────────────────────────────────
            MessagingCenter.Subscribe<object, string>(
                this, MSG_UPDATE_INSTALLING, (sender, versionInfo) =>
                {
                    Device.BeginInvokeOnMainThread(() =>
                    {
                        IsDownloading = false; // Stop spinner
                        UpdateProgressPercent = 100;
                        UpdateStatusText = "📲  Ready to install!";
                        UpdateSubStatusText = "Tap Install on the device to complete";
                    });
                });

            // ── Update complete ───────────────────────────────────────────────
            MessagingCenter.Subscribe<object, string>(
                this, MSG_UPDATE_COMPLETE, (sender, message) =>
                {
                    Device.BeginInvokeOnMainThread(() =>
                    {
                        IsDownloading = false;
                        UpdateProgressPercent = 100;
                        UpdateStatusText = "✅  Update installed!";
                        UpdateSubStatusText = message;
                    });

                    // Auto-hide the card after 4 seconds
                    Task.Delay(4000).ContinueWith(t =>
                        Device.BeginInvokeOnMainThread(ResetUpdateCard));
                });

            // ── Update failed ─────────────────────────────────────────────────
            MessagingCenter.Subscribe<object, string>(
                this, MSG_UPDATE_FAILED, (sender, errorMessage) =>
                {
                    Device.BeginInvokeOnMainThread(() =>
                    {
                        IsDownloading = false;
                        UpdateStatusText = "❌  Update failed";
                        UpdateSubStatusText = errorMessage;
                    });

                    // Auto-hide the card after 5 seconds
                    Task.Delay(5000).ContinueWith(t =>
                        Device.BeginInvokeOnMainThread(ResetUpdateCard));
                });
        }

        /// <summary>
        /// Unsubscribes all MessagingCenter update listeners.
        /// Called from MainPage.OnDisappearing to prevent memory leaks.
        /// </summary>
        public void UnsubscribeUpdateMessages()
        {
            MessagingCenter.Unsubscribe<object, string>(this, MSG_UPDATE_STARTED);
            MessagingCenter.Unsubscribe<object, string>(this, MSG_UPDATE_PROGRESS);
            MessagingCenter.Unsubscribe<object, string>(this, MSG_UPDATE_INSTALLING);
            MessagingCenter.Unsubscribe<object, string>(this, MSG_UPDATE_COMPLETE);
            MessagingCenter.Unsubscribe<object, string>(this, MSG_UPDATE_FAILED);
        }

        /// <summary>
        /// Resets all update card properties to their hidden/empty defaults.
        /// Called automatically after complete/failed states time out.
        /// </summary>
        private void ResetUpdateCard()
        {
            IsUpdating = false;
            IsDownloading = false;
            UpdateProgressPercent = 0;
            UpdateProgressFraction = 0.0;
            UpdateStatusText = string.Empty;
            UpdateSubStatusText = string.Empty;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Service status helpers
        // ─────────────────────────────────────────────────────────────────────

        private async Task CheckServiceStatus()
        {
            try
            {
                IsBusy = true;
                bool running = await _locationService.IsTrackingActive();
                IsServiceRunning = running;
                LastUpdateText = string.Format("Last check: {0:HH:mm:ss}", DateTime.Now);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[MainViewModel] CheckServiceStatus error: " + ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task TryAutoStartAsync()
        {
            try
            {
                bool autoStart = Preferences.Get(PREF_AUTO_START, false);
                if (!autoStart || IsServiceRunning) return;

                string setupDone = await SecureStorage.GetAsync("setup_completed");
                if (string.IsNullOrEmpty(setupDone)) return;

                await ExecuteStartService();
            }
            catch
            {
                // Silent fail — auto-start is best-effort
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Command implementations
        // ─────────────────────────────────────────────────────────────────────

        private async Task ExecuteStartService()
        {
            try
            {
                IsBusy = true;
                ((Command)StartServiceCommand).ChangeCanExecute();
                ((Command)StopServiceCommand).ChangeCanExecute();

                await _locationService.StartTracking();

                IsServiceRunning = true;
                LastUpdateText = string.Format("Started at {0:HH:mm:ss}", DateTime.Now);

                MessagingCenter.Send<MainViewModel>(this, "ServiceStarted");
                ShowSuccess?.Invoke(this, "Location tracking started successfully.");
            }
            catch (Exception ex)
            {
                ShowAlert?.Invoke(this, "Failed to start tracking: " + ex.Message);
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
                LastUpdateText = string.Format("Stopped at {0:HH:mm:ss}", DateTime.Now);

                MessagingCenter.Send<MainViewModel>(this, "ServiceStopped");
                ShowSuccess?.Invoke(this, "Location tracking stopped.");
            }
            catch (Exception ex)
            {
                ShowAlert?.Invoke(this, "Failed to stop tracking: " + ex.Message);
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
                string mapsUrl = "https://www.google.com/maps?q=" + lat + "," + lon;

                await Share.RequestAsync(new ShareTextRequest
                {
                    Text = mapsUrl,
                    Title = "Share My Location"
                });
            }
            catch (Exception ex)
            {
                ShowAlert?.Invoke(this, "Share failed: " + ex.Message);
            }
            finally
            {
                IsBusy = false;
                ((Command)ShareLocationCommand).ChangeCanExecute();
            }
        }
    }
}