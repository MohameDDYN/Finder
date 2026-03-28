using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Locations;
using Android.OS;
using Android.Preferences;
using AndroidX.Core.App;
using Finder.Droid.Services;
using Finder.Models;
using Newtonsoft.Json;
using Xamarin.Essentials;

namespace Finder.Droid.Managers
{
    public class TelegramCommandHandler
    {
        private static DateTime _lastIntervalUpdate = DateTime.MinValue;
        private static DateTime _lastStartupMessage = DateTime.MinValue;
        private static DateTime _lastRestartCommand = DateTime.MinValue;

        private const int INTERVAL_COOLDOWN_S = 30;
        private const int STARTUP_COOLDOWN_S = 60;
        private const int RESTART_COOLDOWN_S = 120;

        private const string GPS_ALERT_CHANNEL_ID = "finder_gps_alert_channel";
        private const int GPS_ALERT_NOTIFICATION_ID = 2001;

        private readonly string _settingsFilePath;
        private readonly Context _context;
        private readonly GeoJsonManager _geoJsonManager;
        private HttpClient _httpClient;
        private Timer _pollTimer;
        private long _lastUpdateId = 0;

        public TelegramCommandHandler(Context context)
        {
            _context = context;
            _settingsFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                "secure_settings.json");
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _geoJsonManager = new GeoJsonManager(context);

            CreateGpsAlertNotificationChannel();
        }

        public void Start(bool sendStartupMessage = false)
        {
            try
            {
                var settings = LoadSettings();
                if (string.IsNullOrEmpty(settings.BotToken) ||
                    string.IsNullOrEmpty(settings.ChatId)) return;

                _pollTimer = new Timer(PollForCommands, null, 0, 10000);

                if (sendStartupMessage)
                {
                    var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
                    bool suppress = prefs.GetBoolean("suppress_next_startup_message", false);

                    if (suppress)
                    {
                        var editor = prefs.Edit();
                        editor.PutBoolean("suppress_next_startup_message", false);
                        editor.Apply();
                    }
                    else if ((DateTime.Now - _lastStartupMessage).TotalSeconds > STARTUP_COOLDOWN_S)
                    {
                        _lastStartupMessage = DateTime.Now;
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(2000);
                            await SendTelegramMessageAsync(
                                settings.BotToken, settings.ChatId,
                                "🤖 Finder service started");
                        });
                    }
                }
            }
            catch { }
        }

        public void Stop()
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
        }

        private async void PollForCommands(object state)
        {
            try
            {
                var settings = LoadSettings();
                if (string.IsNullOrEmpty(settings.BotToken) ||
                    string.IsNullOrEmpty(settings.ChatId)) return;

                string url = $"https://api.telegram.org/bot{settings.BotToken}" +
                              $"/getUpdates?offset={_lastUpdateId + 1}&timeout=5";
                string json = await _httpClient.GetStringAsync(url);
                var resp = JsonConvert.DeserializeObject<TelegramUpdateResponse>(json);

                if (resp?.Result == null || resp.Result.Length == 0) return;

                foreach (var update in resp.Result)
                {
                    if (update.UpdateId > _lastUpdateId)
                        _lastUpdateId = update.UpdateId;

                    if (update.Message?.Chat?.Id.ToString() != settings.ChatId) continue;

                    string text = update.Message?.Text;
                    if (!string.IsNullOrEmpty(text) && text.StartsWith("/"))
                        await ProcessCommandAsync(text, settings);
                }
            }
            catch { }
        }

        private async Task ProcessCommandAsync(string command, AppSettings currentSettings)
        {
            try
            {
                string[] parts = command.Split(new[] { ' ' }, 2);
                string cmd = parts[0].ToLower().Trim();
                string param = parts.Length > 1 ? parts[1].Trim() : null;

                string response;

                switch (cmd)
                {
                    case "/interval":
                        if ((DateTime.Now - _lastIntervalUpdate).TotalSeconds < INTERVAL_COOLDOWN_S)
                        {
                            response = $"⏳ Cooldown active. Wait {INTERVAL_COOLDOWN_S}s between changes.";
                            break;
                        }
                        if (!string.IsNullOrEmpty(param) &&
                            int.TryParse(param, out int ivMs) && ivMs >= 5000)
                        {
                            _lastIntervalUpdate = DateTime.Now;
                            currentSettings.Interval = ivMs.ToString();
                            SaveSettings(currentSettings);
                            await SecureStorage.SetAsync("Interval", ivMs.ToString());

                            var bIntent = new Intent("com.finder.UPDATE_INTERVAL");
                            bIntent.PutExtra("new_interval", ivMs);
                            _context.SendBroadcast(bIntent);

                            response = $"⏱ Interval set to {ivMs} ms";
                        }
                        else
                        {
                            response = "❌ Usage: /interval [milliseconds] (min 5000)";
                        }
                        break;

                    case "/status":
                        var files = _geoJsonManager.GetAvailableDataFiles();
                        response = $"📍 Status\n" +
                                    $"Tracking: {(IsServiceRunning() ? "✅ Active" : "❌ Stopped")}\n" +
                                    $"Token: {MaskToken(currentSettings.BotToken)}\n" +
                                    $"Chat ID: {currentSettings.ChatId}\n" +
                                    $"Interval: {currentSettings.Interval} ms\n" +
                                    $"Data files: {files.Count}\n" +
                                    $"Device time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                        break;

                    case "/start":
                        StartService();
                        response = "✅ Location tracking started";
                        break;

                    case "/stop":
                        StopService();
                        response = "⏹ Location tracking stopped";
                        break;

                    case "/restart":
                        if ((DateTime.Now - _lastRestartCommand).TotalSeconds < RESTART_COOLDOWN_S)
                        {
                            int remaining = RESTART_COOLDOWN_S -
                                (int)(DateTime.Now - _lastRestartCommand).TotalSeconds;
                            response = $"⏳ Please wait {remaining}s before restarting again.";
                            break;
                        }
                        _lastRestartCommand = DateTime.Now;

                        var suppressPrefs = PreferenceManager.GetDefaultSharedPreferences(_context);
                        var suppressEditor = suppressPrefs.Edit();
                        suppressEditor.PutBoolean("suppress_next_startup_message", true);
                        suppressEditor.Apply();

                        StopService();
                        await Task.Delay(2000);
                        StartService();
                        response = "🔄 Service restarted";
                        break;

                    case "/token":
                        if (!string.IsNullOrEmpty(param))
                        {
                            currentSettings.BotToken = param;
                            SaveSettings(currentSettings);
                            response = $"🔑 Token updated: {MaskToken(param)}";
                        }
                        else
                        {
                            response = "❌ Usage: /token [your_bot_token]";
                        }
                        break;

                    case "/chatid":
                        if (!string.IsNullOrEmpty(param))
                        {
                            currentSettings.ChatId = param;
                            SaveSettings(currentSettings);
                            response = $"💬 Chat ID updated: {param}";
                        }
                        else
                        {
                            response = "❌ Usage: /chatid [your_chat_id]";
                        }
                        break;

                    case "/today":
                        await SendGeoJsonReport(DateTime.Today, currentSettings);
                        response = null;
                        break;

                    case "/yesterday":
                        await SendGeoJsonReport(DateTime.Today.AddDays(-1), currentSettings);
                        response = null;
                        break;

                    case "/report":
                        if (!string.IsNullOrEmpty(param) &&
                            DateTime.TryParseExact(param, "yyyy-MM-dd",
                                null,
                                System.Globalization.DateTimeStyles.None,
                                out DateTime reportDate))
                        {
                            await SendGeoJsonReport(reportDate, currentSettings);
                            response = null;
                        }
                        else
                        {
                            response = "❌ Usage: /report YYYY-MM-DD";
                        }
                        break;

                    case "/files":
                        var dataFiles = _geoJsonManager.GetAvailableDataFiles();
                        if (dataFiles.Count == 0)
                        {
                            response = "📂 No data files found.";
                        }
                        else
                        {
                            response = $"📂 Data files ({dataFiles.Count}):\n" +
                                       string.Join("\n", dataFiles.Take(20));
                            if (dataFiles.Count > 20)
                                response += $"\n…and {dataFiles.Count - 20} more";
                        }
                        break;

                    case "/cleanup":
                        int keepDays = int.TryParse(param, out int d) && d > 0 ? d : 30;
                        int before = _geoJsonManager.GetAvailableDataFiles().Count;
                        await _geoJsonManager.CleanupOldFiles(keepDays);
                        int after = _geoJsonManager.GetAvailableDataFiles().Count;
                        response = $"🧹 Cleanup done · Removed {before - after} files older than {keepDays} days";
                        break;

                    case "/gpsstatus":
                        bool gpsOn = IsGpsEnabled();
                        bool svcRunning = IsServiceRunning();
                        int battery = GetBatteryLevel();

                        response = "📡 *GPS Status*\n" +
                                   $"GPS Provider: {(gpsOn ? "✅ Enabled" : "❌ Disabled")}\n" +
                                   $"Tracking Service: {(svcRunning ? "✅ Running" : "⏹ Stopped")}\n" +
                                   $"Battery: {(battery >= 0 ? $"{battery}%" : "Unknown")}\n" +
                                   $"Time: {DateTime.Now:HH:mm:ss}";

                        if (!gpsOn)
                            response += "\n\n💡 Send /enablelocation to request GPS activation.";
                        break;

                    case "/enablelocation":
                        bool isAlreadyOn = IsGpsEnabled();

                        if (isAlreadyOn)
                        {
                            response = "✅ GPS is already enabled on this device.";
                        }
                        else
                        {
                            ShowEnableLocationNotification();
                            response = "📲 *Action required on the device*\n\n" +
                                       "A notification has been sent to the phone.\n" +
                                       "Tap it to open Location Settings and enable GPS.\n\n" +
                                       "⚠️ Android does not allow apps to silently enable GPS — " +
                                       "one tap by the user is required.";
                        }
                        break;

                    case "/cmd":
                    default:
                        response = "📋 *Available commands:*\n" +
                                   "/interval [ms] — Set update interval\n" +
                                   "/status — Current status\n" +
                                   "/start — Start tracking\n" +
                                   "/stop — Stop tracking\n" +
                                   "/restart — Restart service\n" +
                                   "/token [token] — Change bot token\n" +
                                   "/chatid [id] — Change chat ID\n" +
                                   "/today — Today's GeoJSON report\n" +
                                   "/yesterday — Yesterday's report\n" +
                                   "/report YYYY-MM-DD — Specific date report\n" +
                                   "/files — List data files\n" +
                                   "/cleanup [days] — Delete old files\n" +
                                   "/gpsstatus — Check if GPS is on or off\n" +
                                   "/enablelocation — Send notification to enable GPS";
                        break;
                }

                if (response != null)
                    await SendTelegramMessageAsync(
                        currentSettings.BotToken, currentSettings.ChatId, response);
            }
            catch { }
        }

        private bool IsGpsEnabled()
        {
            try
            {
                var locationManager =
                    (LocationManager)_context.GetSystemService(Context.LocationService);
                return locationManager != null &&
                       locationManager.IsProviderEnabled(LocationManager.GpsProvider);
            }
            catch
            {
                return false;
            }
        }

        private int GetBatteryLevel()
        {
            try
            {
                var filter = new IntentFilter(Intent.ActionBatteryChanged);
                var batteryStatus = _context.RegisterReceiver(null, filter);
                int level = batteryStatus?.GetIntExtra(BatteryManager.ExtraLevel, -1) ?? -1;
                int scale = batteryStatus?.GetIntExtra(BatteryManager.ExtraScale, -1) ?? -1;
                if (level < 0 || scale <= 0) return -1;
                return (int)((level / (float)scale) * 100);
            }
            catch
            {
                return -1;
            }
        }

        private void ShowEnableLocationNotification()
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
                    _context, GPS_ALERT_NOTIFICATION_ID, settingsIntent, pendingFlags);

                var notification = new NotificationCompat.Builder(_context, GPS_ALERT_CHANNEL_ID)
                    .SetContentTitle("📍 Enable GPS — Finder")
                    .SetContentText("Tap here to open Location Settings and turn on GPS.")
                    .SetStyle(new NotificationCompat.BigTextStyle()
                        .BigText("Finder needs GPS to track your location.\n" +
                                 "Tap this notification to open Location Settings " +
                                 "and enable GPS with one tap."))
                    .SetSmallIcon(Android.Resource.Drawable.IcDialogMap)
                    .SetContentIntent(pendingIntent)
                    .SetAutoCancel(true)
                    .SetPriority(NotificationCompat.PriorityHigh)
                    .Build();

                NotificationManagerCompat
                    .From(_context)
                    .Notify(GPS_ALERT_NOTIFICATION_ID, notification);
            }
            catch { }
        }

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
                    Description = "Alerts sent when GPS needs to be enabled remotely."
                };

                channel.EnableVibration(true);

                ((NotificationManager)_context.GetSystemService(Context.NotificationService))
                    ?.CreateNotificationChannel(channel);
            }
            catch { }
        }

        private async Task SendGeoJsonReport(DateTime date, AppSettings settings)
        {
            try
            {
                string geoJson = await _geoJsonManager.GenerateGeoJsonForDate(date);

                if (string.IsNullOrEmpty(geoJson))
                {
                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        $"📭 No data found for {date:yyyy-MM-dd}");
                    return;
                }

                string tempPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                    $"report_{date:yyyy-MM-dd}.geojson");

                File.WriteAllText(tempPath, geoJson);

                string caption = $"📍 Location report for {date:yyyy-MM-dd}";

                using (var form = new MultipartFormDataContent())
                {
                    var bytes = File.ReadAllBytes(tempPath);
                    var content = new ByteArrayContent(bytes);
                    content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("application/geo+json");
                    form.Add(content, "document", Path.GetFileName(tempPath));
                    form.Add(new StringContent(settings.ChatId), "chat_id");
                    form.Add(new StringContent(caption), "caption");

                    await _httpClient.PostAsync(
                        $"https://api.telegram.org/bot{settings.BotToken}/sendDocument", form);
                }
            }
            catch { }
        }

        private async Task SendTelegramMessageAsync(string token, string chatId, string message)
        {
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chatId)) return;
            try
            {
                string url = $"https://api.telegram.org/bot{token}" +
                             $"/sendMessage?chat_id={chatId}" +
                             $"&text={Uri.EscapeDataString(message)}&parse_mode=Markdown";
                await _httpClient.GetStringAsync(url);
            }
            catch { }
        }

        private bool IsServiceRunning()
        {
            var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
            return prefs.GetBoolean("is_tracking_service_running", false);
        }

        private void StartService()
        {
            try
            {
                var intent = new Intent(_context, typeof(BackgroundLocationService));
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    _context.StartForegroundService(intent);
                else
                    _context.StartService(intent);

                var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
                var editor = prefs.Edit();
                editor.PutBoolean("is_tracking_service_running", true);
                editor.Apply();
            }
            catch { }
        }

        private void StopService()
        {
            try
            {
                BackgroundLocationService.IsStoppingByUserRequest = true;
                var intent = new Intent(_context, typeof(BackgroundLocationService));
                _context.StopService(intent);

                var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
                var editor = prefs.Edit();
                editor.PutBoolean("is_tracking_service_running", false);
                editor.Apply();

                Task.Delay(2000).ContinueWith(_ =>
                {
                    BackgroundLocationService.IsStoppingByUserRequest = false;
                });
            }
            catch { }
        }

        private AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                    return JsonConvert.DeserializeObject<AppSettings>(
                        File.ReadAllText(_settingsFilePath)) ?? new AppSettings();
            }
            catch { }
            return new AppSettings();
        }

        private void SaveSettings(AppSettings settings)
        {
            try
            {
                File.WriteAllText(_settingsFilePath, JsonConvert.SerializeObject(settings));
            }
            catch { }
        }

        private string MaskToken(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length < 8) return "Not set";
            return token.Substring(0, 6) + "•••" + token.Substring(token.Length - 4);
        }
    }
}