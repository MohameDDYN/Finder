using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Timers;
using Android.App;
using Android.Content;
using Android.Locations;
using Android.OS;
using AndroidX.Core.App;
using Finder.Droid.Managers;
using Finder.Models;
using Newtonsoft.Json;
using AndroidLocation = Android.Locations.Location;

namespace Finder.Droid.Services
{
    /// <summary>
    /// Foreground service that continuously tracks GPS location and
    /// sends periodic updates to a Telegram bot.
    /// Survives app closes and device reboots (via BootReceiver).
    /// </summary>
    [Service(ForegroundServiceType = Android.Content.PM.ForegroundService.TypeLocation)]
    public class BackgroundLocationService : Service, ILocationListener
    {
        // ── Static flag: set to true when user explicitly stops tracking ───
        public static bool IsStoppingByUserRequest = false;

        // ── Notification constants ─────────────────────────────────────────
        public const int SERVICE_NOTIFICATION_ID = 1001;
        private const string NOTIFICATION_CHANNEL_ID = "finder_location_channel";

        // ── Adaptive update intervals ──────────────────────────────────────
        private const int STATIONARY_UPDATE_INTERVAL_MS = 60000; // 1 minute when still
        private const int MOVING_UPDATE_INTERVAL_MS = 20000; // 20 seconds when moving
        private const float SIGNIFICANT_MOVEMENT_METERS = 25f;   // 25m triggers "moving" mode

        // ── State ──────────────────────────────────────────────────────────
        private AndroidLocation _lastSignificantLocation;
        private int _currentUpdateInterval = STATIONARY_UPDATE_INTERVAL_MS;
        private LocationManager _locationManager;
        private Timer _telegramTimer;
        private Timer _dailyGeoJsonTimer;
        private HttpClient _httpClient;
        private string _currentLocation = "Unknown";
        private PowerManager.WakeLock _wakeLock;
        private bool _isProcessingLocation = false;
        private int _updateCounter = 0;

        // ── Dependencies ───────────────────────────────────────────────────
        private GeoJsonManager _geoJsonManager;
        private TelegramCommandHandler _commandHandler;
        private IntervalUpdateReceiver _intervalUpdateReceiver;

        // ── Telegram credentials (loaded from file) ────────────────────────
        private string _telegramBotToken;
        private string _chatId;
        private string _interval;

        private readonly string _settingsFilePath;

        public BackgroundLocationService()
        {
            _settingsFilePath = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal),
                "secure_settings.json");
        }

        public override IBinder OnBind(Intent intent) => null;

        // ── Service lifecycle ──────────────────────────────────────────────

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            // Mark service as running in SharedPreferences
            SetRunningPreference(true);

            // Load Telegram credentials from settings file
            LoadSettings();

            // Initialize location data manager
            _geoJsonManager = new GeoJsonManager(this);

            // Register interval update broadcast receiver
            if (_intervalUpdateReceiver == null)
            {
                _intervalUpdateReceiver = new IntervalUpdateReceiver(this);
                var filter = new IntentFilter("com.finder.UPDATE_INTERVAL");
                RegisterReceiver(_intervalUpdateReceiver, filter);
            }

            // Start Telegram command handler (if not already running)
            if (_commandHandler == null)
            {
                try
                {
                    _commandHandler = new TelegramCommandHandler(this);
                    _commandHandler.Start();
                }
                catch { /* Silent fail */ }
            }

            // Create notification channel (required on Android 8+)
            CreateNotificationChannel();

            // Show foreground notification
            var notification = BuildNotification("Finder is active", "Initializing GPS…");
            StartForeground(SERVICE_NOTIFICATION_ID, notification);

            // Initialize HTTP client
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            // Start periodic Telegram update timer
            InitializeTelegramTimer();

            // Set up daily GeoJSON report timer
            SetupDailyGeoJsonTimer();

            // Start receiving GPS location updates
            _locationManager = GetSystemService(LocationService) as LocationManager;
            StartLocationUpdates();

            // Return Sticky so Android restarts the service if it's killed
            return StartCommandResult.Sticky;
        }

        public override void OnDestroy()
        {
            SetRunningPreference(false);
            base.OnDestroy();

            // Unregister broadcast receiver
            if (_intervalUpdateReceiver != null)
            {
                try { UnregisterReceiver(_intervalUpdateReceiver); }
                catch { /* Silent fail */ }
                _intervalUpdateReceiver = null;
            }

            // Release wake lock
            if (_wakeLock?.IsHeld == true)
            {
                _wakeLock.Release();
                _wakeLock = null;
            }

            // Stop timers
            StopTimer(ref _telegramTimer);
            StopTimer(ref _dailyGeoJsonTimer);

            // Stop command handler
            _commandHandler?.Stop();
            _commandHandler = null;

            // Remove GPS updates
            _locationManager?.RemoveUpdates(this);
            _locationManager = null;

            // Dispose HTTP client
            _httpClient?.Dispose();
            _httpClient = null;

            // Auto-restart unless user explicitly stopped
            if (!IsStoppingByUserRequest)
            {
                var intent = new Intent(ApplicationContext, typeof(BackgroundLocationService));
                StartService(intent);
            }
        }

        // ── GPS location updates ───────────────────────────────────────────

        private void StartLocationUpdates()
        {
            if (_locationManager == null) return;

            if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation)
                != Android.Content.PM.Permission.Granted) return;

            try
            {
                _locationManager.RequestLocationUpdates(
                    LocationManager.GpsProvider,
                    _currentUpdateInterval,
                    SIGNIFICANT_MOVEMENT_METERS,
                    this);

                // Use last known location immediately if available
                var lastKnown = _locationManager.GetLastKnownLocation(LocationManager.GpsProvider);
                if (lastKnown != null)
                {
                    _lastSignificantLocation = lastKnown;
                    OnLocationChanged(lastKnown);
                }
            }
            catch (Exception ex)
            {
                UpdateNotification("GPS Error", ex.Message);
            }
        }

        public void OnLocationChanged(AndroidLocation location)
        {
            if (location == null) return;

            try
            {
                _isProcessingLocation = true;
                AcquireWakeLock();

                // Format coordinates with invariant culture (ensures '.' decimal)
                string lat = location.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string lon = location.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _currentLocation = $"{lat},{lon}";

                // Save point to GeoJSON file asynchronously
                _ = Task.Run(() =>
                {
                    try { _geoJsonManager.AddLocationPoint(location, "automatic"); }
                    catch { /* Silent fail */ }
                });

                // Adaptive update frequency based on movement
                if (_lastSignificantLocation != null)
                {
                    float distance = location.DistanceTo(_lastSignificantLocation);
                    int newInterval = distance > SIGNIFICANT_MOVEMENT_METERS
                        ? MOVING_UPDATE_INTERVAL_MS
                        : STATIONARY_UPDATE_INTERVAL_MS;

                    if (newInterval != _currentUpdateInterval)
                    {
                        _currentUpdateInterval = newInterval;
                        _lastSignificantLocation = location;
                        RestartLocationUpdates();
                    }
                }
                else
                {
                    _lastSignificantLocation = location;
                }
            }
            catch { /* Silent fail */ }
            finally
            {
                _isProcessingLocation = false;
                ReleaseWakeLock();
            }
        }

        public void OnProviderEnabled(string provider) => UpdateNotification("Finder is active", "GPS enabled");
        public void OnProviderDisabled(string provider) => UpdateNotification("Finder is active", "GPS disabled — waiting…");
        public void OnStatusChanged(string provider, Availability status, Bundle extras) { }

        // ── Telegram timer ─────────────────────────────────────────────────

        private void InitializeTelegramTimer()
        {
            StopTimer(ref _telegramTimer);

            if (!int.TryParse(_interval, out int intervalMs))
                intervalMs = 60000;

            _telegramTimer = new Timer(intervalMs);
            _telegramTimer.Elapsed += TelegramTimer_Elapsed;
            _telegramTimer.AutoReset = true;
            _telegramTimer.Start();
        }

        private async void TelegramTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                _isProcessingLocation = true;
                AcquireWakeLock();

                bool credentialsValid = !string.IsNullOrEmpty(_telegramBotToken)
                                     && !string.IsNullOrEmpty(_chatId);

                if (credentialsValid)
                {
                    if (_currentLocation != "Unknown")
                    {
                        // In moving mode, send every tick; stationary — every 3rd tick
                        bool inMovingMode = _currentUpdateInterval == MOVING_UPDATE_INTERVAL_MS;
                        if (inMovingMode || _updateCounter % 3 == 0)
                            await SendLocationToTelegramAsync();

                        _updateCounter++;
                        UpdateNotification("Finder is active",
                            $"Last update: {DateTime.Now:HH:mm:ss} · {_currentLocation}");
                    }
                    else
                    {
                        await SendMessageToTelegramAsync("📍 Location unknown — waiting for GPS fix…");
                    }
                }
                else
                {
                    UpdateNotification("Finder is active",
                        $"Telegram not configured · {DateTime.Now:HH:mm}");
                }
            }
            catch { /* Silent fail */ }
            finally
            {
                _isProcessingLocation = false;
                ReleaseWakeLock();
            }
        }

        // ── Update interval from broadcast ─────────────────────────────────

        public void UpdateInterval(int newIntervalMs)
        {
            try
            {
                _interval = newIntervalMs.ToString();

                // Persist updated interval to settings file
                var settings = LoadSettingsFromFile();
                if (settings != null)
                {
                    settings.Interval = _interval;
                    SaveSettingsToFile(settings);
                }

                InitializeTelegramTimer();
                UpdateNotification("Finder is active",
                    $"Interval updated to {newIntervalMs} ms");
            }
            catch { /* Silent fail */ }
        }

        // ── Daily GeoJSON report ───────────────────────────────────────────

        private void SetupDailyGeoJsonTimer()
        {
            var now = DateTime.Now;
            var nextMidnight = now.Date.AddDays(1);
            var timeUntilMidnight = nextMidnight - now;

            _dailyGeoJsonTimer = new Timer(timeUntilMidnight.TotalMilliseconds);
            _dailyGeoJsonTimer.Elapsed += DailyGeoJsonTimer_Elapsed;
            _dailyGeoJsonTimer.AutoReset = false;
            _dailyGeoJsonTimer.Start();
        }

        private async void DailyGeoJsonTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                await SendDailyReportAsync(DateTime.Now.AddDays(-1));
                await _geoJsonManager.CleanupOldFiles(30);

                // Re-arm for next midnight
                _dailyGeoJsonTimer.Interval = TimeSpan.FromDays(1).TotalMilliseconds;
                _dailyGeoJsonTimer.AutoReset = true;
                _dailyGeoJsonTimer.Start();
            }
            catch { /* Silent fail */ }
        }

        private async Task SendDailyReportAsync(DateTime date)
        {
            if (string.IsNullOrEmpty(_telegramBotToken) || string.IsNullOrEmpty(_chatId)) return;

            try
            {
                string geoJson = await _geoJsonManager.GenerateGeoJsonForDate(date);

                if (string.IsNullOrEmpty(geoJson))
                {
                    await SendMessageToTelegramAsync($"No location data for {date:yyyy-MM-dd}");
                    return;
                }

                string tempFile = Path.Combine(
                    Path.GetTempPath(),
                    $"finder_report_{date:yyyy-MM-dd}.geojson");

                await File.WriteAllTextAsync(tempFile, geoJson);
                await SendFileToTelegramAsync(tempFile, $"📊 Daily report — {date:yyyy-MM-dd}");

                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
            catch { /* Silent fail */ }
        }

        // ── Telegram API calls ─────────────────────────────────────────────

        private async Task SendLocationToTelegramAsync()
        {
            try
            {
                string[] coords = _currentLocation.Split(',');
                if (coords.Length != 2) return;
                if (!double.TryParse(coords[0], System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double lat) ||
                    !double.TryParse(coords[1], System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double lon)) return;

                string url = $"https://api.telegram.org/bot{_telegramBotToken}" +
                             $"/sendLocation?chat_id={_chatId}" +
                             $"&latitude={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                             $"&longitude={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

                await _httpClient.GetAsync(url);
            }
            catch { /* Silent fail */ }
        }

        private async Task SendMessageToTelegramAsync(string message)
        {
            if (string.IsNullOrEmpty(_telegramBotToken) || string.IsNullOrEmpty(_chatId)) return;
            try
            {
                string url = $"https://api.telegram.org/bot{_telegramBotToken}" +
                             $"/sendMessage?chat_id={_chatId}" +
                             $"&text={Uri.EscapeDataString(message)}";
                await _httpClient.GetAsync(url);
            }
            catch { /* Silent fail */ }
        }

        private async Task SendFileToTelegramAsync(string filePath, string caption)
        {
            if (!File.Exists(filePath)) return;
            try
            {
                using (var form = new MultipartFormDataContent())
                {
                    var bytes = await File.ReadAllBytesAsync(filePath);
                    var content = new ByteArrayContent(bytes);
                    content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("application/geo+json");
                    form.Add(content, "document", Path.GetFileName(filePath));
                    form.Add(new StringContent(_chatId), "chat_id");
                    form.Add(new StringContent(caption), "caption");

                    string url = $"https://api.telegram.org/bot{_telegramBotToken}/sendDocument";
                    await _httpClient.PostAsync(url, form);
                }
            }
            catch { /* Silent fail */ }
        }

        // ── Settings ───────────────────────────────────────────────────────

        private void LoadSettings()
        {
            try
            {
                var settings = LoadSettingsFromFile();
                if (settings != null &&
                    !string.IsNullOrEmpty(settings.BotToken) &&
                    !string.IsNullOrEmpty(settings.ChatId))
                {
                    _telegramBotToken = settings.BotToken;
                    _chatId = settings.ChatId;
                    _interval = string.IsNullOrEmpty(settings.Interval) ? "60000" : settings.Interval;
                    return;
                }
            }
            catch { /* Fall through to defaults */ }

            _telegramBotToken = null;
            _chatId = null;
            _interval = "60000";
            UpdateNotification("Finder — Not configured", "Open app to add Telegram credentials");
        }

        private AppSettings LoadSettingsFromFile()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                    return JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(_settingsFilePath));
            }
            catch { /* Silent fail */ }
            return null;
        }

        private void SaveSettingsToFile(AppSettings settings)
        {
            try { File.WriteAllText(_settingsFilePath, JsonConvert.SerializeObject(settings)); }
            catch { /* Silent fail */ }
        }

        // ── Notification helpers ───────────────────────────────────────────

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;

            var channel = new NotificationChannel(
                NOTIFICATION_CHANNEL_ID,
                "Finder Location Service",
                NotificationImportance.Low)
            {
                Description = "Shows while Finder is actively tracking your location."
            };

            var mgr = (NotificationManager)GetSystemService(NotificationService);
            mgr.CreateNotificationChannel(channel);
        }

        private Notification BuildNotification(string title, string text)
        {
            var intent = new Intent(this, typeof(MainActivity));
            intent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            var pendingIntent = PendingIntent.GetActivity(this, 0, intent, PendingIntentFlags.Immutable);

            return new NotificationCompat.Builder(this, NOTIFICATION_CHANNEL_ID)
                .SetContentTitle(title)
                .SetContentText(text)
                .SetSmallIcon(Android.Resource.Drawable.IcDialogMap)
                .SetOngoing(true)
                .SetContentIntent(pendingIntent)
                .Build();
        }

        private void UpdateNotification(string title, string text)
        {
            try
            {
                var notification = BuildNotification(title, text);
                NotificationManagerCompat.From(this).Notify(SERVICE_NOTIFICATION_ID, notification);
            }
            catch { /* Silent fail */ }
        }

        // ── Wake lock helpers ──────────────────────────────────────────────

        private void AcquireWakeLock()
        {
            if (_wakeLock == null)
            {
                var pm = (PowerManager)GetSystemService(PowerService);
                _wakeLock = pm.NewWakeLock(WakeLockFlags.Partial, "Finder::LocationWakeLock");
            }
            if (!_wakeLock.IsHeld && _isProcessingLocation)
                _wakeLock.Acquire(30000); // Auto-release after 30 seconds
        }

        private void ReleaseWakeLock()
        {
            if (_wakeLock?.IsHeld == true && !_isProcessingLocation)
                _wakeLock.Release();
        }

        // ── Utility helpers ────────────────────────────────────────────────

        private void SetRunningPreference(bool running)
        {
            var prefs = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(this);
            var editor = prefs.Edit();
            editor.PutBoolean("is_tracking_service_running", running);
            editor.Apply();
        }

        private void RestartLocationUpdates()
        {
            try
            {
                _locationManager?.RemoveUpdates(this);
                _locationManager?.RequestLocationUpdates(
                    LocationManager.GpsProvider,
                    _currentUpdateInterval,
                    SIGNIFICANT_MOVEMENT_METERS,
                    this);
            }
            catch { /* Silent fail */ }
        }

        private static void StopTimer(ref Timer timer)
        {
            if (timer == null) return;
            timer.Stop();
            timer.Dispose();
            timer = null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Broadcast receiver for dynamic interval updates
    // ─────────────────────────────────────────────────────────────────────────

    public class IntervalUpdateReceiver : BroadcastReceiver
    {
        private readonly BackgroundLocationService _service;

        public IntervalUpdateReceiver(BackgroundLocationService service)
        {
            _service = service;
        }

        public override void OnReceive(Context context, Intent intent)
        {
            if (intent?.Action == "com.finder.UPDATE_INTERVAL")
            {
                int newInterval = intent.GetIntExtra("new_interval", 60000);
                _service.UpdateInterval(newInterval);
            }
        }
    }
}