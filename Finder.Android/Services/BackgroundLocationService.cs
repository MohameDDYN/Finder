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
        // ── Static flags ──────────────────────────────────────────────────────

        /// <summary>
        /// True only when the user explicitly presses Stop in the UI or sends /stop via Telegram.
        /// Used by OnDestroy() to decide whether to auto-restart and whether to write
        /// false to SharedPreferences.
        /// </summary>
        public static bool IsStoppingByUserRequest = false;

        /// <summary>
        /// True while this service instance is alive in the current process.
        /// Reset to false in OnDestroy(). Used by WatchdogJobService to detect
        /// whether the service is actually running (not just what the preference says).
        /// </summary>
        public static bool IsRunning = false;

        // ── Constants ─────────────────────────────────────────────────────────
        public const int SERVICE_NOTIFICATION_ID = 1001;
        private const string NOTIFICATION_CHANNEL_ID = "finder_location_channel";
        private const string PREF_KEY_RUNNING = "is_tracking_service_running";

        private const int STATIONARY_UPDATE_INTERVAL_MS = 60000;
        private const int MOVING_UPDATE_INTERVAL_MS = 20000;
        private const float SIGNIFICANT_MOVEMENT_METERS = 25f;

        // ── State ─────────────────────────────────────────────────────────────
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

        // ── Dependencies ──────────────────────────────────────────────────────
        private GeoJsonManager _geoJsonManager;
        private TelegramCommandHandler _commandHandler;
        private IntervalUpdateReceiver _intervalUpdateReceiver;

        // ── Credentials ───────────────────────────────────────────────────────
        private string _telegramBotToken;
        private string _chatId;
        private string _interval;

        // Single JSON file shared between UI layer and all Android components.
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
            // Mark this process-level instance as alive
            IsRunning = true;

            // Always write true — covers normal start AND BootReceiver restart after reboot.
            SetRunningPreference(true);

            LoadSettings();
            _geoJsonManager = new GeoJsonManager(this);

            if (_intervalUpdateReceiver == null)
            {
                _intervalUpdateReceiver = new IntervalUpdateReceiver(this);
                RegisterReceiver(_intervalUpdateReceiver,
                    new IntentFilter("com.finder.UPDATE_INTERVAL"));
            }

            if (_commandHandler == null)
            {
                try
                {
                    _commandHandler = new TelegramCommandHandler(this);
                    _commandHandler.Start();
                }
                catch { /* Silent fail */ }
            }

            CreateNotificationChannel();
            StartForeground(SERVICE_NOTIFICATION_ID,
                BuildNotification("Finder is active", "Initializing GPS…"));

            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            InitializeTelegramTimer();
            SetupDailyGeoJsonTimer();

            _locationManager = GetSystemService(LocationService) as LocationManager;
            StartLocationUpdates();

            // ── Layer 4: schedule the periodic watchdog job ────────────────────
            // The watchdog runs every 15 min and restarts the service if it finds
            // IsRunning=false while the preference still says it should be running.
            WatchdogJobService.Schedule(this);

            // Sticky: if Android kills the service due to memory pressure,
            // it will restart it automatically (with a null intent).
            return StartCommandResult.Sticky;
        }

        public override void OnDestroy()
        {
            // ── Do NOT write false unconditionally ────────────────────────────
            //
            // Writing false here unconditionally breaks two things:
            //
            //   • Reboot: Phone shuts down → OnDestroy fires → false written →
            //     BootReceiver reads false after reboot → service never restarts. ❌
            //
            //   • Stop button: App closed → OnDestroy fires → false written →
            //     UI reads false on reopen → Stop button is permanently disabled
            //     even though the service is still running in the background. ❌
            //
            // LocationService.StopTracking() writes false BEFORE calling StopService(),
            // so no write is needed here for the intentional-stop path.
            // ─────────────────────────────────────────────────────────────────
            if (IsStoppingByUserRequest)
                SetRunningPreference(false);

            // Mark process-level instance as gone
            IsRunning = false;

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

            StopTimer(ref _telegramTimer);
            StopTimer(ref _dailyGeoJsonTimer);

            _commandHandler?.Stop();
            _commandHandler = null;

            _locationManager?.RemoveUpdates(this);
            _locationManager = null;

            _httpClient?.Dispose();
            _httpClient = null;

            // ── Layer 2: immediate auto-restart (OS / crash kills) ─────────────
            // Only when the stop was NOT triggered by the user intentionally.
            // This covers: OS memory kill, crash, unexpected termination.
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
                catch { /* Silent fail — Layer 3 / 4 will catch this */ }
            }
        }

        /// <summary>
        /// Layer 3: Fires when the user swipes the app away from the Recent Tasks screen.
        /// Schedules a delayed restart via AlarmManager so the service survives
        /// task removal even if OnDestroy's direct restart fails.
        /// </summary>
        public override void OnTaskRemoved(Intent rootIntent)
        {
            base.OnTaskRemoved(rootIntent);

            // Only schedule a restart if the user has NOT explicitly stopped tracking.
            var prefs = PreferenceManager.GetDefaultSharedPreferences(this);
            bool should = prefs.GetBoolean(PREF_KEY_RUNNING, false);

            if (!should || IsStoppingByUserRequest) return;

            try
            {
                // Schedule the RestartReceiver to fire 3 seconds from now.
                var restartIntent = new Intent(ApplicationContext,
                    typeof(RestartReceiver));
                restartIntent.SetPackage(PackageName);

                var pendingFlags = Build.VERSION.SdkInt >= BuildVersionCodes.M
                    ? PendingIntentFlags.OneShot | PendingIntentFlags.Immutable
                    : PendingIntentFlags.OneShot;

                var pendingIntent = PendingIntent.GetBroadcast(
                    ApplicationContext, 0, restartIntent, pendingFlags);

                var alarmManager = (AlarmManager)GetSystemService(AlarmService);
                long triggerAt = SystemClock.ElapsedRealtime() + 3000; // 3 s

                if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
                    alarmManager.SetExactAndAllowWhileIdle(
                        AlarmType.ElapsedRealtime, triggerAt, pendingIntent);
                else
                    alarmManager.SetExact(
                        AlarmType.ElapsedRealtime, triggerAt, pendingIntent);
            }
            catch { /* Silent fail */ }
        }

        // ── GPS ───────────────────────────────────────────────────────────────

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

                _ = Task.Run(() =>
                {
                    try { _geoJsonManager.AddLocationPoint(location, "automatic"); }
                    catch { /* Silent fail */ }
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
            catch { /* Silent fail */ }
            finally
            {
                _isProcessingLocation = false;
                ReleaseWakeLock();
            }
        }

        public void OnProviderEnabled(string provider) { }
        public void OnProviderDisabled(string provider) { }
        public void OnStatusChanged(string provider, Availability status, Bundle extras) { }

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
            catch { /* Silent fail */ }
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
            catch { /* Silent fail */ }
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
            catch { /* Silent fail */ }
            finally
            {
                // Reschedule for the next midnight
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
            catch { /* Silent fail */ }
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
                    $"&text={Uri.EscapeDataString(message)}");
            }
            catch { /* Silent fail */ }
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
                        $"https://api.telegram.org/bot{settings.BotToken}/sendDocument",
                        form);
                }
            }
            catch { /* Silent fail */ }
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
                                        ? "60000" : settings.Interval;
                    return;
                }
            }
            catch { /* Fall through to defaults */ }

            _telegramBotToken = null;
            _chatId = null;
            _interval = "60000";
            UpdateNotification("Finder — Not configured",
                "Open app to add Telegram credentials");
        }

        private AppSettings LoadSettingsFromFile()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                    return JsonConvert.DeserializeObject<AppSettings>(
                        File.ReadAllText(_settingsFilePath));
            }
            catch { /* Silent fail */ }
            return null;
        }

        private void SaveSettingsToFile(AppSettings settings)
        {
            try
            {
                File.WriteAllText(_settingsFilePath,
                    JsonConvert.SerializeObject(settings));
            }
            catch { /* Silent fail */ }
        }

        // ── Notification ──────────────────────────────────────────────────────

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;

            var channel = new Android.App.NotificationChannel(
                NOTIFICATION_CHANNEL_ID,
                "Finder Location Service",
                Android.App.NotificationImportance.Low)
            {
                Description = "Shows while Finder is actively tracking your location."
            };

            ((Android.App.NotificationManager)GetSystemService(NotificationService))
                .CreateNotificationChannel(channel);
        }

        private Android.App.Notification BuildNotification(string title, string text)
        {
            var intent = new Intent(this, typeof(MainActivity));
            intent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);

            var pendingFlags = Build.VERSION.SdkInt >= BuildVersionCodes.M
                ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
                : PendingIntentFlags.UpdateCurrent;

            var pending = PendingIntent.GetActivity(this, 0, intent, pendingFlags);

            return new NotificationCompat.Builder(this, NOTIFICATION_CHANNEL_ID)
                .SetContentTitle(title)
                .SetContentText(text)
                .SetSmallIcon(Android.Resource.Drawable.IcDialogMap)
                .SetOngoing(true)
                .SetContentIntent(pending)
                .Build();
        }

        private void UpdateNotification(string title, string text)
        {
            try
            {
                NotificationManagerCompat.From(this)
                    .Notify(SERVICE_NOTIFICATION_ID, BuildNotification(title, text));
            }
            catch { /* Silent fail */ }
        }

        // ── Wake lock ─────────────────────────────────────────────────────────

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

        // ── Helpers ───────────────────────────────────────────────────────────

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

    // ── Broadcast receiver for dynamic interval updates ───────────────────────
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
                _service.UpdateInterval(intent.GetIntExtra("new_interval", 60000));
        }
    }
}