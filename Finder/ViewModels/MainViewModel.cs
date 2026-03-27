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

            // Wire up commands
            StartServiceCommand = new Command(async () => await ExecuteStartService(), () => !IsServiceRunning && !IsBusy);
            StopServiceCommand = new Command(async () => await ExecuteStopService(), () => IsServiceRunning && !IsBusy);
            ShareLocationCommand = new Command(async () => await ExecuteShareLocation(), () => !IsBusy);
            OpenSettingsCommand = new Command(() => RequestOpenSettings?.Invoke(this, EventArgs.Empty));
            ViewHistoryCommand = new Command(() => RequestViewHistory?.Invoke(this, EventArgs.Empty));
            RefreshStatusCommand = new Command(async () => await CheckServiceStatus());
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
                    StatusColor = value ? Color.FromHex("#43A047") : Color.FromHex("#E53935");
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

        public async Task CheckServiceStatus()
        {
            try
            {
                IsBusy = true;
                IsServiceRunning = await _locationService.IsTrackingActive();
                LastUpdateText = $"Last checked: {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                ShowAlert?.Invoke(this, $"Could not check service status: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ── Command implementations ────────────────────────────────────────

        private async Task ExecuteStartService()
        {
            try
            {
                IsBusy = true;
                await _locationService.StartTracking();
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
            }
        }

        private async Task ExecuteStopService()
        {
            try
            {
                IsBusy = true;
                await _locationService.StopTracking();
                IsServiceRunning = false;
                LastUpdateText = $"Stopped at {DateTime.Now:HH:mm:ss}";
                ShowSuccess?.Invoke(this, "Location tracking stopped.");
            }
            catch (Exception ex)
            {
                ShowAlert?.Invoke(this, $"Failed to stop tracking: {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ExecuteShareLocation()
        {
            try
            {
                IsBusy = true;
                var location = await _locationService.GetCurrentLocation();

                if (location == null)
                {
                    ShowAlert?.Invoke(this, "Could not retrieve current location. Make sure GPS is enabled.");
                    return;
                }

                // Format coordinates ensuring '.' as decimal separator (culture-safe)
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
            }
        }
    }
}