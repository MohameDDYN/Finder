using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Finder.Services;
using Xamarin.Essentials;
using Xamarin.Forms;

namespace Finder.ViewModels
{
    /// <summary>
    /// ViewModel for MainPage — manages tracking state and location sharing.
    /// </summary>
    public class MainViewModel : BaseViewModel
    {
        private readonly ILocationService _locationService;

        // ── Events raised to the View for navigation ───────────────────────
        public event EventHandler RequestOpenSettings;
        public event EventHandler RequestViewHistory;
        public event EventHandler<string> ShowAlert;
        public event EventHandler<string> ShowSuccess;

        public MainViewModel()
        {
            Title = "Finder";
            _locationService = DependencyService.Get<ILocationService>();

            // ── FIX: Command CanExecute rules ──────────────────────────────
            //
            // StartServiceCommand: disabled while service is running OR busy.
            //
            // StopServiceCommand:  disabled ONLY when service is NOT running.
            //                      IsBusy is intentionally NOT checked here.
            //
            //   Root cause of the bug:
            //   The XAML had both Command="{Binding StopServiceCommand}" AND
            //   IsEnabled="{Binding IsServiceRunning}" on the Stop button.
            //   In Xamarin.Forms 5, Command.CanExecuteChanged calls
            //   Button.UpdateIsEnabled() which directly overwrites IsEnabled,
            //   bypassing the XAML binding. Because CanExecute included !IsBusy,
            //   any moment IsBusy=true (e.g. during CheckServiceStatus on
            //   OnAppearing) caused CanExecute=false, setting IsEnabled=false.
            //   The XAML binding would try to restore it to true, but the two
            //   mechanisms raced — and the CanExecute path often won, leaving
            //   the Stop button permanently disabled.
            //
            //   Fix: Remove IsEnabled binding from XAML (MainPage.xaml).
            //   Let Command.CanExecute be the single source of truth for
            //   button state. Remove !IsBusy from StopServiceCommand so that
            //   a user can always stop tracking regardless of busy state.
            // ──────────────────────────────────────────────────────────────

            StartServiceCommand = new Command(
                async () => await ExecuteStartService(),
                () => !IsServiceRunning && !IsBusy);

            StopServiceCommand = new Command(
                async () => await ExecuteStopService(),
                () => IsServiceRunning);          // <── IsBusy removed intentionally

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

        // ── Bindable properties ────────────────────────────────────────────

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
                        ? Color.FromHex("#43A047")   // green
                        : Color.FromHex("#E53935");   // red

                    // Refresh both commands whenever service state changes.
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

        // ── Commands ───────────────────────────────────────────────────────

        public ICommand StartServiceCommand { get; }
        public ICommand StopServiceCommand { get; }
        public ICommand ShareLocationCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand ViewHistoryCommand { get; }
        public ICommand RefreshStatusCommand { get; }

        // ── Initialization ─────────────────────────────────────────────────

        public async Task InitializeAsync()
        {
            await CheckServiceStatus();
        }

        /// <summary>
        /// Reads the service running state from the Android SharedPreferences
        /// (via ILocationService) and updates IsServiceRunning accordingly.
        /// </summary>
        public async Task CheckServiceStatus()
        {
            try
            {
                // FIX: Only set IsBusy for the spinner/ShareLocation command.
                // Start/Stop button state is driven by IsServiceRunning alone,
                // so we update that first — before toggling IsBusy — so the
                // buttons reflect the correct state immediately.
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

        // ── Command implementations ────────────────────────────────────────

        private async Task ExecuteStartService()
        {
            try
            {
                IsBusy = true;
                ((Command)StartServiceCommand).ChangeCanExecute();
                ((Command)ShareLocationCommand).ChangeCanExecute();

                await _locationService.StartTracking();

                // Set IsServiceRunning = true AFTER StartTracking() returns
                // so the preference is already written before CanExecute is evaluated.
                IsServiceRunning = true;
                LastUpdateText = $"Started at {DateTime.Now:HH:mm:ss}";

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
                // No IsBusy guard here — we want stop to always respond quickly.
                await _locationService.StopTracking();

                IsServiceRunning = false;
                LastUpdateText = $"Stopped at {DateTime.Now:HH:mm:ss}";

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

                // Culture-safe decimal separator for coordinates
                string lat = location.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string lon = location.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
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