using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Preferences;
using AndroidX.Core.App;
using Finder.Droid.Services;
using Finder.Models;
using Newtonsoft.Json;
using Xamarin.Essentials;
using Xamarin.Forms;

// ── Alias to avoid ambiguity with Android.Gms.Location.ILocationListener ──────
using DroidLocation = Android.Locations;
using AndroidLocation = Android.Locations.Location;

namespace Finder.Droid.Managers
{
    public class TelegramCommandHandler
    {
        // ── Cooldown timestamps ───────────────────────────────────────────────
        private static DateTime _lastIntervalUpdate = DateTime.MinValue;
        private static DateTime _lastStartupMessage = DateTime.MinValue;
        private static DateTime _lastRestartCommand = DateTime.MinValue;
        private static DateTime _lastUpdateCommand = DateTime.MinValue;

        private const int INTERVAL_COOLDOWN_S = 30;
        private const int STARTUP_COOLDOWN_S = 60;
        private const int RESTART_COOLDOWN_S = 120;
        private const int UPDATE_COOLDOWN_S = 60;

        // ── Poll-interval defaults & limits ───────────────────────────────────
        private const int DEFAULT_POLL_INTERVAL_MS = 60_000;
        private const int MIN_POLL_INTERVAL_MS = 10_000;

        // ── SharedPreferences keys ────────────────────────────────────────────
        private const string LAST_UPDATE_ID_KEY = "telegram_last_update_id";
        private const string PREF_POLL_INTERVAL_MS = "telegram_poll_interval_ms";
        private const string PREF_POLLING_ENABLED = "telegram_polling_enabled";

        // ── Pending update — stored when "Install Unknown Apps" permission is
        //    missing at /update time so OnResume can auto-resume the download
        //    as soon as the user returns from the settings screen. ─────────────
        private const string PREF_PENDING_UPDATE_VERSION = "pending_update_version";
        private const string PREF_PENDING_UPDATE_URL = "pending_update_url";

        // ── Notification channels ─────────────────────────────────────────────
        private const string GPS_ALERT_CHANNEL_ID = "finder_gps_alert_channel";
        private const int GPS_ALERT_NOTIFICATION_ID = 2001;
        private const string UPDATE_CHANNEL_ID = "finder_update_channel";
        private const int UPDATE_NOTIFICATION_ID = 3001;

        // ── Instance fields ───────────────────────────────────────────────────
        private readonly string _settingsFilePath;
        private readonly Context _context;
        private readonly GeoJsonManager _geoJsonManager;
        private HttpClient _httpClient;
        private Timer _pollTimer;
        private long _lastUpdateId;

        // ─────────────────────────────────────────────────────────────────────
        // Constructor
        // ─────────────────────────────────────────────────────────────────────

        public TelegramCommandHandler(Context context)
        {
            _context = context;
            _settingsFilePath = Path.Combine(
                System.Environment.GetFolderPath(
                    System.Environment.SpecialFolder.Personal),
                "secure_settings.json");

            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _geoJsonManager = new GeoJsonManager(context);

            var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
            _lastUpdateId = prefs.GetLong(LAST_UPDATE_ID_KEY, 0);

            CreateGpsAlertNotificationChannel();
            CreateUpdateNotificationChannel();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Public lifecycle
        // ─────────────────────────────────────────────────────────────────────

        public void Start(bool sendStartupMessage = false)
        {
            if (!IsPollingEnabled()) return;

            int intervalMs = GetSavedPollIntervalMs();
            RestartPollTimer(intervalMs);

            if (sendStartupMessage)
                _ = SendStartupMessageAsync();
        }

        public void Stop()
        {
            _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _pollTimer?.Dispose();
            _pollTimer = null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Notification channel creation
        // ─────────────────────────────────────────────────────────────────────

        private void CreateGpsAlertNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            try
            {
                var channel = new NotificationChannel(
                    GPS_ALERT_CHANNEL_ID, "GPS Alerts", NotificationImportance.High);
                var nm = (NotificationManager)_context
                    .GetSystemService(Context.NotificationService);
                nm?.CreateNotificationChannel(channel);
            }
            catch { }
        }

        private void CreateUpdateNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            try
            {
                var channel = new NotificationChannel(
                    UPDATE_CHANNEL_ID, "App Updates", NotificationImportance.High);
                var nm = (NotificationManager)_context
                    .GetSystemService(Context.NotificationService);
                nm?.CreateNotificationChannel(channel);
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Polling loop
        // ─────────────────────────────────────────────────────────────────────

        private async void PollForCommands(object state)
        {
            try
            {
                var settings = LoadSettings();
                if (string.IsNullOrEmpty(settings.BotToken) ||
                    string.IsNullOrEmpty(settings.ChatId)) return;

                string url =
                    $"https://api.telegram.org/bot{settings.BotToken}" +
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

                var prefsEditor = PreferenceManager
                    .GetDefaultSharedPreferences(_context).Edit();
                prefsEditor.PutLong(LAST_UPDATE_ID_KEY, _lastUpdateId);
                prefsEditor.Apply();
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Command dispatcher
        // ─────────────────────────────────────────────────────────────────────

        private async Task ProcessCommandAsync(
            string command, AppSettings currentSettings)
        {
            try
            {
                string[] parts = command.Split(new[] { ' ' }, 2);
                string cmd = parts[0].ToLower().Trim();
                string param = parts.Length > 1 ? parts[1].Trim() : null;

                string response;

                switch (cmd)
                {
                    // ── /gpsprovider fused|raw ────────────────────────────────
                    case "/gpsprovider":
                        switch (param?.ToLower())
                        {
                            case "fused":
                                BroadcastGpsProvider("fused");
                                response =
                                    "🔋 *GPS provider → Fused (default)*\n\n" +
                                    "Uses GPS + WiFi + cell towers.\n" +
                                    "The OS manages chip power — battery-efficient.\n\n" +
                                    "✅ Switch applied immediately.\n" +
                                    "ℹ️ /location always uses raw GPS regardless.";
                                break;
                            case "raw":
                                BroadcastGpsProvider("raw");
                                response =
                                    "🛰 *GPS provider → Raw GPS*\n\n" +
                                    "Uses the hardware GPS chip directly.\n" +
                                    "Higher accuracy but *higher battery drain*.\n\n" +
                                    "✅ Switch applied immediately.\n" +
                                    "💡 Send /gpsprovider fused to revert.";
                                break;
                            default:
                                string cur = GetActiveGpsProvider();
                                response =
                                    $"📡 *GPS Provider*\n\n" +
                                    $"Current: {(cur == "raw" ? "🛰 Raw GPS" : "🔋 Fused (default)")}\n\n" +
                                    "/gpsprovider fused — battery-efficient (default)\n" +
                                    "/gpsprovider raw   — hardware GPS (max accuracy)\n\n" +
                                    "ℹ️ /location *always* uses raw GPS for best accuracy.";
                                break;
                        }
                        break;

                    // ── /interval [ms] ────────────────────────────────────────
                    case "/interval":
                        if ((DateTime.Now - _lastIntervalUpdate).TotalSeconds < INTERVAL_COOLDOWN_S)
                        {
                            response = $"⏳ Cooldown active. Wait {INTERVAL_COOLDOWN_S}s.";
                            break;
                        }
                        if (!string.IsNullOrEmpty(param) &&
                            int.TryParse(param, out int ivMs) && ivMs >= 5000)
                        {
                            _lastIntervalUpdate = DateTime.Now;
                            currentSettings.Interval = ivMs.ToString();
                            SaveSettings(currentSettings);
                            await SecureStorage.SetAsync("Interval", ivMs.ToString());

                            var bIntent = new Intent(
                                BackgroundLocationService.ACTION_UPDATE_INTERVAL);
                            bIntent.PutExtra("new_interval", ivMs);
                            _context.SendBroadcast(bIntent);

                            response = $"⏱ Location send interval set to {ivMs} ms";
                        }
                        else
                        {
                            response = "❌ Usage: /interval [ms] (min 5000)";
                        }
                        break;

                    // ── /polling on|off ───────────────────────────────────────
                    case "/polling":
                        switch (param?.ToLower())
                        {
                            case "off":
                                SetPollingEnabled(false);
                                Stop();
                                response =
                                    "⏸ *Polling disabled* — zero network calls.\n" +
                                    "⚠️ Re-enable from the app or restart tracking.";
                                break;
                            case "on":
                                SetPollingEnabled(true);
                                int ci = GetSavedPollIntervalMs();
                                RestartPollTimer(ci);
                                response = $"▶️ *Polling enabled* — every {ci / 1000}s.";
                                break;
                            default:
                                bool en = IsPollingEnabled();
                                response =
                                    $"📡 Polling: *{(en ? "ON ✅" : "OFF ⏸")}*\n" +
                                    "/polling on|off";
                                break;
                        }
                        break;

                    // ── /pollinterval [seconds] ───────────────────────────────
                    case "/pollinterval":
                        if (!string.IsNullOrEmpty(param) &&
                            int.TryParse(param, out int pollSec) && pollSec >= 10)
                        {
                            int pollMs = pollSec * 1000;
                            if (!IsPollingEnabled())
                            {
                                var ed = PreferenceManager
                                    .GetDefaultSharedPreferences(_context).Edit();
                                ed.PutInt(PREF_POLL_INTERVAL_MS, pollMs);
                                ed.Apply();
                                response = $"💾 Saved {pollSec}s — applies when /polling on.";
                            }
                            else
                            {
                                RestartPollTimer(pollMs);
                                response = $"⏱ Poll interval set to {pollSec}s.";
                            }
                        }
                        else
                        {
                            int cur = GetSavedPollIntervalMs();
                            response =
                                $"📡 Poll interval: *{cur / 1000}s*\n" +
                                "/pollinterval [sec] (min 10)";
                        }
                        break;

                    // ── /status ──────────────────────────────────────────────
                    case "/status":
                        bool sendsPaused = IsTelegramSendingPaused();
                        bool pollEnabled = IsPollingEnabled();
                        bool autoStart = Preferences.Get(
                            Finder.ViewModels.MainViewModel.PREF_AUTO_START, false);
                        string activeProvider = GetActiveGpsProvider();
                        string providerLabel = activeProvider == "raw"
                            ? "🛰 Raw GPS"
                            : "🔋 Fused";
                        int pollSecs = GetSavedPollIntervalMs() / 1000;
                        var statusFiles = _geoJsonManager.GetAvailableDataFiles();

                        var statusAssembly = System.Reflection.Assembly
                            .GetExecutingAssembly();
                        var statusVersion = statusAssembly.GetName().Version;
                        string versionStr =
                            $"{statusVersion.Major}.{statusVersion.Minor}" +
                            $".{statusVersion.Build}";

                        int statusBattery = GetBatteryLevel();
                        string batteryStr = statusBattery >= 0
                            ? $"{statusBattery}%" : "Unknown";

                        response =
                            $"📍 *Status*\n" +
                            $"Tracking:        {(IsServiceRunning() ? "✅ Active" : "❌ Stopped")}\n" +
                            $"GPS provider:    {providerLabel}\n" +
                            $"Telegram sends:  {(sendsPaused ? "⏸ Paused" : "▶️ Active")}\n" +
                            $"Command polling: {(pollEnabled ? $"✅ Every {pollSecs}s" : "⏸ Disabled")}\n" +
                            $"Auto-start:      {(autoStart ? "✅ Enabled" : "❌ Disabled")}\n" +
                            $"Token:           {MaskToken(currentSettings.BotToken)}\n" +
                            $"Chat ID:         {currentSettings.ChatId}\n" +
                            $"Send interval:   {currentSettings.Interval} ms\n" +
                            $"Data files:      {statusFiles.Count}\n" +
                            $"App version:     v{versionStr}\n" +
                            $"Battery:         {batteryStr}\n" +
                            $"Device time:     {DateTime.Now:yyyy-MM-dd HH:mm:ss}";

                        if (sendsPaused) response += "\n\n💡 /resumelocation to resume.";
                        if (!pollEnabled) response += "\n💡 /polling on to re-enable.";
                        if (!autoStart) response += "\n💡 /autostart on to enable.";
                        if (activeProvider == "raw")
                            response += "\n⚡ Raw GPS active — higher battery drain.";
                        break;

                    // ── /start ───────────────────────────────────────────────
                    case "/start":
                        StartService();
                        response = "✅ Location tracking started";
                        break;

                    // ── /stop ────────────────────────────────────────────────
                    case "/stop":
                        StopService();
                        response = "⏹ Location tracking stopped";
                        break;

                    // ── /restart ─────────────────────────────────────────────
                    case "/restart":
                        if ((DateTime.Now - _lastRestartCommand).TotalSeconds < RESTART_COOLDOWN_S)
                        {
                            int rem = RESTART_COOLDOWN_S -
                                (int)(DateTime.Now - _lastRestartCommand).TotalSeconds;
                            response = $"⏳ Wait {rem}s before restarting.";
                            break;
                        }
                        _lastRestartCommand = DateTime.Now;

                        var suppressPrefs = PreferenceManager
                            .GetDefaultSharedPreferences(_context);
                        var suppressEditor = suppressPrefs.Edit();
                        suppressEditor.PutBoolean("suppress_next_startup_message", true);
                        suppressEditor.Apply();

                        StopService();
                        await Task.Delay(2000);
                        StartService();
                        response = "🔄 Service restarted";
                        break;

                    // ── /token [token] ────────────────────────────────────────
                    case "/token":
                        if (!string.IsNullOrEmpty(param))
                        {
                            currentSettings.BotToken = param;
                            SaveSettings(currentSettings);
                            response = $"🔑 Token updated: {MaskToken(param)}";
                        }
                        else { response = "❌ Usage: /token [your_bot_token]"; }
                        break;

                    // ── /chatid [id] ──────────────────────────────────────────
                    case "/chatid":
                        if (!string.IsNullOrEmpty(param))
                        {
                            currentSettings.ChatId = param;
                            SaveSettings(currentSettings);
                            response = $"💬 Chat ID updated: {param}";
                        }
                        else { response = "❌ Usage: /chatid [your_chat_id]"; }
                        break;

                    // ── /today ────────────────────────────────────────────────
                    case "/today":
                        await HandleGeoJsonReportAsync(currentSettings, DateTime.Today);
                        response = null;
                        break;

                    // ── /yesterday ────────────────────────────────────────────
                    case "/yesterday":
                        await HandleGeoJsonReportAsync(
                            currentSettings, DateTime.Today.AddDays(-1));
                        response = null;
                        break;

                    // ── /report YYYY-MM-DD ─────────────────────────────────────
                    case "/report":
                        if (!string.IsNullOrEmpty(param) &&
                            DateTime.TryParseExact(param, "yyyy-MM-dd",
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out DateTime reportDate))
                        {
                            await HandleGeoJsonReportAsync(currentSettings, reportDate);
                            response = null;
                        }
                        else { response = "❌ Usage: /report YYYY-MM-DD"; }
                        break;

                    // ── /files ────────────────────────────────────────────────
                    case "/files":
                        var files = _geoJsonManager.GetAvailableDataFiles();
                        response = files.Count == 0
                            ? "📂 No data files found."
                            : "📂 *Available files:*\n" +
                              string.Join("\n",
                                  files.Select(f => $"• {Path.GetFileName(f)}"));
                        break;

                    // ── /cleanup [days] ───────────────────────────────────────
                    case "/cleanup":
                        int keepDays = int.TryParse(param, out int kd) ? kd : 30;
                        int before = _geoJsonManager.GetAvailableDataFiles().Count;
                        await _geoJsonManager.CleanupOldFiles(keepDays);
                        int after = _geoJsonManager.GetAvailableDataFiles().Count;
                        response =
                            $"🧹 Removed {before - after} files older than {keepDays} days.";
                        break;

                    // ── /gpsstatus ────────────────────────────────────────────
                    case "/gpsstatus":
                        bool gpsOn = IsGpsEnabled();
                        bool svcActive = IsServiceRunning();
                        int battery = GetBatteryLevel();
                        string gpsProv = GetActiveGpsProvider();

                        response =
                            "📡 *GPS Status*\n" +
                            $"GPS chip:  {(gpsOn ? "✅ Enabled" : "❌ Disabled")}\n" +
                            $"Provider:  {(gpsProv == "raw" ? "🛰 Raw GPS" : "🔋 Fused")}\n" +
                            $"Service:   {(svcActive ? "✅ Running" : "⏹ Stopped")}\n" +
                            $"Battery:   {(battery >= 0 ? $"{battery}%" : "Unknown")}\n" +
                            $"Time:      {DateTime.Now:HH:mm:ss}";
                        if (!gpsOn)
                            response +=
                                "\n\n💡 /enablelocation to request GPS activation.";
                        break;

                    // ── /enablelocation ───────────────────────────────────────
                    case "/enablelocation":
                        ShowEnableLocationNotification();
                        response =
                            "📲 Notification sent — tap it on the device to open " +
                            "Location Settings.";
                        break;

                    // ── /location ─────────────────────────────────────────────
                    case "/location":
                        await HandleLocationCommandAsync(currentSettings);
                        response = null;
                        break;

                    // ── /pauselocation ────────────────────────────────────────
                    case "/pauselocation":
                        if (IsTelegramSendingPaused())
                        {
                            response = "⏸ Already paused.";
                        }
                        else
                        {
                            BroadcastSendingPaused(true);
                            response = "⏸ *Location sends paused.*\n/resumelocation to resume.";
                        }
                        break;

                    // ── /resumelocation ───────────────────────────────────────
                    case "/resumelocation":
                        if (!IsTelegramSendingPaused())
                        {
                            response = "▶️ Already active.";
                        }
                        else
                        {
                            BroadcastSendingPaused(false);
                            response = "▶️ *Location sends resumed.*";
                        }
                        break;

                    // ── /autostart on|off ─────────────────────────────────────
                    case "/autostart":
                        switch (param?.ToLower())
                        {
                            case "on":
                                Preferences.Set(
                                    Finder.ViewModels.MainViewModel.PREF_AUTO_START, true);
                                response = "✅ Auto-start enabled.";
                                break;
                            case "off":
                                Preferences.Set(
                                    Finder.ViewModels.MainViewModel.PREF_AUTO_START, false);
                                response = "❌ Auto-start disabled.";
                                break;
                            default:
                                response = "❌ Usage: /autostart on|off";
                                break;
                        }
                        break;

                    // ── /version ──────────────────────────────────────────────
                    case "/version":
                        var runningAssembly = System.Reflection.Assembly
                            .GetExecutingAssembly();
                        var runningVersion = runningAssembly.GetName().Version;
                        response =
                            $"📦 *Finder — Installed Version*\n\n" +
                            $"Version: `{runningVersion.Major}.{runningVersion.Minor}" +
                            $".{runningVersion.Build}`\n\n" +
                            $"To push an update:\n" +
                            $"`/update [version] [url]`\n\n" +
                            $"Example:\n" +
                            $"`/update 1.0.2 https://drive.usercontent.google.com/" +
                            $"download?id=FILE_ID&export=download&confirm=t`";
                        break;

                    // ── /update [version] [url] ───────────────────────────────
                    // Downloads the APK from [url] and triggers the Android installer.
                    // Supported: Google Drive, Dropbox, any direct HTTPS URL.
                    // Usage:
                    //   /update 1.0.2 https://drive.usercontent.google.com/download?id=FILE_ID&export=download&confirm=t
                    //   /update 1.0.2 https://www.dropbox.com/s/xxx/Finder.apk?dl=1
                    case "/update":
                        await HandleUpdateCommandAsync(param, currentSettings);
                        response = null;
                        break;

                    // ═════════════════════════════════════════════════════════
                    // 🔋 DEVICE HEALTH COMMANDS
                    // ═════════════════════════════════════════════════════════

                    // ── /battery ─────────────────────────────────────────────
                    case "/battery":
                        response = GetBatteryReport();
                        break;

                    // ── /charging ────────────────────────────────────────────
                    case "/charging":
                        response = GetChargingReport();
                        break;

                    // ── /temperature ─────────────────────────────────────────
                    case "/temperature":
                        response = GetTemperatureReport();
                        break;

                    // ── /storage ─────────────────────────────────────────────
                    case "/storage":
                        response = GetStorageReport();
                        break;

                    // ── /memory ──────────────────────────────────────────────
                    case "/memory":
                        response = GetMemoryReport();
                        break;

                    // ── /uptime ──────────────────────────────────────────────
                    case "/uptime":
                        response = GetUptimeReport();
                        break;

                    // ═════════════════════════════════════════════════════════
                    // /cmd — command list (help)
                    // ═════════════════════════════════════════════════════════
                    case "/cmd":
                    default:
                        response =
                            "📋 *Available commands:*\n\n" +
                            "📍 *Tracking*\n" +
                            "/start · /stop · /restart · /status\n" +
                            "/autostart on|off\n\n" +
                            "🛰 *GPS Provider*\n" +
                            "/gpsprovider fused — battery-efficient (default)\n" +
                            "/gpsprovider raw   — hardware GPS (max accuracy)\n" +
                            "/gpsstatus · /enablelocation\n" +
                            "/location — current fix *(always raw GPS)*\n\n" +
                            "📡 *Polling*\n" +
                            "/polling on|off\n" +
                            "/pollinterval [sec] (min 10)\n\n" +
                            "📤 *Location sends*\n" +
                            "/interval [ms] (min 5000)\n" +
                            "/pauselocation · /resumelocation\n\n" +
                            "🔋 *Device Health*\n" +
                            "/battery     — level, status, plug, voltage\n" +
                            "/charging    — charge source & state\n" +
                            "/temperature — battery temp & health\n" +
                            "/storage     — internal storage usage\n" +
                            "/memory      — RAM usage\n" +
                            "/uptime      — time since last reboot\n\n" +
                            "📂 *Data*\n" +
                            "/today · /yesterday\n" +
                            "/report YYYY-MM-DD\n" +
                            "/files · /cleanup [days]\n\n" +
                            "⚙️ *Config*\n" +
                            "/token [token] · /chatid [id]\n\n" +
                            "⬆️ *App Update*\n" +
                            "/version — show installed version\n" +
                            "/update [ver] [url] — push new APK\n";
                        break;
                }

                if (response != null)
                    await SendTelegramMessageAsync(
                        currentSettings.BotToken, currentSettings.ChatId, response);
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        // /location handler — ALWAYS uses raw GPS regardless of active provider
        // ─────────────────────────────────────────────────────────────────────

        private async Task HandleLocationCommandAsync(AppSettings settings)
        {
            await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                "📡 Fetching current location via raw GPS…");
            try
            {
                AndroidLocation rawLocation = null;

                // Step 1: try last known raw GPS fix (zero battery cost)
                try
                {
                    var lm = (DroidLocation.LocationManager)_context
                        .GetSystemService(Context.LocationService);
                    rawLocation = lm?.GetLastKnownLocation(
                        DroidLocation.LocationManager.GpsProvider);
                }
                catch { }

                // Step 2: if no cached fix, request a fresh one (times out in 30s)
                if (rawLocation == null)
                    rawLocation = await RequestFreshGpsFixAsync();

                if (rawLocation == null)
                {
                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        "❌ Could not get a GPS fix. Make sure GPS is enabled and " +
                        "the device has sky visibility.");
                    return;
                }

                string mapsUrl =
                    $"https://www.google.com/maps?q=" +
                    $"{rawLocation.Latitude},{rawLocation.Longitude}";

                int bat = GetBatteryLevel();
                string batStr = bat >= 0 ? $"{bat}%" : "unknown";

                string msg =
                    $"📍 *Current Location*\n\n" +
                    $"Lat: `{rawLocation.Latitude:F6}`\n" +
                    $"Lng: `{rawLocation.Longitude:F6}`\n" +
                    (rawLocation.HasAltitude
                        ? $"Alt: {rawLocation.Altitude:F1} m\n" : "") +
                    (rawLocation.HasAccuracy
                        ? $"Accuracy: ±{rawLocation.Accuracy:F0} m\n" : "") +
                    (rawLocation.HasSpeed
                        ? $"Speed: {rawLocation.Speed * 3.6:F1} km/h\n" : "") +
                    $"Battery: {batStr}\n" +
                    $"Time: {DateTime.Now:HH:mm:ss}\n\n" +
                    $"[Open in Maps]({mapsUrl})";

                await SendTelegramMessageAsync(settings.BotToken, settings.ChatId, msg);
            }
            catch (Exception ex)
            {
                await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                    $"❌ Location error: {ex.Message}");
            }
        }

        private async Task<AndroidLocation> RequestFreshGpsFixAsync()
        {
            var tcs = new TaskCompletionSource<AndroidLocation>();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetResult(null));

            try
            {
                var lm = (DroidLocation.LocationManager)_context
                    .GetSystemService(Context.LocationService);

                var listener = new SingleShotLocationListener(loc =>
                    tcs.TrySetResult(loc));

                lm?.RequestSingleUpdate(
                    DroidLocation.LocationManager.GpsProvider, listener, null);

                return await tcs.Task;
            }
            catch
            {
                return null;
            }
            finally
            {
                cts.Dispose();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // /update handler
        // ─────────────────────────────────────────────────────────────────────

        private async Task HandleUpdateCommandAsync(string param, AppSettings settings)
        {
            try
            {
                // ── Cooldown guard ────────────────────────────────────────────
                if ((DateTime.Now - _lastUpdateCommand).TotalSeconds < UPDATE_COOLDOWN_S)
                {
                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        $"⏳ Update cooldown active. " +
                        $"Please wait {UPDATE_COOLDOWN_S} seconds between requests.");
                    return;
                }

                // ── Parameter validation ──────────────────────────────────────
                if (string.IsNullOrWhiteSpace(param))
                {
                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        "❌ *Missing parameters.*\n\n" +
                        "Usage: `/update [version] [url]`\n\n" +
                        "Example:\n" +
                        "`/update 1.0.2 https://drive.usercontent.google.com/" +
                        "download?id=FILE_ID&export=download&confirm=t`");
                    return;
                }

                string[] updateParts = param.Split(
                    new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);

                if (updateParts.Length < 2 ||
                    string.IsNullOrWhiteSpace(updateParts[0]) ||
                    string.IsNullOrWhiteSpace(updateParts[1]))
                {
                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        "❌ *Invalid format.*\n\nUsage: `/update [version] [url]`");
                    return;
                }

                string requestedVersionStr = updateParts[0].Trim();
                string downloadUrl = updateParts[1].Trim();

                // ── Version comparison ────────────────────────────────────────
                var installedAssembly = System.Reflection.Assembly.GetExecutingAssembly();
                var installedVersion = installedAssembly.GetName().Version;

                if (!Version.TryParse(requestedVersionStr, out Version requestedVersion))
                {
                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        $"❌ Invalid version format: `{requestedVersionStr}`\n" +
                        "Use `Major.Minor.Build` e.g. `1.0.2`");
                    return;
                }

                string installedStr =
                    $"{installedVersion.Major}.{installedVersion.Minor}" +
                    $".{installedVersion.Build}";

                if (requestedVersion <= installedVersion)
                {
                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        $"ℹ️ Installed version (`{installedStr}`) is already " +
                        $"≥ requested (`{requestedVersionStr}`). No update needed.");
                    return;
                }

                // ── Step 0: Install-unknown-apps permission check ─────────────
                // API 26+: per-app toggle the user must enable once.
                // API 21-25: REQUEST_INSTALL_PACKAGES manifest entry is enough —
                //   CanInstallPackages() always returns true on those versions.
                //
                // If the permission is missing we store the pending update in
                // SharedPreferences, open the settings screen, and abort here.
                // MainActivity.OnResume will call ResumePendingUpdateAsync()
                // when the user returns — no need to send /update again.
                if (!ApkInstaller.CanInstallPackages(_context))
                {
                    // Persist version + URL so the auto-resume can pick them up
                    var pendingEd = PreferenceManager
                        .GetDefaultSharedPreferences(_context).Edit();
                    pendingEd.PutString(PREF_PENDING_UPDATE_VERSION, requestedVersionStr);
                    pendingEd.PutString(PREF_PENDING_UPDATE_URL, downloadUrl);
                    pendingEd.Apply();

                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        "⚠️ *Permission Required: Install Unknown Apps*\n\n" +
                        "Finder does not have permission to install APKs from " +
                        "unknown sources on this device.\n\n" +
                        "📲 Opening *Install Unknown Apps* settings on the " +
                        "device now…\n\n" +
                        "👉 Find *Finder* and toggle it *ON*.\n\n" +
                        "✅ The update will then *start automatically* — " +
                        "no need to send `/update` again.");

                    // Opens Settings → Special App Access → Install Unknown Apps → Finder
                    // No-op on API 21-25 (global setting there).
                    ApkInstaller.OpenInstallPermissionSettings(_context);
                    return; // Abort — OnResume will auto-resume the download.
                }

                // Permission is granted — clear any stale pending update entry.
                ClearPendingUpdate();
                // ─────────────────────────────────────────────────────────────

                _lastUpdateCommand = DateTime.Now;

                await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                    $"⬆️ *Update initiated*\n\n" +
                    $"`{installedStr}` → `{requestedVersionStr}`\n\n" +
                    $"Downloading…");

                // ── Notify in-app UI ──────────────────────────────────────────
                MessagingCenter.Send<object, string>(
                    this,
                    Finder.ViewModels.MainViewModel.MSG_UPDATE_STARTED,
                    $"{installedStr} → {requestedVersionStr}");

                // ── Download ──────────────────────────────────────────────────
                var downloader = new ApkDownloaderService(_context);
                var result = await downloader.DownloadApkAsync(
                    downloadUrl,
                    progress =>
                    {
                        MessagingCenter.Send<object, string>(
                            this,
                            Finder.ViewModels.MainViewModel.MSG_UPDATE_PROGRESS,
                            progress.ToString());
                    });

                if (!result.IsSuccess)
                {
                    MessagingCenter.Send<object, string>(
                        this,
                        Finder.ViewModels.MainViewModel.MSG_UPDATE_FAILED,
                        result.FailReason);

                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        $"❌ *Download failed:*\n`{result.FailReason}`");
                    return;
                }

                // ── Install ───────────────────────────────────────────────────
                MessagingCenter.Send<object, string>(
                    this,
                    Finder.ViewModels.MainViewModel.MSG_UPDATE_INSTALLING,
                    requestedVersionStr);

                ApkInstaller.Install(_context, result.FilePath);

                MessagingCenter.Send<object, string>(
                    this,
                    Finder.ViewModels.MainViewModel.MSG_UPDATE_COMPLETE,
                    requestedVersionStr);

                await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                    $"✅ *Install prompt launched.*\n\n" +
                    $"Version: `{requestedVersionStr}`\n\n" +
                    "📲 Tap *Install* on the device to complete the update.\n" +
                    "_The app restarts automatically after installation._");
            }
            catch (Exception ex)
            {
                try
                {
                    MessagingCenter.Send<object, string>(
                        this,
                        Finder.ViewModels.MainViewModel.MSG_UPDATE_FAILED,
                        ex.Message);

                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        $"❌ *Unexpected update error:*\n`{ex.Message}`");
                }
                catch { }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // GeoJSON report handler
        // ─────────────────────────────────────────────────────────────────────

        private async Task HandleGeoJsonReportAsync(AppSettings settings, DateTime date)
        {
            try
            {
                string filePath = _geoJsonManager.GetFilePathForDate(date);
                if (filePath == null || !File.Exists(filePath))
                {
                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        $"📂 No data found for {date:yyyy-MM-dd}.");
                    return;
                }

                string caption = $"📍 Location data for {date:yyyy-MM-dd}";
                await SendFileToTelegramAsync(filePath, caption, settings);
            }
            catch (Exception ex)
            {
                await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                    $"❌ Report error: {ex.Message}");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // 🔋 DEVICE HEALTH — report builders
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// /battery — full battery snapshot: level, status, plug type,
        /// temperature, voltage, health.
        /// No extra permissions required (BatteryManager sticky broadcast).
        /// </summary>
        private string GetBatteryReport()
        {
            try
            {
                var filter = new IntentFilter(Intent.ActionBatteryChanged);
                var battery = _context.RegisterReceiver(null, filter);

                if (battery == null)
                    return "❌ Could not read battery information.";

                int level = battery.GetIntExtra(BatteryManager.ExtraLevel, -1);
                int scale = battery.GetIntExtra(BatteryManager.ExtraScale, 100);
                int pct = scale > 0 ? (int)(level * 100f / scale) : -1;
                int statusCode = battery.GetIntExtra(BatteryManager.ExtraStatus, -1);
                int plugCode = battery.GetIntExtra(BatteryManager.ExtraPlugged, -1);
                int tempRaw = battery.GetIntExtra(BatteryManager.ExtraTemperature, -1);
                int voltMv = battery.GetIntExtra(BatteryManager.ExtraVoltage, -1);
                int healthCode = battery.GetIntExtra(BatteryManager.ExtraHealth, -1);

                float tempC = tempRaw / 10f;
                string tempStr = tempRaw >= 0 ? $"{tempC:F1} °C" : "Unknown";
                string volt = voltMv > 0 ? $"{voltMv} mV" : "Unknown";

                string levelEmoji = BatteryEmoji(pct, statusCode);

                return
                    $"🔋 *Battery*\n\n" +
                    $"{levelEmoji} Level:       {(pct >= 0 ? $"{pct}%" : "Unknown")}\n" +
                    $"⚡ Status:      {BatteryStatusLabel(statusCode)}\n" +
                    $"🔌 Plugged:     {PluggedLabel(plugCode)}\n" +
                    $"🌡 Temperature: {tempStr}\n" +
                    $"⚡ Voltage:     {volt}\n" +
                    $"❤️ Health:      {BatteryHealthLabel(healthCode)}\n" +
                    $"🕐 Time:        {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                return $"❌ Battery error: {ex.Message}";
            }
        }

        /// <summary>
        /// /charging — focused view on charging state and power source only.
        /// </summary>
        private string GetChargingReport()
        {
            try
            {
                var filter = new IntentFilter(Intent.ActionBatteryChanged);
                var battery = _context.RegisterReceiver(null, filter);

                if (battery == null)
                    return "❌ Could not read charging information.";

                int level = battery.GetIntExtra(BatteryManager.ExtraLevel, -1);
                int scale = battery.GetIntExtra(BatteryManager.ExtraScale, 100);
                int pct = scale > 0 ? (int)(level * 100f / scale) : -1;
                int statusCode = battery.GetIntExtra(BatteryManager.ExtraStatus, -1);
                int plugCode = battery.GetIntExtra(BatteryManager.ExtraPlugged, -1);

                bool isCharging =
                    statusCode == (int)BatteryStatus.Charging ||
                    statusCode == (int)BatteryStatus.Full;

                string header = isCharging ? "⚡ *Charging*" : "🔋 *Not Charging*";

                return
                    $"{header}\n\n" +
                    $"Status:  {BatteryStatusLabel(statusCode)}\n" +
                    $"Source:  {PluggedLabel(plugCode)}\n" +
                    $"Level:   {(pct >= 0 ? $"{pct}%" : "Unknown")}\n" +
                    $"Time:    {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                return $"❌ Charging error: {ex.Message}";
            }
        }

        /// <summary>
        /// /temperature — battery temperature + plain-language health label.
        /// Temperature is reported by Android in tenths of a degree Celsius.
        /// </summary>
        private string GetTemperatureReport()
        {
            try
            {
                var filter = new IntentFilter(Intent.ActionBatteryChanged);
                var battery = _context.RegisterReceiver(null, filter);

                if (battery == null)
                    return "❌ Could not read temperature.";

                int tempRaw = battery.GetIntExtra(BatteryManager.ExtraTemperature, -1);
                float tempC = tempRaw / 10f;
                float tempF = tempC * 9f / 5f + 32f;
                int healthCode = battery.GetIntExtra(BatteryManager.ExtraHealth, -1);

                string assessment;
                string assessEmoji;

                if (tempRaw < 0) { assessment = "Unknown"; assessEmoji = "❓"; }
                else if (tempC < 0) { assessment = "Too cold — may affect performance"; assessEmoji = "🥶"; }
                else if (tempC <= 20) { assessment = "Cool"; assessEmoji = "❄️"; }
                else if (tempC <= 35) { assessment = "Normal"; assessEmoji = "✅"; }
                else if (tempC <= 45) { assessment = "Warm — monitor closely"; assessEmoji = "⚠️"; }
                else { assessment = "HOT — risk of throttling or shutdown"; assessEmoji = "🔥"; }

                string tempDisplay = tempRaw >= 0
                    ? $"{tempC:F1} °C  ({tempF:F1} °F)"
                    : "Unknown";

                return
                    $"🌡 *Battery Temperature*\n\n" +
                    $"Temperature: {tempDisplay}\n" +
                    $"Assessment:  {assessEmoji} {assessment}\n" +
                    $"Health:      {BatteryHealthLabel(healthCode)}\n" +
                    $"Time:        {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                return $"❌ Temperature error: {ex.Message}";
            }
        }

        /// <summary>
        /// /storage — internal storage (data partition): total, used, free.
        /// Also reports external SD card if mounted. No permissions required.
        /// </summary>
        private string GetStorageReport()
        {
            try
            {
                var stat = new StatFs(Android.OS.Environment.DataDirectory.AbsolutePath);
                long blockSize = stat.BlockSizeLong;
                long totalBytes = stat.BlockCountLong * blockSize;
                long freeBytes = stat.AvailableBlocksLong * blockSize;
                long usedBytes = totalBytes - freeBytes;
                int fillPct = totalBytes > 0 ? (int)(usedBytes * 100L / totalBytes) : 0;
                string bar = StorageBar(fillPct);

                string report =
                    $"💾 *Internal Storage*\n\n" +
                    $"Total: {FormatBytes(totalBytes)}\n" +
                    $"Used:  {FormatBytes(usedBytes)}  ({fillPct}%)\n" +
                    $"Free:  {FormatBytes(freeBytes)}  ({100 - fillPct}%)\n\n" +
                    $"`{bar}` {fillPct}%";

                // External SD card (optional)
                try
                {
                    if (Android.OS.Environment.ExternalStorageState ==
                        Android.OS.Environment.MediaMounted)
                    {
                        var extStat = new StatFs(
                            Android.OS.Environment.ExternalStorageDirectory.AbsolutePath);
                        long extBlock = extStat.BlockSizeLong;
                        long extTotal = extStat.BlockCountLong * extBlock;
                        long extFree = extStat.AvailableBlocksLong * extBlock;
                        long extUsed = extTotal - extFree;
                        int extPct = extTotal > 0
                            ? (int)(extUsed * 100L / extTotal) : 0;

                        report +=
                            $"\n\n💽 *External Storage*\n" +
                            $"Total: {FormatBytes(extTotal)}\n" +
                            $"Used:  {FormatBytes(extUsed)}  ({extPct}%)\n" +
                            $"Free:  {FormatBytes(extFree)}  ({100 - extPct}%)";
                    }
                }
                catch { /* External storage not available — skip silently */ }

                report += $"\n\n🕐 Time: {DateTime.Now:HH:mm:ss}";
                return report;
            }
            catch (Exception ex)
            {
                return $"❌ Storage error: {ex.Message}";
            }
        }

        /// <summary>
        /// /memory — RAM: total, available, used, Android's low-memory flag.
        /// Uses ActivityManager.MemoryInfo — no permissions required.
        /// </summary>
        private string GetMemoryReport()
        {
            try
            {
                var am = (ActivityManager)_context
                    .GetSystemService(Context.ActivityService);
                var memInfo = new ActivityManager.MemoryInfo();
                am.GetMemoryInfo(memInfo);

                long totalRam = memInfo.TotalMem;
                long availRam = memInfo.AvailMem;
                long usedRam = totalRam - availRam;
                int usedPct = totalRam > 0
                    ? (int)(usedRam * 100L / totalRam) : 0;
                bool lowMem = memInfo.LowMemory;
                string bar = StorageBar(usedPct);

                return
                    $"🧠 *RAM*\n\n" +
                    $"Total:      {FormatBytes(totalRam)}\n" +
                    $"Used:       {FormatBytes(usedRam)}  ({usedPct}%)\n" +
                    $"Available:  {FormatBytes(availRam)}  ({100 - usedPct}%)\n\n" +
                    $"`{bar}` {usedPct}%\n\n" +
                    $"Low Memory: {(lowMem ? "⚠️ YES — system under pressure" : "✅ No")}\n" +
                    $"🕐 Time:    {DateTime.Now:HH:mm:ss}";
            }
            catch (Exception ex)
            {
                return $"❌ Memory error: {ex.Message}";
            }
        }

        /// <summary>
        /// /uptime — elapsed time since the last device reboot.
        /// Computed from SystemClock.ElapsedRealtime() — includes deep sleep,
        /// no permissions required.
        /// </summary>
        private string GetUptimeReport()
        {
            try
            {
                long elapsedMs = SystemClock.ElapsedRealtime();
                long totalSec = elapsedMs / 1000;
                long days = totalSec / 86400;
                long hours = (totalSec % 86400) / 3600;
                long minutes = (totalSec % 3600) / 60;
                long seconds = totalSec % 60;

                DateTime bootTime = DateTime.Now.AddMilliseconds(-elapsedMs);

                string uptime;
                if (days > 0) uptime = $"{days}d {hours}h {minutes}m";
                else if (hours > 0) uptime = $"{hours}h {minutes}m {seconds}s";
                else uptime = $"{minutes}m {seconds}s";

                return
                    $"⏱ *Device Uptime*\n\n" +
                    $"Running for: {uptime}\n" +
                    $"Last reboot: {bootTime:yyyy-MM-dd HH:mm:ss}\n" +
                    $"Now:         {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            }
            catch (Exception ex)
            {
                return $"❌ Uptime error: {ex.Message}";
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // 🔋 Device Health — private helper utilities
        // ═════════════════════════════════════════════════════════════════════

        private static string BatteryStatusLabel(int code)
        {
            if (code == (int)BatteryStatus.Charging) return "⚡ Charging";
            if (code == (int)BatteryStatus.Discharging) return "🔋 Discharging";
            if (code == (int)BatteryStatus.Full) return "✅ Full";
            if (code == (int)BatteryStatus.NotCharging) return "⏸ Not Charging";
            return "❓ Unknown";
        }

        private static string PluggedLabel(int code)
        {
            if (code == (int)BatteryPlugged.Ac) return "🔌 AC Adapter";
            if (code == (int)BatteryPlugged.Usb) return "🔌 USB";
            if (code == (int)BatteryPlugged.Wireless) return "📶 Wireless";
            return "🔋 Unplugged";
        }

        private static string BatteryHealthLabel(int code)
        {
            if (code == (int)BatteryHealth.Good) return "✅ Good";
            if (code == (int)BatteryHealth.Overheat) return "🔥 Overheat";
            if (code == (int)BatteryHealth.Dead) return "💀 Dead";
            if (code == (int)BatteryHealth.OverVoltage) return "⚡ Over Voltage";
            if (code == (int)BatteryHealth.Cold) return "🥶 Cold";
            return "❓ Unknown";
        }

        private static string BatteryEmoji(int pct, int statusCode)
        {
            if (statusCode == (int)BatteryStatus.Charging ||
                statusCode == (int)BatteryStatus.Full) return "⚡";
            if (pct < 0) return "❓";
            if (pct <= 10) return "🪫";
            return "🔋";
        }

        /// <summary>
        /// Builds a 10-character ASCII progress bar. Example: 65% → [██████░░░░]
        /// </summary>
        private static string StorageBar(int fillPct)
        {
            const int barLen = 10;
            int filled = Math.Max(0, Math.Min(barLen, (int)Math.Round(fillPct / 10.0)));
            return $"[{new string('█', filled)}{new string('░', barLen - filled)}]";
        }

        /// <summary>Converts a byte count to KB / MB / GB string.</summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "Unknown";
            if (bytes < 1024L) return $"{bytes} B";
            if (bytes < 1048576L) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1073741824L) return $"{bytes / 1048576.0:F1} MB";
            return $"{bytes / 1073741824.0:F2} GB";
        }

        // ─────────────────────────────────────────────────────────────────────
        // Service control helpers
        // ─────────────────────────────────────────────────────────────────────

        private void StartService()
        {
            try
            {
                AppCommandHandler.Stop();
                var intent = new Intent(_context, typeof(BackgroundLocationService));
                intent.PutExtra("explicit_user_start", true);
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    _context.StartForegroundService(intent);
                else
                    _context.StartService(intent);
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

                Task.Delay(2000).ContinueWith(_ =>
                {
                    BackgroundLocationService.IsStoppingByUserRequest = false;
                    AppCommandHandler.Start(_context, sendStartupMessage: false);
                });
            }
            catch { }
        }

        private bool IsServiceRunning()
            => BackgroundLocationService.IsRunning;

        // ─────────────────────────────────────────────────────────────────────
        // Telegram sends paused helpers
        // ─────────────────────────────────────────────────────────────────────

        private bool IsTelegramSendingPaused()
        {
            var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
            return prefs.GetBoolean(
                BackgroundLocationService.PREF_KEY_SENDING_PAUSED_PUBLIC, false);
        }

        private void BroadcastSendingPaused(bool paused)
        {
            var editor = PreferenceManager
                .GetDefaultSharedPreferences(_context).Edit();
            editor.PutBoolean(
                BackgroundLocationService.PREF_KEY_SENDING_PAUSED_PUBLIC, paused);
            editor.Apply();

            try
            {
                var intent = new Intent(BackgroundLocationService.ACTION_SET_SENDING_PAUSED);
                intent.PutExtra("paused", paused);
                _context.SendBroadcast(intent);
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        // GPS / battery helpers
        // ─────────────────────────────────────────────────────────────────────

        private bool IsGpsEnabled()
        {
            try
            {
                var lm = (DroidLocation.LocationManager)_context
                    .GetSystemService(Context.LocationService);
                return lm?.IsProviderEnabled(
                    DroidLocation.LocationManager.GpsProvider) == true;
            }
            catch { return false; }
        }

        /// <summary>Returns battery level 0-100, or -1 if unavailable.</summary>
        private int GetBatteryLevel()
        {
            try
            {
                var filter = new IntentFilter(Intent.ActionBatteryChanged);
                var battery = _context.RegisterReceiver(null, filter);
                int level = battery?.GetIntExtra(BatteryManager.ExtraLevel, -1) ?? -1;
                int scale = battery?.GetIntExtra(BatteryManager.ExtraScale, 1) ?? 1;
                return scale > 0 ? (int)(level * 100f / scale) : -1;
            }
            catch { return -1; }
        }

        private string GetActiveGpsProvider()
        {
            try
            {
                var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
                return prefs.GetString(
                    BackgroundLocationService.PREF_KEY_GPS_PROVIDER, "fused") ?? "fused";
            }
            catch { return "fused"; }
        }

        private void BroadcastGpsProvider(string provider)
        {
            var editor = PreferenceManager.GetDefaultSharedPreferences(_context).Edit();
            editor.PutString(BackgroundLocationService.PREF_KEY_GPS_PROVIDER, provider);
            editor.Apply();

            try
            {
                var intent = new Intent(BackgroundLocationService.ACTION_SET_GPS_PROVIDER);
                intent.PutExtra("provider", provider);
                _context.SendBroadcast(intent);
            }
            catch { }
        }

        private void ShowEnableLocationNotification()
        {
            try
            {
                var intent = new Intent(
                    Android.Provider.Settings.ActionLocationSourceSettings);
                intent.AddFlags(ActivityFlags.NewTask);

                var pendingFlags = Build.VERSION.SdkInt >= BuildVersionCodes.M
                    ? PendingIntentFlags.Immutable
                    : (PendingIntentFlags)0;

                var pendingIntent = PendingIntent.GetActivity(
                    _context, 0, intent, pendingFlags);

                var builder = new NotificationCompat.Builder(
                        _context, GPS_ALERT_CHANNEL_ID)
                    .SetContentTitle("Enable GPS")
                    .SetContentText("Tap to open Location Settings")
                    .SetSmallIcon(Android.Resource.Drawable.IcDialogMap)
                    .SetPriority(NotificationCompat.PriorityHigh)
                    .SetAutoCancel(true)
                    .SetContentIntent(pendingIntent);

                var nm = (NotificationManager)_context
                    .GetSystemService(Context.NotificationService);
                nm?.Notify(GPS_ALERT_NOTIFICATION_ID, builder.Build());
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Poll timer helpers
        // ─────────────────────────────────────────────────────────────────────

        private void RestartPollTimer(int intervalMs)
        {
            _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _pollTimer?.Dispose();
            _pollTimer = new Timer(PollForCommands, null, 0, intervalMs);

            var ed = PreferenceManager.GetDefaultSharedPreferences(_context).Edit();
            ed.PutInt(PREF_POLL_INTERVAL_MS, intervalMs);
            ed.Apply();
        }

        private bool IsPollingEnabled()
        {
            var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
            return prefs.GetBoolean(PREF_POLLING_ENABLED, true);
        }

        private void SetPollingEnabled(bool enabled)
        {
            var ed = PreferenceManager.GetDefaultSharedPreferences(_context).Edit();
            ed.PutBoolean(PREF_POLLING_ENABLED, enabled);
            ed.Apply();
        }

        private int GetSavedPollIntervalMs()
        {
            var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
            return Math.Max(
                MIN_POLL_INTERVAL_MS,
                prefs.GetInt(PREF_POLL_INTERVAL_MS, DEFAULT_POLL_INTERVAL_MS));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Telegram messaging
        // ─────────────────────────────────────────────────────────────────────

        private async Task SendTelegramMessageAsync(
            string botToken, string chatId, string message)
        {
            try
            {
                string url =
                    $"https://api.telegram.org/bot{botToken}/sendMessage" +
                    $"?chat_id={chatId}" +
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

        // ─────────────────────────────────────────────────────────────────────
        // Settings helpers
        // ─────────────────────────────────────────────────────────────────────

        private AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var s = JsonConvert.DeserializeObject<AppSettings>(
                        File.ReadAllText(_settingsFilePath));
                    if (s != null) return s;
                }
            }
            catch { }
            return new AppSettings();
        }

        private void SaveSettings(AppSettings settings)
        {
            try
            {
                File.WriteAllText(
                    _settingsFilePath,
                    JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch { }
        }

        private static string MaskToken(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length < 8) return "***";
            return token.Substring(0, 4) + "…" + token.Substring(token.Length - 4);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Startup message
        // ─────────────────────────────────────────────────────────────────────

        private async Task SendStartupMessageAsync()
        {
            try
            {
                // Respect the suppress flag set by /restart to avoid double messages
                var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
                bool suppress = prefs.GetBoolean("suppress_next_startup_message", false);
                if (suppress)
                {
                    var ed = prefs.Edit();
                    ed.PutBoolean("suppress_next_startup_message", false);
                    ed.Apply();
                    return;
                }

                if ((DateTime.Now - _lastStartupMessage).TotalSeconds < STARTUP_COOLDOWN_S)
                    return;
                _lastStartupMessage = DateTime.Now;

                var ver = System.Reflection.Assembly
                    .GetExecutingAssembly().GetName().Version;
                string vs = $"{ver.Major}.{ver.Minor}.{ver.Build}";

                int bat = GetBatteryLevel();
                string batStr = bat >= 0 ? $"{bat}%" : "Unknown";

                await SendTelegramMessageAsync(
                    LoadSettings().BotToken,
                    LoadSettings().ChatId,
                    $"📡 *Finder v{vs} is running*\n" +
                    $"Battery: {batStr}\n" +
                    $"Time:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n" +
                    "Send /cmd for all available commands.");
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Pending update — auto-resume after permission grant
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Called by MainActivity.OnResume every time the app returns to the
        /// foreground — including after the user comes back from the
        /// "Install Unknown Apps" settings screen.
        ///
        /// If the permission is now granted AND a pending update was stored
        /// earlier, this method performs the full download + install flow
        /// automatically. No need to send /update again.
        ///
        /// This is a no-op when:
        ///   • There is no pending update in SharedPreferences.
        ///   • The install permission is still not granted.
        /// </summary>
        public static async Task ResumePendingUpdateAsync(Android.Content.Context context)
        {
            // Guard: permission must be granted now
            if (!ApkInstaller.CanInstallPackages(context)) return;

            // Guard: a pending update must have been stored
            var prefs = PreferenceManager.GetDefaultSharedPreferences(context);
            string version = prefs.GetString(PREF_PENDING_UPDATE_VERSION, null);
            string url = prefs.GetString(PREF_PENDING_UPDATE_URL, null);

            if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(url)) return;

            // Clear immediately — prevents double-trigger if OnResume fires twice
            ClearPendingUpdate(context);

            System.Diagnostics.Debug.WriteLine(
                $"[TelegramCommandHandler] Auto-resuming pending update → v{version}");

            try
            {
                // Notify in-app UI: download starting
                MessagingCenter.Send<object, string>(
                    new object(),
                    Finder.ViewModels.MainViewModel.MSG_UPDATE_STARTED,
                    $"→ v{version}");

                // Download
                var downloader = new ApkDownloaderService(context);
                var result = await downloader.DownloadApkAsync(url, progress =>
                {
                    MessagingCenter.Send<object, string>(
                        new object(),
                        Finder.ViewModels.MainViewModel.MSG_UPDATE_PROGRESS,
                        progress.ToString());
                });

                if (!result.IsSuccess)
                {
                    MessagingCenter.Send<object, string>(
                        new object(),
                        Finder.ViewModels.MainViewModel.MSG_UPDATE_FAILED,
                        result.FailReason);

                    System.Diagnostics.Debug.WriteLine(
                        $"[TelegramCommandHandler] Auto-resume download failed: " +
                        result.FailReason);
                    return;
                }

                // Install
                MessagingCenter.Send<object, string>(
                    new object(),
                    Finder.ViewModels.MainViewModel.MSG_UPDATE_INSTALLING,
                    version);

                ApkInstaller.Install(context, result.FilePath);

                MessagingCenter.Send<object, string>(
                    new object(),
                    Finder.ViewModels.MainViewModel.MSG_UPDATE_COMPLETE,
                    version);
            }
            catch (Exception ex)
            {
                MessagingCenter.Send<object, string>(
                    new object(),
                    Finder.ViewModels.MainViewModel.MSG_UPDATE_FAILED,
                    ex.Message);

                System.Diagnostics.Debug.WriteLine(
                    $"[TelegramCommandHandler] Auto-resume exception: {ex.Message}");
            }
        }

        /// <summary>Instance version — uses _context.</summary>
        private void ClearPendingUpdate()
            => ClearPendingUpdate(_context);

        /// <summary>
        /// Static version — used by ResumePendingUpdateAsync which has no instance.
        /// </summary>
        private static void ClearPendingUpdate(Android.Content.Context context)
        {
            try
            {
                var ed = PreferenceManager
                    .GetDefaultSharedPreferences(context).Edit();
                ed.Remove(PREF_PENDING_UPDATE_VERSION);
                ed.Remove(PREF_PENDING_UPDATE_URL);
                ed.Apply();
            }
            catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Single-shot GPS location listener
    // Used by /location to request a fresh fix when no cached fix is available.
    // ─────────────────────────────────────────────────────────────────────────

    internal class SingleShotLocationListener : Java.Lang.Object,
        DroidLocation.ILocationListener
    {
        private readonly Action<AndroidLocation> _callback;
        private bool _fired;

        public SingleShotLocationListener(Action<AndroidLocation> callback)
            => _callback = callback;

        public void OnLocationChanged(AndroidLocation location)
        {
            if (_fired) return;
            _fired = true;
            _callback?.Invoke(location);
        }

        public void OnProviderDisabled(string provider) { }
        public void OnProviderEnabled(string provider) { }
        public void OnStatusChanged(
            string provider,
            DroidLocation.Availability status,
            Android.OS.Bundle extras)
        { }
    }
}