using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Timers;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Preferences;
using AndroidX.Core.App;
using Finder.Droid.Managers;
using Finder.Droid.Receivers;
using Finder.Models;
using Newtonsoft.Json;

// ── Namespace aliases — resolves all ambiguity between Gms and Android.Locations ──
using GmsLocation = Android.Gms.Location;      // FusedLocationProvider namespace
using DroidLocation = Android.Locations;          // Raw GPS / LocationManager namespace
using AndroidLocation = Android.Locations.Location; // shared Location object

namespace Finder.Droid.Services
{
    /// <summary>
    /// Background foreground service that tracks the device location and
    /// forwards it to Telegram.
    ///
    /// GPS provider can be switched live via Telegram command /gpsprovider:
    ///   fused — FusedLocationProviderClient (default, battery-efficient)
    ///   raw   — Android LocationManager GpsProvider (maximum accuracy)
    ///
    /// The active provider is persisted in SharedPreferences so the choice
    /// survives service restarts and device reboots.
    /// </summary>
    [Service(ForegroundServiceType = Android.Content.PM.ForegroundService.TypeLocation)]
    public class BackgroundLocationService : Service, DroidLocation.ILocationListener
    {
        // ── Static state ──────────────────────────────────────────────────────
        public static bool IsStoppingByUserRequest = false;
        public static bool IsRunning = false;

        // ── Notification constants ─────────────────────────────────────────────
        public const int SERVICE_NOTIFICATION_ID = 1001;
        private const string NOTIFICATION_CHANNEL_ID = "finder_location_channel";
        private const string GPS_ALERT_CHANNEL_ID = "finder_gps_alert_channel";
        private const int GPS_ALERT_NOTIFICATION_ID = 2001;

        // ── SharedPreferences keys ─────────────────────────────────────────────
        private const string PREF_KEY_RUNNING = "is_tracking_service_running";
        private const string PREF_KEY_SENDING_PAUSED = "is_telegram_sending_paused";

        /// <summary>Stores "fused" (default) or "raw". Read by TelegramCommandHandler.</summary>
        public const string PREF_KEY_GPS_PROVIDER = "gps_provider_mode";
        public const string PREF_KEY_SENDING_PAUSED_PUBLIC = "is_telegram_sending_paused";

        // ── Broadcast actions ──────────────────────────────────────────────────
        public const string ACTION_UPDATE_INTERVAL = "com.finder.UPDATE_INTERVAL";
        public const string ACTION_SET_SENDING_PAUSED = "com.finder.SET_TELEGRAM_PAUSED";
        public const string ACTION_SET_GPS_PROVIDER = "com.finder.SET_GPS_PROVIDER";

        // ── GPS update thresholds ──────────────────────────────────────────────
        private const int STATIONARY_UPDATE_INTERVAL_MS = 60_000;
        private const int MOVING_UPDATE_INTERVAL_MS = 20_000;
        private const float SIGNIFICANT_MOVEMENT_METERS = 25f;

        // ── GPS provider fields ────────────────────────────────────────────────
        private string _gpsProviderMode = "fused"; // default

        // Fused (battery-efficient, default)
        private GmsLocation.FusedLocationProviderClient _fusedClient;
        private FusedLocationCallback _fusedCallback;

        // Raw GPS (ILocationListener implemented by this class)
        private DroidLocation.LocationManager _locationManager;

        // ── Common location state ──────────────────────────────────────────────
        private AndroidLocation _lastSignificantLocation;
        private int _currentUpdateInterval = STATIONARY_UPDATE_INTERVAL_MS;
        private string _currentLocation = "Unknown";

        // ── Other service state ────────────────────────────────────────────────
        private Timer _telegramTimer;
        private Timer _dailyGeoJsonTimer;
        private HttpClient _httpClient;

        private Android.OS.PowerManager.WakeLock _wakeLock;
        private bool _isProcessingLocation = false;
        private int _updateCounter = 0;

        private bool _isTelegramSendingPaused = false;

        private GeoJsonManager _geoJsonManager;
        private TelegramCommandHandler _commandHandler;
        private IntervalUpdateReceiver _intervalUpdateReceiver;

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

        public override Android.OS.IBinder OnBind(Intent intent) => null;

        // ═════════════════════════════════════════════════════════════════════
        // Service lifecycle
        // ═════════════════════════════════════════════════════════════════════

        public override StartCommandResult OnStartCommand(
            Intent intent, StartCommandFlags flags, int startId)
        {
            IsRunning = true;
            SetRunningPreference(true);

            bool isExplicitUserStart =
                intent?.GetBooleanExtra("explicit_user_start", false) ?? false;

            LoadSettings();

            var prefs = PreferenceManager.GetDefaultSharedPreferences(this);
            _isTelegramSendingPaused = prefs.GetBoolean(PREF_KEY_SENDING_PAUSED, false);

            // Restore saved GPS provider (default: "fused")
            _gpsProviderMode = prefs.GetString(PREF_KEY_GPS_PROVIDER, "fused") ?? "fused";

            _geoJsonManager = new GeoJsonManager(this);

            if (_intervalUpdateReceiver == null)
            {
                _intervalUpdateReceiver = new IntervalUpdateReceiver(this);
                var filter = new IntentFilter();
                filter.AddAction(ACTION_UPDATE_INTERVAL);
                filter.AddAction(ACTION_SET_SENDING_PAUSED);
                filter.AddAction(ACTION_SET_GPS_PROVIDER);
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

            StartLocationUpdates();

            return StartCommandResult.Sticky;
        }

        public override void OnDestroy()
        {
            if (IsStoppingByUserRequest)
                SetRunningPreference(false);

            IsRunning = false;
            base.OnDestroy();

            if (_intervalUpdateReceiver != null)
            {
                try { UnregisterReceiver(_intervalUpdateReceiver); }
                catch { }
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

            StopLocationUpdates();

            try { _geoJsonManager?.FlushBuffer(); }
            catch { }

            _httpClient?.Dispose();
            _httpClient = null;

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
                catch { }
            }
        }

        /// <summary>
        /// Uses SetExact (not SetExactAndAllowWhileIdle) — no Doze bypass.
        /// WatchdogJobService provides the 15-min safety net.
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
                long triggerAt = SystemClock.ElapsedRealtime() + 5000;

                alarmManager.SetExact(AlarmType.ElapsedRealtime, triggerAt, pendingIntent);
            }
            catch { }
        }

        // ═════════════════════════════════════════════════════════════════════
        // GPS provider switching — public API called by IntervalUpdateReceiver
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Switches the active GPS provider at runtime without restarting the service.
        /// Persists the new value to SharedPreferences immediately.
        /// </summary>
        public void SetGpsProvider(string provider)
        {
            string normalized = provider?.ToLower()?.Trim() ?? "fused";
            if (normalized != "fused" && normalized != "raw") return;
            if (normalized == _gpsProviderMode) return;

            StopLocationUpdates();
            _gpsProviderMode = normalized;

            var editor = PreferenceManager.GetDefaultSharedPreferences(this).Edit();
            editor.PutString(PREF_KEY_GPS_PROVIDER, normalized);
            editor.Apply();

            StartLocationUpdates();

            string label = normalized == "fused"
                ? "Fused (battery saver)"
                : "Raw GPS (max accuracy)";
            UpdateNotification("Finder is active", $"GPS provider: {label}");
        }

        // ═════════════════════════════════════════════════════════════════════
        // Location updates — start / stop / restart (provider-aware)
        // ═════════════════════════════════════════════════════════════════════

        private void StartLocationUpdates()
        {
            if (_gpsProviderMode == "raw")
                StartRawGpsUpdates();
            else
                StartFusedUpdates();
        }

        private void StopLocationUpdates()
        {
            StopFusedUpdates();
            StopRawGpsUpdates();
        }

        private void RestartLocationUpdates()
        {
            StopLocationUpdates();
            StartLocationUpdates();
        }

        // ── FusedLocationProvider (default — battery-efficient) ───────────────

        private void StartFusedUpdates()
        {
            if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation)
                != Android.Content.PM.Permission.Granted) return;
            try
            {
                _fusedCallback = new FusedLocationCallback(this);
                _fusedClient = GmsLocation.LocationServices
                                    .GetFusedLocationProviderClient(this);

                var req = BuildFusedRequest(_currentUpdateInterval);
                _fusedClient.RequestLocationUpdates(
                    req, _fusedCallback, Android.OS.Looper.MainLooper);
            }
            catch (Exception ex)
            {
                UpdateNotification("GPS Error (Fused)", ex.Message);
            }
        }

        private void StopFusedUpdates()
        {
            try
            {
                if (_fusedClient != null && _fusedCallback != null)
                    _fusedClient.RemoveLocationUpdates(_fusedCallback);
            }
            catch { }
            finally
            {
                _fusedClient = null;
                _fusedCallback = null;
            }
        }

        /// <summary>Builds a FusedLocationProvider request — fully qualified to avoid ambiguity.</summary>
        private static GmsLocation.LocationRequest BuildFusedRequest(int intervalMs)
        {
            var req = new GmsLocation.LocationRequest();
            req.SetPriority(GmsLocation.LocationRequest.PriorityBalancedPowerAccuracy);
            req.SetInterval(intervalMs);
            req.SetFastestInterval(intervalMs / 2);
            req.SetSmallestDisplacement(SIGNIFICANT_MOVEMENT_METERS);
            return req;
        }

        // ── Raw GPS / ILocationListener (maximum accuracy) ────────────────────

        private void StartRawGpsUpdates()
        {
            if (CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation)
                != Android.Content.PM.Permission.Granted) return;
            try
            {
                _locationManager = GetSystemService(LocationService)
                                       as DroidLocation.LocationManager;

                // Cast 'this' explicitly — avoids CS1503 ambiguity error
                _locationManager?.RequestLocationUpdates(
                    DroidLocation.LocationManager.GpsProvider,
                    _currentUpdateInterval,
                    SIGNIFICANT_MOVEMENT_METERS,
                    (DroidLocation.ILocationListener)this);

                var lastKnown = _locationManager
                    ?.GetLastKnownLocation(DroidLocation.LocationManager.GpsProvider);
                if (lastKnown != null)
                {
                    _lastSignificantLocation = lastKnown;
                    HandleLocationUpdate(lastKnown);
                }
            }
            catch (Exception ex)
            {
                UpdateNotification("GPS Error (Raw)", ex.Message);
            }
        }

        private void StopRawGpsUpdates()
        {
            try
            {
                // Explicit cast — avoids CS1503 ambiguity with RemoveUpdates overloads
                _locationManager?.RemoveUpdates((DroidLocation.ILocationListener)this);
            }
            catch { }
            finally { _locationManager = null; }
        }

        // ── DroidLocation.ILocationListener callbacks (raw GPS only) ──────────

        /// <summary>Called by Android when a raw GPS fix is available.</summary>
        public void OnLocationChanged(AndroidLocation location)
            => HandleLocationUpdate(location);

        /// <summary>Called when the GPS provider is disabled (raw mode only).</summary>
        public void OnProviderDisabled(string provider)
        {
            if (provider != DroidLocation.LocationManager.GpsProvider) return;

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

        /// <summary>Called when the GPS provider is re-enabled (raw mode only).</summary>
        public void OnProviderEnabled(string provider)
        {
            if (provider != DroidLocation.LocationManager.GpsProvider) return;

            UpdateNotification("Finder is active", "GPS re-enabled — resuming tracking.");

            _ = Task.Run(async () =>
            {
                await SendMessageToTelegramAsync(
                    "✅ *GPS Enabled*\n" +
                    "Location tracking has resumed automatically.");
            });

            StartRawGpsUpdates();
        }

        public void OnStatusChanged(
            string provider,
            DroidLocation.Availability status,
            Android.OS.Bundle extras)
        { }

        // ═════════════════════════════════════════════════════════════════════
        // Shared location update handler (called by BOTH providers)
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Processes a location fix from either FusedLocationProvider or raw GPS.
        /// Identical logic regardless of provider — single entry point.
        /// </summary>
        public void HandleLocationUpdate(AndroidLocation location)
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

                // Buffer the point — FlushBuffer() writes to disk every 5 points
                _ = Task.Run(() =>
                {
                    try { _geoJsonManager.AddLocationPoint(location, "automatic"); }
                    catch { }
                });

                // Adaptive interval: faster when moving, slower when stationary
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
        /// Called by FusedLocationCallback when location becomes unavailable.
        /// Equivalent to OnProviderDisabled for fused mode.
        /// </summary>
        public void HandleLocationUnavailable()
        {
            _currentLocation = "Unknown";
            UpdateNotification("Finder — Location Unavailable", "Check GPS settings.");

            _ = Task.Run(async () =>
            {
                await SendMessageToTelegramAsync(
                    "⚠️ *Location Unavailable*\n" +
                    "Could not get a fix — GPS may be disabled.\n" +
                    "Send /enablelocation to request GPS activation.");
            });

            ShowGpsDisabledNotification();
        }

        // ═════════════════════════════════════════════════════════════════════
        // Interval / pause public methods (called from IntervalUpdateReceiver)
        // ═════════════════════════════════════════════════════════════════════

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
                    $"Send interval updated to {newIntervalMs} ms");
            }
            catch { }
        }

        public void SetTelegramSendingPaused(bool paused)
        {
            _isTelegramSendingPaused = paused;

            var editor = PreferenceManager.GetDefaultSharedPreferences(this).Edit();
            editor.PutBoolean(PREF_KEY_SENDING_PAUSED, paused);
            editor.Apply();

            if (paused)
                UpdateNotification("Finder is active — sends paused",
                    "GPS recording continues · Telegram sends are paused");
            else
                UpdateNotification("Finder is active", "Telegram location sends resumed");
        }

        // ═════════════════════════════════════════════════════════════════════
        // GPS status helper
        // ═════════════════════════════════════════════════════════════════════

        private bool IsGpsEnabled()
        {
            try
            {
                var lm = (DroidLocation.LocationManager)GetSystemService(LocationService);
                return lm?.IsProviderEnabled(DroidLocation.LocationManager.GpsProvider) == true;
            }
            catch { return false; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // WakeLock
        // ═════════════════════════════════════════════════════════════════════

        private void AcquireWakeLock()
        {
            if (_wakeLock == null)
            {
                var pm = (Android.OS.PowerManager)GetSystemService(PowerService);
                _wakeLock = pm.NewWakeLock(
                    Android.OS.WakeLockFlags.Partial, "Finder::LocationWakeLock");
            }
            // 3 s is ample for string format + buffer add (~5 ms actual work)
            if (!_wakeLock.IsHeld && _isProcessingLocation)
                _wakeLock.Acquire(3000);
        }

        private void ReleaseWakeLock()
        {
            if (_wakeLock?.IsHeld == true && !_isProcessingLocation)
                _wakeLock.Release();
        }

        // ═════════════════════════════════════════════════════════════════════
        // Telegram timer
        // ═════════════════════════════════════════════════════════════════════

        private void InitializeTelegramTimer()
        {
            StopTimer(ref _telegramTimer);
            int intervalMs = int.TryParse(_interval, out int iv) ? iv : 60_000;
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
                    if (_isTelegramSendingPaused)
                    {
                        UpdateNotification("Finder is active — sends paused",
                            $"Paused · {DateTime.Now:HH:mm:ss} · {_currentLocation}");
                        return;
                    }

                    bool inMovingMode = _currentUpdateInterval == MOVING_UPDATE_INTERVAL_MS;

                    if (_currentLocation != "Unknown")
                    {
                        var parts = _currentLocation.Split(',');
                        if (parts.Length == 2 &&
                            double.TryParse(parts[0],
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out _) &&
                            double.TryParse(parts[1],
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out _))
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

        // ═════════════════════════════════════════════════════════════════════
        // Daily GeoJSON report
        // ═════════════════════════════════════════════════════════════════════

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
            finally { SetupDailyGeoJsonTimer(); }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Telegram API helpers
        // ═════════════════════════════════════════════════════════════════════

        private async Task SendLocationToTelegramAsync()
        {
            var parts = _currentLocation.Split(',');
            if (parts.Length != 2) return;

            if (!double.TryParse(parts[0],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double lat)) return;
            if (!double.TryParse(parts[1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double lon)) return;
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
                string url = $"https://api.telegram.org/bot{_telegramBotToken}/sendMessage" +
                             $"?chat_id={_chatId}" +
                             $"&text={Uri.EscapeDataString(message)}" +
                             $"&parse_mode=Markdown";
                await _httpClient.GetStringAsync(url);
            }
            catch { }
        }

        private async Task SendFileToTelegramAsync(
            string filePath, string caption, AppSettings settings)
        {
            try
            {
                using var content = new MultipartFormDataContent();
                using var fileStream = File.OpenRead(filePath);
                using var fileContent = new System.Net.Http.StreamContent(fileStream);
                fileContent.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                content.Add(fileContent, "document", Path.GetFileName(filePath));

                string url =
                    $"https://api.telegram.org/bot{settings.BotToken}" +
                    $"/sendDocument?chat_id={settings.ChatId}" +
                    $"&caption={Uri.EscapeDataString(caption)}";
                await _httpClient.PostAsync(url, content);
            }
            catch { }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Settings helpers
        // ═════════════════════════════════════════════════════════════════════

        private void LoadSettings()
        {
            var s = LoadSettingsFromFile();
            if (s == null) return;
            _telegramBotToken = s.BotToken;
            _chatId = s.ChatId;
            _interval = s.Interval;
        }

        private AppSettings LoadSettingsFromFile()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                    return JsonConvert.DeserializeObject<AppSettings>(
                               File.ReadAllText(_settingsFilePath))
                           ?? new AppSettings();
            }
            catch { }
            return new AppSettings();
        }

        private void SaveSettingsToFile(AppSettings settings)
        {
            try { File.WriteAllText(_settingsFilePath, JsonConvert.SerializeObject(settings)); }
            catch { }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Notification helpers
        // ═════════════════════════════════════════════════════════════════════

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            try
            {
                var channel = new NotificationChannel(
                    NOTIFICATION_CHANNEL_ID, "Finder Location", NotificationImportance.Low)
                { Description = "Persistent tracking notification" };
                ((NotificationManager)GetSystemService(NotificationService))
                    ?.CreateNotificationChannel(channel);
            }
            catch { }
        }

        private void CreateGpsAlertNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            try
            {
                var channel = new NotificationChannel(
                    GPS_ALERT_CHANNEL_ID, "GPS Alerts", NotificationImportance.High)
                { Description = "Alerts for GPS state changes" };
                channel.EnableVibration(true);
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

        private void ShowGpsDisabledNotification()
        {
            try
            {
                var intent = new Intent(
                    Android.Provider.Settings.ActionLocationSourceSettings);
                intent.AddFlags(ActivityFlags.NewTask);

                var pendingFlags = Build.VERSION.SdkInt >= BuildVersionCodes.M
                    ? PendingIntentFlags.Immutable
                    : (PendingIntentFlags)0;

                var pendingIntent = PendingIntent.GetActivity(this, 0, intent, pendingFlags);

                var builder = new NotificationCompat.Builder(this, GPS_ALERT_CHANNEL_ID)
                    .SetContentTitle("Location Unavailable")
                    .SetContentText("Tap to check location settings")
                    .SetSmallIcon(Android.Resource.Drawable.IcDialogMap)
                    .SetPriority(NotificationCompat.PriorityHigh)
                    .SetAutoCancel(true)
                    .SetContentIntent(pendingIntent);

                ((NotificationManager)GetSystemService(NotificationService))
                    ?.Notify(GPS_ALERT_NOTIFICATION_ID, builder.Build());
            }
            catch { }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Misc helpers
        // ═════════════════════════════════════════════════════════════════════

        private void SetRunningPreference(bool running)
        {
            var editor = PreferenceManager.GetDefaultSharedPreferences(this).Edit();
            editor.PutBoolean(PREF_KEY_RUNNING, running);
            editor.Apply();
        }

        private static void StopTimer(ref Timer timer)
        {
            if (timer == null) return;
            timer.Stop();
            timer.Dispose();
            timer = null;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // FusedLocationCallback
    // Receives fixes from FusedLocationProviderClient when in "fused" mode.
    // ═════════════════════════════════════════════════════════════════════════

    internal class FusedLocationCallback : Android.Gms.Location.LocationCallback
    {
        private readonly BackgroundLocationService _service;

        public FusedLocationCallback(BackgroundLocationService service)
            => _service = service;

        public override void OnLocationResult(Android.Gms.Location.LocationResult result)
        {
            base.OnLocationResult(result);
            var location = result?.LastLocation;
            if (location != null)
                _service.HandleLocationUpdate(location);
        }

        public override void OnLocationAvailability(
            Android.Gms.Location.LocationAvailability availability)
        {
            base.OnLocationAvailability(availability);
            if (availability != null && !availability.IsLocationAvailable)
                _service.HandleLocationUnavailable();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // IntervalUpdateReceiver
    // Handles all runtime broadcast commands sent to the running service.
    // ═════════════════════════════════════════════════════════════════════════

    public class IntervalUpdateReceiver : BroadcastReceiver
    {
        private readonly BackgroundLocationService _service;

        public IntervalUpdateReceiver(BackgroundLocationService service)
            => _service = service;

        public override void OnReceive(Context context, Intent intent)
        {
            if (intent == null) return;

            switch (intent.Action)
            {
                case BackgroundLocationService.ACTION_UPDATE_INTERVAL:
                    int newInterval = intent.GetIntExtra("new_interval", 60_000);
                    _service.UpdateInterval(newInterval);
                    break;

                case BackgroundLocationService.ACTION_SET_SENDING_PAUSED:
                    bool paused = intent.GetBooleanExtra("paused", false);
                    _service.SetTelegramSendingPaused(paused);
                    break;

                case BackgroundLocationService.ACTION_SET_GPS_PROVIDER:
                    string provider = intent.GetStringExtra("provider") ?? "fused";
                    _service.SetGpsProvider(provider);
                    break;
            }
        }
    }
}