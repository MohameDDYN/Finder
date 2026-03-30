using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Timers;
using Android.App;
using Android.Content;
using Android.Locations;
using Android.OS;
using Android.Preferences;
using AndroidX.Core.App;
using Finder.Droid.Managers;
using Finder.Droid.Receivers;
using Finder.Models;
using Newtonsoft.Json;
using AndroidLocation = Android.Locations.Location;

namespace Finder.Droid.Services
{
    [Service(ForegroundServiceType = Android.Content.PM.ForegroundService.TypeLocation)]
    public class BackgroundLocationService : Service, ILocationListener
    {
        /// <summary>
        /// Set to true before calling StopService() when the user intentionally stops tracking.
        /// OnDestroy() reads this flag to decide whether to auto-restart the service
        /// and whether to write false to SharedPreferences.
        /// </summary>
        public static bool IsStoppingByUserRequest = false;

        /// <summary>
        /// True while this service instance is alive in the current process.
        /// WatchdogJobService checks this to detect an unexpected service death.
        /// </summary>
        public static bool IsRunning = false;

        public const int SERVICE_NOTIFICATION_ID = 1001;
        private const string NOTIFICATION_CHANNEL_ID = "finder_location_channel";
        private const string GPS_ALERT_CHANNEL_ID = "finder_gps_alert_channel";
        private const int GPS_ALERT_NOTIFICATION_ID = 2001;

        // ── SharedPreferences keys ─────────────────────────────────────────────
        private const string PREF_KEY_RUNNING = "is_tracking_service_running";
        private const string PREF_KEY_SENDING_PAUSED = "is_telegram_sending_paused";

        // ── Broadcast actions ──────────────────────────────────────────────────
        // Existing: interval updates
        public const string ACTION_UPDATE_INTERVAL = "com.finder.UPDATE_INTERVAL";
        // NEW: pause / resume periodic Telegram location sends
        public const string ACTION_SET_SENDING_PAUSED = "com.finder.SET_TELEGRAM_PAUSED";

        private const int STATIONARY_UPDATE_INTERVAL_MS = 60000;
        private const int MOVING_UPDATE_INTERVAL_MS = 20000;
        private const float SIGNIFICANT_MOVEMENT_METERS = 25f;

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

        /// <summary>
        /// When true the periodic Telegram location sends are suppressed.
        /// The GPS recording and service continue unaffected.
        /// Toggled by /pauselocation and /resumelocation commands via broadcast.
        /// Persisted in SharedPreferences so it survives service restarts and reboots.
        /// </summary>
        private bool _isTelegramSendingPaused = false;

        private GeoJsonManager _geoJsonManager;
        private TelegramCommandHandler _commandHandler;
        private IntervalUpdateReceiver _intervalUpdateReceiver;

        private string _telegramBotToken;
        private string _chatId;
        private string _interval;

        // Single JSON settings file shared between the UI layer and all Android components.
        private readonly string _settingsFilePath;

        public BackgroundLocationService()
        {
            _settingsFilePath = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal),
                "secure_settings.json");
        }

        public override IBinder OnBind(Intent intent) => null;

        // ── Service lifecycle ─────────────────────────────────────────────────

        public override StartCommandResult OnStartCommand(
            Intent intent, StartCommandFlags flags, int startId)
        {
            IsRunning = true;
            SetRunningPreference(true);

            bool isExplicitUserStart =
                intent?.GetBooleanExtra("explicit_user_start", false) ?? false;

            LoadSettings();

            // Restore the paused state that may have been set before a restart
            var prefs = PreferenceManager.GetDefaultSharedPreferences(this);
            _isTelegramSendingPaused = prefs.GetBoolean(PREF_KEY_SENDING_PAUSED, false);

            _geoJsonManager = new GeoJsonManager(this);

            // Register the broadcast receiver for both interval updates and
            // the new pause/resume actions using a multi-action IntentFilter.
            if (_intervalUpdateReceiver == null)
            {
                _intervalUpdateReceiver = new IntervalUpdateReceiver(this);

                var filter = new IntentFilter();
                filter.AddAction(ACTION_UPDATE_INTERVAL);
                filter.AddAction(ACTION_SET_SENDING_PAUSED);   // NEW

                RegisterReceiver(_intervalUpdateReceiver, filter);
            }

            if (_commandHandler == null)
            {
                try
                {
                    AppCommandHandler.Stop();
                    _commandHandler = new TelegramCommandHandler(this);
                    _commandHandler.Start(sendStartupMessage: isExplicitUserStart);
                }
                catch { }
            }

            CreateNotificationChannel();
            CreateGpsAlertNotificationChannel();
            StartForeground(SERVICE_NOTIFICATION_ID,
                BuildNotification("Finder is active", "Initializing GPS…"));

            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            InitializeTelegramTimer();
            SetupDailyGeoJsonTimer();
            WatchdogJobService.Schedule(this);

            _locationManager = GetSystemService(LocationService) as LocationManager;
            StartLocationUpdates();

            return StartCommandResult.Sticky;
        }

        public override void OnDestroy()
        {
            // Only write false when the user explicitly stopped tracking.
            // Skipping the write on other paths keeps the preference correct
            // so BootReceiver can restart the service after a reboot.
            if (IsStoppingByUserRequest)
                SetRunningPreference(false);

            IsRunning = false;

            base.OnDestroy();

            if (_intervalUpdateReceiver != null)
            {
                try { UnregisterReceiver(_intervalUpdateReceiver); }
                catch { /* Silent fail */ }
                _intervalUpdateReceiver = null;
            }

            if (_wakeLock?.IsHeld == true)
            {
                _wakeLock.Release();
                _wakeLock = null;
            }

            StopTimer(ref _telegramTimer);
            StopTimer(ref _dailyGeoJsonTimer);

            _commandHandler?.Stop();
            _commandHandler = null;

            _locationManager?.RemoveUpdates(this);
            _locationManager = null;

            _httpClient?.Dispose();
            _httpClient = null;

            // Auto-restart when the service dies for any reason other than
            // an intentional user stop (OS memory kill, crash, etc.).
            if (!IsStoppingByUserRequest)
            {
                try
                {
                    var restartIntent = new Intent(ApplicationContext,
                        typeof(BackgroundLocationService));
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                        StartForegroundService(restartIntent);
                    else
                        StartService(restartIntent);
                }
                catch { /* Silent fail */ }
            }
        }

        /// <summary>
        /// Fires when the user swipes the app away from Recent Tasks.
        /// Schedules RestartReceiver via AlarmManager to relaunch the service
        /// 3 seconds later, giving the OS time to finish its cleanup first.
        /// </summary>
        public override void OnTaskRemoved(Intent rootIntent)
        {
            base.OnTaskRemoved(rootIntent);

            var prefs = PreferenceManager.GetDefaultSharedPreferences(this);
            bool should = prefs.GetBoolean(PREF_KEY_RUNNING, false);

            if (!should || IsStoppingByUserRequest) return;

            try
            {
                var restartIntent = new Intent(ApplicationContext, typeof(RestartReceiver));
                restartIntent.SetPackage(PackageName);

                var pendingFlags = Build.VERSION.SdkInt >= BuildVersionCodes.M
                    ? PendingIntentFlags.OneShot | PendingIntentFlags.Immutable
                    : PendingIntentFlags.OneShot;

                var pendingIntent = PendingIntent.GetBroadcast(
                    ApplicationContext, 0, restartIntent, pendingFlags);

                var alarmManager = (AlarmManager)GetSystemService(AlarmService);
                long triggerAt = SystemClock.ElapsedRealtime() + 3000;

                if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                    alarmManager.SetExactAndAllowWhileIdle(
                        AlarmType.ElapsedRealtime, triggerAt, pendingIntent);
                else
                    alarmManager.SetExact(
                        AlarmType.ElapsedRealtime, triggerAt, pendingIntent);
            }
            catch { }
        }

        // ── GPS / ILocationListener ───────────────────────────────────────────

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

                var lastKnown = _locationManager
                    .GetLastKnownLocation(LocationManager.GpsProvider);
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

                string lat = location.Latitude.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                string lon = location.Longitude.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                _currentLocation = $"{lat},{lon}";

                // Always record to GeoJSON regardless of the sending-paused flag
                _ = Task.Run(() =>
                {
                    try { _geoJsonManager.AddLocationPoint(location, "automatic"); }
                    catch { }
                });

                if (_lastSignificantLocation != null)
                {
                    float dist = location.DistanceTo(_lastSignificantLocation);
                    int newInterval = dist > SIGNIFICANT_MOVEMENT_METERS
                        ? MOVING_UPDATE_INTERVAL_MS
                        : STATIONARY_UPDATE_INTERVAL_MS;

                    if (newInterval != _currentUpdateInterval)
                    {
                        _currentUpdateInterval = newInterval;
                        RestartLocationUpdates();
                    }
                }

                _lastSignificantLocation = location;
            }
            catch { }
            finally
            {
                _isProcessingLocation = false;
                ReleaseWakeLock();
            }
        }

        /// <summary>
        /// Fires automatically when the user disables GPS while the service is running.
        /// Sends a Telegram alert and fires a heads-up notification on the device.
        /// </summary>
        public void OnProviderDisabled(string provider)
        {
            if (provider != LocationManager.GpsProvider) return;

            _currentLocation = "Unknown";
            UpdateNotification("Finder — GPS Disabled", "Tap to enable location.");

            _ = Task.Run(async () =>
            {
                await SendMessageToTelegramAsync(
                    "⚠️ *GPS Disabled*\n" +
                    "Location tracking is paused — GPS was turned off on the device.\n" +
                    "Send /enablelocation to request GPS activation.");
            });

            ShowGpsDisabledNotification();
        }

        /// <summary>
        /// Fires automatically when GPS is re-enabled on the device.
        /// Sends a Telegram confirmation and resumes location updates.
        /// </summary>
        public void OnProviderEnabled(string provider)
        {
            if (provider != LocationManager.GpsProvider) return;

            UpdateNotification("Finder is active", "GPS re-enabled — resuming tracking.");

            _ = Task.Run(async () =>
            {
                await SendMessageToTelegramAsync(
                    "✅ *GPS Enabled*\n" +
                    "Location tracking has resumed automatically.");
            });

            StartLocationUpdates();
        }

        public void OnStatusChanged(string provider, Availability status, Bundle extras) { }

        // ── GPS helpers ───────────────────────────────────────────────────────

        private bool IsGpsEnabled()
        {
            try
            {
                return _locationManager != null &&
                       _locationManager.IsProviderEnabled(LocationManager.GpsProvider);
            }
            catch { return false; }
        }

        /// <summary>
        /// Fires a high-importance heads-up notification on the device.
        /// Tapping it opens Android Location Settings directly.
        /// </summary>
        private void ShowGpsDisabledNotification()
        {
            try
            {
                var settingsIntent = new Intent(
                    Android.Provider.Settings.ActionLocationSourceSettings);
                settingsIntent.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);

                var pendingFlags = Build.VERSION.SdkInt >= BuildVersionCodes.M
                    ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
                    : PendingIntentFlags.UpdateCurrent;

                var pendingIntent = PendingIntent.GetActivity(
                    this, GPS_ALERT_NOTIFICATION_ID, settingsIntent, pendingFlags);

                var notification = new NotificationCompat.Builder(this, GPS_ALERT_CHANNEL_ID)
                    .SetContentTitle("📍 GPS Disabled — Finder")
                    .SetContentText("Tracking paused. Tap to open Location Settings.")
                    .SetStyle(new NotificationCompat.BigTextStyle()
                        .BigText("Finder detected that GPS was turned off.\n" +
                                 "Tap this notification to open Location Settings " +
                                 "and re-enable GPS with one tap."))
                    .SetSmallIcon(Android.Resource.Drawable.IcDialogMap)
                    .SetContentIntent(pendingIntent)
                    .SetAutoCancel(true)
                    .SetPriority(NotificationCompat.PriorityHigh)
                    .Build();

                NotificationManagerCompat
                    .From(this)
                    .Notify(GPS_ALERT_NOTIFICATION_ID, notification);
            }
            catch { }
        }

        /// <summary>
        /// Creates the high-importance notification channel used for GPS alerts.
        /// Idempotent — safe to call multiple times.
        /// </summary>
        private void CreateGpsAlertNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;

            try
            {
                var channel = new NotificationChannel(
                    GPS_ALERT_CHANNEL_ID,
                    "Finder GPS Alerts",
                    NotificationImportance.High)
                {
                    Description = "Alerts when GPS is disabled while Finder is tracking."
                };

                channel.EnableVibration(true);

                ((NotificationManager)GetSystemService(NotificationService))
                    ?.CreateNotificationChannel(channel);
            }
            catch { }
        }

        // ── Telegram timer ────────────────────────────────────────────────────

        private void InitializeTelegramTimer()
        {
            StopTimer(ref _telegramTimer);

            int intervalMs = int.TryParse(_interval, out int iv) ? iv : 60000;

            _telegramTimer = new Timer(intervalMs);
            _telegramTimer.Elapsed += async (s, e) => await OnTelegramTimerElapsed();
            _telegramTimer.AutoReset = true;
            _telegramTimer.Start();
        }

        private async Task OnTelegramTimerElapsed()
        {
            try
            {
                AcquireWakeLock();
                _isProcessingLocation = true;
                LoadSettings();

                if (!string.IsNullOrEmpty(_telegramBotToken) &&
                    !string.IsNullOrEmpty(_chatId))
                {
                    // ── PAUSE GUARD ───────────────────────────────────────────
                    // When the user has sent /pauselocation we skip sending to
                    // Telegram but still update the notification bar so the
                    // service appears healthy. GPS recording continues as normal.
                    if (_isTelegramSendingPaused)
                    {
                        UpdateNotification("Finder is active — sends paused",
                            $"Paused · {DateTime.Now:HH:mm:ss} · {_currentLocation}");
                        return;
                    }
                    // ─────────────────────────────────────────────────────────

                    bool inMovingMode = _currentUpdateInterval == MOVING_UPDATE_INTERVAL_MS;

                    if (_currentLocation != "Unknown")
                    {
                        var parts = _currentLocation.Split(',');
                        if (parts.Length == 2 &&
                            double.TryParse(parts[0],
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out double lat) &&
                            double.TryParse(parts[1],
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out double lon))
                        {
                            if (inMovingMode || _updateCounter % 3 == 0)
                                await SendLocationToTelegramAsync();

                            _updateCounter++;
                            UpdateNotification("Finder is active",
                                $"Last update: {DateTime.Now:HH:mm:ss} · {_currentLocation}");
                        }
                    }
                    else
                    {
                        await SendMessageToTelegramAsync(
                            "📍 Location unknown — waiting for GPS fix…");
                    }
                }
                else
                {
                    UpdateNotification("Finder is active",
                        $"Telegram not configured · {DateTime.Now:HH:mm}");
                }
            }
            catch { }
            finally
            {
                _isProcessingLocation = false;
                ReleaseWakeLock();
            }
        }

        public void UpdateInterval(int newIntervalMs)
        {
            try
            {
                _interval = newIntervalMs.ToString();

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
            catch { }
        }

        /// <summary>
        /// Pauses or resumes periodic Telegram location sends without stopping the service.
        /// Called by IntervalUpdateReceiver when a SET_TELEGRAM_PAUSED broadcast arrives.
        /// The state is persisted to SharedPreferences so it survives service restarts.
        /// </summary>
        public void SetTelegramSendingPaused(bool paused)
        {
            _isTelegramSendingPaused = paused;

            // Persist so the flag survives a service restart or reboot
            var prefs = PreferenceManager.GetDefaultSharedPreferences(this);
            var editor = prefs.Edit();
            editor.PutBoolean(PREF_KEY_SENDING_PAUSED, paused);
            editor.Apply();

            // Update the persistent notification so the user can see the state
            if (paused)
                UpdateNotification("Finder is active — sends paused",
                    "GPS recording continues · Telegram sends are paused");
            else
                UpdateNotification("Finder is active",
                    "Telegram location sends resumed");
        }

        // ── Daily GeoJSON report ──────────────────────────────────────────────

        private void SetupDailyGeoJsonTimer()
        {
            var timeUntilMidnight = DateTime.Now.Date.AddDays(1) - DateTime.Now;

            _dailyGeoJsonTimer = new Timer(timeUntilMidnight.TotalMilliseconds);
            _dailyGeoJsonTimer.Elapsed += DailyGeoJsonTimer_Elapsed;
            _dailyGeoJsonTimer.AutoReset = false;
            _dailyGeoJsonTimer.Start();
        }

        private async void DailyGeoJsonTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            try
            {
                var yesterday = DateTime.Now.Date.AddDays(-1);
                var settings = LoadSettingsFromFile();

                if (settings != null &&
                    !string.IsNullOrEmpty(settings.BotToken) &&
                    !string.IsNullOrEmpty(settings.ChatId))
                {
                    string geoJson = await _geoJsonManager.GenerateGeoJsonForDate(yesterday);
                    if (!string.IsNullOrEmpty(geoJson))
                    {
                        string tempFile = Path.Combine(
                            Path.GetTempPath(),
                            $"finder_daily_{yesterday:yyyy-MM-dd}.geojson");
                        File.WriteAllText(tempFile, geoJson);

                        await SendFileToTelegramAsync(tempFile,
                            $"📊 Daily report · {yesterday:yyyy-MM-dd}", settings);

                        if (File.Exists(tempFile)) File.Delete(tempFile);
                    }
                }
            }
            catch { }
            finally
            {
                SetupDailyGeoJsonTimer();
            }
        }

        // ── Telegram API helpers ──────────────────────────────────────────────

        private async Task SendLocationToTelegramAsync()
        {
            var parts = _currentLocation.Split(',');
            if (parts.Length != 2) return;

            if (!double.TryParse(parts[0],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double lat)) return;
            if (!double.TryParse(parts[1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double lon)) return;

            try
            {
                await _httpClient.GetAsync(
                    $"https://api.telegram.org/bot{_telegramBotToken}" +
                    $"/sendLocation?chat_id={_chatId}" +
                    $"&latitude={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                    $"&longitude={lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            catch { }
        }

        private async Task SendMessageToTelegramAsync(string message)
        {
            if (string.IsNullOrEmpty(_telegramBotToken) ||
                string.IsNullOrEmpty(_chatId)) return;
            try
            {
                await _httpClient.GetAsync(
                    $"https://api.telegram.org/bot{_telegramBotToken}" +
                    $"/sendMessage?chat_id={_chatId}" +
                    $"&text={Uri.EscapeDataString(message)}&parse_mode=Markdown");
            }
            catch { }
        }

        private async Task SendFileToTelegramAsync(
            string filePath, string caption, AppSettings settings)
        {
            if (!File.Exists(filePath)) return;
            try
            {
                using (var form = new MultipartFormDataContent())
                {
                    var bytes = File.ReadAllBytes(filePath);
                    var content = new ByteArrayContent(bytes);
                    content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("application/geo+json");
                    form.Add(content, "document", Path.GetFileName(filePath));
                    form.Add(new StringContent(settings.ChatId), "chat_id");
                    form.Add(new StringContent(caption), "caption");

                    await _httpClient.PostAsync(
                        $"https://api.telegram.org/bot{settings.BotToken}/sendDocument", form);
                }
            }
            catch { }
        }

        // ── Settings ──────────────────────────────────────────────────────────

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
                    _interval = string.IsNullOrEmpty(settings.Interval)
                                        ? "60000"
                                        : settings.Interval;
                }
            }
            catch { }
        }

        private AppSettings LoadSettingsFromFile()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                    return JsonConvert.DeserializeObject<AppSettings>(
                        File.ReadAllText(_settingsFilePath));
            }
            catch { }
            return null;
        }

        private void SaveSettingsToFile(AppSettings settings)
        {
            try { File.WriteAllText(_settingsFilePath, JsonConvert.SerializeObject(settings)); }
            catch { }
        }

        // ── Notification helpers ──────────────────────────────────────────────

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            try
            {
                var channel = new NotificationChannel(
                    NOTIFICATION_CHANNEL_ID,
                    "Finder Location Service",
                    NotificationImportance.Low)
                {
                    Description = "Shows while Finder is tracking your location."
                };
                ((NotificationManager)GetSystemService(NotificationService))
                    ?.CreateNotificationChannel(channel);
            }
            catch { }
        }

        private Android.App.Notification BuildNotification(string title, string text)
        {
            return new NotificationCompat.Builder(this, NOTIFICATION_CHANNEL_ID)
                .SetContentTitle(title)
                .SetContentText(text)
                .SetSmallIcon(Android.Resource.Drawable.IcDialogMap)
                .SetOngoing(true)
                .SetPriority(NotificationCompat.PriorityLow)
                .Build();
        }

        private void UpdateNotification(string title, string text)
        {
            try
            {
                var nm = (NotificationManager)GetSystemService(NotificationService);
                nm?.Notify(SERVICE_NOTIFICATION_ID, BuildNotification(title, text));
            }
            catch { }
        }

        // ── WakeLock ──────────────────────────────────────────────────────────

        private void AcquireWakeLock()
        {
            if (_wakeLock == null)
            {
                var pm = (PowerManager)GetSystemService(PowerService);
                _wakeLock = pm.NewWakeLock(WakeLockFlags.Partial, "Finder::LocationWakeLock");
            }
            if (!_wakeLock.IsHeld && _isProcessingLocation)
                _wakeLock.Acquire(30000);
        }

        private void ReleaseWakeLock()
        {
            if (_wakeLock?.IsHeld == true && !_isProcessingLocation)
                _wakeLock.Release();
        }

        // ── Misc helpers ──────────────────────────────────────────────────────

        private void SetRunningPreference(bool running)
        {
            var prefs = PreferenceManager.GetDefaultSharedPreferences(this);
            var editor = prefs.Edit();
            editor.PutBoolean(PREF_KEY_RUNNING, running);
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
            catch { }
        }

        private static void StopTimer(ref Timer timer)
        {
            if (timer == null) return;
            timer.Stop();
            timer.Dispose();
            timer = null;
        }
    }

    // ── Broadcast receiver ────────────────────────────────────────────────────

    /// <summary>
    /// Handles dynamic runtime commands sent to the running service via LocalBroadcast.
    ///
    /// Actions handled:
    ///   com.finder.UPDATE_INTERVAL     → updates the Telegram send interval
    ///   com.finder.SET_TELEGRAM_PAUSED → pauses or resumes Telegram location sends
    /// </summary>
    public class IntervalUpdateReceiver : BroadcastReceiver
    {
        private readonly BackgroundLocationService _service;

        public IntervalUpdateReceiver(BackgroundLocationService service)
        {
            _service = service;
        }

        public override void OnReceive(Context context, Intent intent)
        {
            if (intent == null) return;

            switch (intent.Action)
            {
                // Existing: update the periodic send interval
                case BackgroundLocationService.ACTION_UPDATE_INTERVAL:
                    int newInterval = intent.GetIntExtra("new_interval", 60000);
                    _service.UpdateInterval(newInterval);
                    break;

                // NEW: pause or resume periodic Telegram location sends
                case BackgroundLocationService.ACTION_SET_SENDING_PAUSED:
                    bool paused = intent.GetBooleanExtra("paused", false);
                    _service.SetTelegramSendingPaused(paused);
                    break;
            }
        }
    }
}