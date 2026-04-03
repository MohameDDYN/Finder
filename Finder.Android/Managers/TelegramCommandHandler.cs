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

// ── Alias to avoid ambiguity with Android.Gms.Location.ILocationListener ──
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

        private const int INTERVAL_COOLDOWN_S = 30;
        private const int STARTUP_COOLDOWN_S = 60;
        private const int RESTART_COOLDOWN_S = 120;

        // ── Poll-interval defaults & limits ───────────────────────────────────
        private const int DEFAULT_POLL_INTERVAL_MS = 60_000;
        private const int MIN_POLL_INTERVAL_MS = 10_000;

        // ── SharedPreferences keys ────────────────────────────────────────────
        private const string LAST_UPDATE_ID_KEY = "telegram_last_update_id";
        private const string PREF_POLL_INTERVAL_MS = "telegram_poll_interval_ms";
        private const string PREF_POLLING_ENABLED = "telegram_polling_enabled";

        // ── Notification channel ──────────────────────────────────────────────
        private const string GPS_ALERT_CHANNEL_ID = "finder_gps_alert_channel";
        private const int GPS_ALERT_NOTIFICATION_ID = 2001;

        // ── Auto-update command ───────────────────────────────────────────────
        private static DateTime _lastUpdateCommand = DateTime.MinValue;
        private const int UPDATE_COOLDOWN_S = 60;

        // Notification channel used for APK download progress display
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
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal),
                "secure_settings.json");

            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _geoJsonManager = new GeoJsonManager(context);

            var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
            _lastUpdateId = prefs.GetLong(LAST_UPDATE_ID_KEY, 0);

            CreateGpsAlertNotificationChannel();

            // Register the notification channel for update download progress
            CreateUpdateNotificationChannel();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Public lifecycle
        // ─────────────────────────────────────────────────────────────────────

        public void Start(bool sendStartupMessage = false)
        {
            try
            {
                var settings = LoadSettings();
                if (string.IsNullOrEmpty(settings.BotToken) ||
                    string.IsNullOrEmpty(settings.ChatId)) return;

                var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
                bool pollingEnabled = prefs.GetBoolean(PREF_POLLING_ENABLED, true);

                if (!pollingEnabled) return;

                int pollIntervalMs = prefs.GetInt(PREF_POLL_INTERVAL_MS, DEFAULT_POLL_INTERVAL_MS);
                if (pollIntervalMs < MIN_POLL_INTERVAL_MS)
                    pollIntervalMs = DEFAULT_POLL_INTERVAL_MS;

                RestartPollTimer(pollIntervalMs);

                if (sendStartupMessage)
                {
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

        // ─────────────────────────────────────────────────────────────────────
        // Poll-timer helpers
        // ─────────────────────────────────────────────────────────────────────

        private void RestartPollTimer(int intervalMs)
        {
            _pollTimer?.Dispose();
            _pollTimer = new Timer(PollForCommands, null, 0, intervalMs);

            var editor = PreferenceManager.GetDefaultSharedPreferences(_context).Edit();
            editor.PutInt(PREF_POLL_INTERVAL_MS, intervalMs);
            editor.Apply();
        }

        private int GetSavedPollIntervalMs()
        {
            var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
            return prefs.GetInt(PREF_POLL_INTERVAL_MS, DEFAULT_POLL_INTERVAL_MS);
        }

        private bool IsPollingEnabled()
        {
            var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
            return prefs.GetBoolean(PREF_POLLING_ENABLED, true);
        }

        private void SetPollingEnabled(bool enabled)
        {
            var editor = PreferenceManager.GetDefaultSharedPreferences(_context).Edit();
            editor.PutBoolean(PREF_POLLING_ENABLED, enabled);
            editor.Apply();
        }

        // ─────────────────────────────────────────────────────────────────────
        // GPS provider helpers
        // ─────────────────────────────────────────────────────────────────────

        private string GetActiveGpsProvider()
        {
            var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
            return prefs.GetString(BackgroundLocationService.PREF_KEY_GPS_PROVIDER, "fused")
                   ?? "fused";
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
                    // ── /gpsprovider fused|raw ────────────────────────────────
                    case "/gpsprovider":
                        switch (param?.ToLower())
                        {
                            case "fused":
                                BroadcastGpsProvider("fused");
                                response = "🔋 *GPS provider → Fused (default)*\n\n" +
                                           "Uses GPS + WiFi + cell towers.\n" +
                                           "The OS manages chip power — battery-efficient.\n\n" +
                                           "✅ Switch applied immediately.\n" +
                                           "ℹ️ /location always uses raw GPS regardless.";
                                break;

                            case "raw":
                                BroadcastGpsProvider("raw");
                                response = "🛰 *GPS provider → Raw GPS*\n\n" +
                                           "Uses the hardware GPS chip directly.\n" +
                                           "Higher accuracy but *higher battery drain*.\n\n" +
                                           "✅ Switch applied immediately.\n" +
                                           "💡 Send /gpsprovider fused to revert.";
                                break;

                            default:
                                string cur = GetActiveGpsProvider();
                                response = $"📡 *GPS Provider*\n\n" +
                                           $"Current: {(cur == "raw" ? "🛰 Raw GPS" : "🔋 Fused (default)")}\n\n" +
                                           "/gpsprovider fused — battery-efficient (default)\n" +
                                           "/gpsprovider raw   — hardware GPS (max accuracy)\n\n" +
                                           "ℹ️ /location *always* uses raw GPS for best accuracy.";
                                break;
                        }
                        break;

                    // ── /interval ────────────────────────────────────────────
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

                            var bIntent = new Intent(BackgroundLocationService.ACTION_UPDATE_INTERVAL);
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
                                response = "⏸ *Polling disabled* — zero network calls.\n" +
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
                                response = $"📡 Polling: *{(en ? "ON ✅" : "OFF ⏸")}*\n" +
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
                                response = $"💾 Saved {pollSec}s — applies on /polling on.";
                            }
                            else
                            {
                                RestartPollTimer(pollMs);
                                response = $"🔄 Poll interval → *{pollSec}s*\n" +
                                           "60s = balanced · 120s = saver · 10s = fastest";
                            }
                        }
                        else
                        {
                            int curSec = GetSavedPollIntervalMs() / 1000;
                            response = $"❌ Usage: /pollinterval [sec] (min 10)\n" +
                                       $"Current: *{curSec}s*";
                        }
                        break;

                    // ── /status ──────────────────────────────────────────────
                    case "/status":
                        var statusFiles = _geoJsonManager.GetAvailableDataFiles();
                        bool sendsPaused = IsTelegramSendingPaused();
                        bool autoStart = Preferences.Get(
                            Finder.ViewModels.MainViewModel.PREF_AUTO_START, false);
                        bool pollEnabled = IsPollingEnabled();
                        int pollSecs = GetSavedPollIntervalMs() / 1000;
                        string activeProvider = GetActiveGpsProvider();
                        string providerLabel = activeProvider == "raw"
                            ? "🛰 Raw GPS (max accuracy)"
                            : "🔋 Fused (battery saver)";

                        response = $"📍 *Status*\n" +
                                   $"Tracking:        {(IsServiceRunning() ? "✅ Active" : "❌ Stopped")}\n" +
                                   $"GPS provider:    {providerLabel}\n" +
                                   $"Telegram sends:  {(sendsPaused ? "⏸ Paused" : "▶️ Active")}\n" +
                                   $"Command polling: {(pollEnabled ? $"✅ Every {pollSecs}s" : "⏸ Disabled")}\n" +
                                   $"Auto-start:      {(autoStart ? "✅ Enabled" : "❌ Disabled")}\n" +
                                   $"Token:           {MaskToken(currentSettings.BotToken)}\n" +
                                   $"Chat ID:         {currentSettings.ChatId}\n" +
                                   $"Send interval:   {currentSettings.Interval} ms\n" +
                                   $"Data files:      {statusFiles.Count}\n" +
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

                        var suppressPrefs = PreferenceManager.GetDefaultSharedPreferences(_context);
                        var suppressEditor = suppressPrefs.Edit();
                        suppressEditor.PutBoolean("suppress_next_startup_message", true);
                        suppressEditor.Apply();

                        StopService();
                        await Task.Delay(2000);
                        StartService();
                        response = "🔄 Service restarted";
                        break;

                    // ── /token ───────────────────────────────────────────────
                    case "/token":
                        if (!string.IsNullOrEmpty(param))
                        {
                            currentSettings.BotToken = param;
                            SaveSettings(currentSettings);
                            response = $"🔑 Token updated: {MaskToken(param)}";
                        }
                        else { response = "❌ Usage: /token [your_bot_token]"; }
                        break;

                    // ── /chatid ──────────────────────────────────────────────
                    case "/chatid":
                        if (!string.IsNullOrEmpty(param))
                        {
                            currentSettings.ChatId = param;
                            SaveSettings(currentSettings);
                            response = $"💬 Chat ID updated: {param}";
                        }
                        else { response = "❌ Usage: /chatid [your_chat_id]"; }
                        break;

                    // ── /today ───────────────────────────────────────────────
                    case "/today":
                        await HandleGeoJsonReportAsync(currentSettings, DateTime.Today);
                        response = null;
                        break;

                    // ── /yesterday ───────────────────────────────────────────
                    case "/yesterday":
                        await HandleGeoJsonReportAsync(currentSettings,
                            DateTime.Today.AddDays(-1));
                        response = null;
                        break;

                    // ── /report ──────────────────────────────────────────────
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

                    // ── /files ───────────────────────────────────────────────
                    case "/files":
                        var files = _geoJsonManager.GetAvailableDataFiles();
                        response = files.Count == 0
                            ? "📂 No data files found."
                            : "📂 *Available files:*\n" +
                              string.Join("\n", files.Select(f => $"• {Path.GetFileName(f)}"));
                        break;

                    // ── /cleanup ─────────────────────────────────────────────
                    case "/cleanup":
                        int keepDays = int.TryParse(param, out int kd) ? kd : 30;
                        int before = _geoJsonManager.GetAvailableDataFiles().Count;
                        await _geoJsonManager.CleanupOldFiles(keepDays);
                        int after = _geoJsonManager.GetAvailableDataFiles().Count;
                        response = $"🧹 Removed {before - after} files older than {keepDays} days.";
                        break;

                    // ── /gpsstatus ───────────────────────────────────────────
                    case "/gpsstatus":
                        bool gpsOn = IsGpsEnabled();
                        bool svcActive = IsServiceRunning();
                        int battery = GetBatteryLevel();
                        string gpsProv = GetActiveGpsProvider();

                        response = "📡 *GPS Status*\n" +
                                   $"GPS chip:  {(gpsOn ? "✅ Enabled" : "❌ Disabled")}\n" +
                                   $"Provider:  {(gpsProv == "raw" ? "🛰 Raw GPS" : "🔋 Fused")}\n" +
                                   $"Service:   {(svcActive ? "✅ Running" : "⏹ Stopped")}\n" +
                                   $"Battery:   {(battery >= 0 ? $"{battery}%" : "Unknown")}\n" +
                                   $"Time:      {DateTime.Now:HH:mm:ss}";
                        if (!gpsOn)
                            response += "\n\n💡 /enablelocation to request GPS activation.";
                        break;

                    // ── /enablelocation ──────────────────────────────────────
                    case "/enablelocation":
                        if (IsGpsEnabled())
                        {
                            response = "✅ GPS is already enabled.";
                        }
                        else
                        {
                            ShowEnableLocationNotification();
                            response = "📲 Notification sent — tap it to open Location Settings.";
                        }
                        break;

                    // ── /location ────────────────────────────────────────────
                    // Always uses raw GPS regardless of the active provider setting.
                    case "/location":
                        await HandleLocationCommandAsync(currentSettings);
                        response = null;
                        break;

                    // ── /pauselocation ───────────────────────────────────────
                    case "/pauselocation":
                        if (IsTelegramSendingPaused())
                        {
                            response = "⏸ Already paused.";
                        }
                        else
                        {
                            BroadcastSendingPaused(true);
                            response = "⏸ *Location sends paused*\n" +
                                       "GPS + GeoJSON still running.\n" +
                                       "Send /resumelocation to turn back on.";
                        }
                        break;

                    // ── /resumelocation ──────────────────────────────────────
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

                    // ── /version ─────────────────────────────────────────────────────────
                    // Reports the currently installed app version back to the admin.
                    // Use this to confirm which version is running before sending /update.
                    case "/version":
                        var runningAssembly = System.Reflection.Assembly.GetExecutingAssembly();
                        var runningVersion = runningAssembly.GetName().Version;
                        response = $"📦 *Finder — Installed Version*\n\n" +
                                   $"Version: `{runningVersion.Major}.{runningVersion.Minor}.{runningVersion.Build}`\n\n" +
                                   $"To push an update:\n" +
                                   $"`/update [version] [url]`\n\n" +
                                   $"Example:\n" +
                                   $"`/update 1.0.2 https://drive.google.com/uc?export=download&id=FILE_ID`";
                        break;

                    // ── /update [version] [url] ──────────────────────────────────────────
                    // Checks if the given version is newer than the installed one.
                    // If newer: downloads the APK from [url] and triggers the installer.
                    // Supported hosts: Google Drive, Dropbox, any direct HTTPS URL.
                    //
                    // Usage:
                    //   /update 1.0.2 https://drive.google.com/uc?export=download&id=FILE_ID
                    //   /update 1.0.2 https://www.dropbox.com/s/xxx/Finder.apk?dl=1
                    case "/update":
                        // Delegate to the dedicated handler — it sends its own Telegram replies
                        await HandleUpdateCommandAsync(param, currentSettings);
                        response = null; // prevent the generic reply below from also sending
                        break;

                    // ── /cmd (help) ──────────────────────────────────────────
                    case "/cmd":
                    default:
                        response = "📋 *Available commands:*\n\n" +
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
                                   "📂 *Data*\n" +
                                   "/today · /yesterday\n" +
                                   "/report YYYY-MM-DD\n" +
                                   "/files · /cleanup [days]\n\n" +
                                   "⚙️ *Config*\n" +
                                   "/token [token] · /chatid [id]\n" +
                                   "⬆️ *App Update*\n" +
                                   "/version — show installed version\n" +
                                   "/update [ver] [url] — push new APK\n\n";
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

                    if (lm != null &&
                        lm.IsProviderEnabled(DroidLocation.LocationManager.GpsProvider))
                    {
                        rawLocation = lm.GetLastKnownLocation(
                            DroidLocation.LocationManager.GpsProvider);
                    }
                }
                catch { }

                // Step 2: stale or missing — request a fresh raw fix (15 s timeout)
                bool isStale = rawLocation != null &&
                    (DateTime.UtcNow -
                     DateTimeOffset.FromUnixTimeMilliseconds(rawLocation.Time).UtcDateTime)
                    .TotalSeconds > 30;

                if (rawLocation == null || isStale)
                    rawLocation = await GetFreshRawGpsFixAsync(timeoutSeconds: 15);

                // Step 3: raw GPS timed out — fall back to Essentials
                if (rawLocation == null)
                {
                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        "⚠️ Raw GPS timed out — falling back to last known location…");

                    var fallback = await Geolocation.GetLastKnownLocationAsync();

                    if (fallback == null)
                    {
                        await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                            "❌ Could not get location.\n" +
                            "Make sure GPS is enabled and you have a clear sky view.");
                        return;
                    }

                    string fbLat = fallback.Latitude.ToString(CultureInfo.InvariantCulture);
                    string fbLon = fallback.Longitude.ToString(CultureInfo.InvariantCulture);
                    string fbMaps = $"https://www.google.com/maps?q={fbLat},{fbLon}";

                    await _httpClient.GetStringAsync(
                        $"https://api.telegram.org/bot{settings.BotToken}" +
                        $"/sendLocation?chat_id={settings.ChatId}" +
                        $"&latitude={fbLat}&longitude={fbLon}");

                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        $"📍 *Last Known Location* _(fallback — not raw GPS)_\n" +
                        $"Lat: `{fbLat}`\nLon: `{fbLon}`\n" +
                        $"Accuracy: {fallback.Accuracy:F0} m\n" +
                        $"[Open in Google Maps]({fbMaps})");
                    return;
                }

                // Step 4: send raw GPS fix
                string lat = rawLocation.Latitude.ToString(CultureInfo.InvariantCulture);
                string lon = rawLocation.Longitude.ToString(CultureInfo.InvariantCulture);
                string maps = $"https://www.google.com/maps?q={lat},{lon}";
                string age = GetFixAgeDescription(rawLocation.Time);

                string accuracy = rawLocation.HasAccuracy
                    ? $"{rawLocation.Accuracy:F0} m" : "unknown";
                string speed = rawLocation.HasSpeed
                    ? $"{rawLocation.Speed * 3.6:F1} km/h" : "—";

                await _httpClient.GetStringAsync(
                    $"https://api.telegram.org/bot{settings.BotToken}" +
                    $"/sendLocation?chat_id={settings.ChatId}" +
                    $"&latitude={lat}&longitude={lon}");

                await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                    $"📍 *Current Location* _(raw GPS)_\n" +
                    $"Lat:      `{lat}`\n" +
                    $"Lon:      `{lon}`\n" +
                    $"Accuracy: {accuracy}\n" +
                    $"Speed:    {speed}\n" +
                    $"Fix age:  {age}\n" +
                    $"[Open in Google Maps]({maps})");
            }
            catch (Exception ex)
            {
                await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                    $"❌ Location error: {ex.Message}");
            }
        }

        /// <summary>
        /// Requests a single fresh raw GPS fix via a one-shot ILocationListener.
        /// Returns null if no fix arrives within <paramref name="timeoutSeconds"/>.
        /// Uses positional long/float args — no named parameters (avoids CS1739).
        /// </summary>
        private Task<AndroidLocation> GetFreshRawGpsFixAsync(int timeoutSeconds)
        {
            var tcs = new TaskCompletionSource<AndroidLocation>();

            try
            {
                var lm = (DroidLocation.LocationManager)_context
                    .GetSystemService(Context.LocationService);

                if (lm == null ||
                    !lm.IsProviderEnabled(DroidLocation.LocationManager.GpsProvider))
                {
                    tcs.TrySetResult(null);
                    return tcs.Task;
                }

                var listener = new SingleShotLocationListener(fix =>
                    tcs.TrySetResult(fix));

                // Positional args: provider, minTimeMs (long), minDistanceM (float), listener
                // Named parameters not supported by Xamarin binding — use positional only
                lm.RequestLocationUpdates(
                    DroidLocation.LocationManager.GpsProvider,
                    0L,    // minTimeMs — long
                    0f,    // minDistanceM — float
                    listener);

                // Timeout guard
                Task.Delay(timeoutSeconds * 1000).ContinueWith(_ =>
                {
                    try { lm.RemoveUpdates(listener); }
                    catch { }
                    tcs.TrySetResult(null);
                });
            }
            catch
            {
                tcs.TrySetResult(null);
            }

            return tcs.Task;
        }

        /// <summary>Returns a human-readable description of how old a GPS fix is.</summary>
        private static string GetFixAgeDescription(long fixTimeMs)
        {
            try
            {
                var age = DateTime.UtcNow -
                          DateTimeOffset.FromUnixTimeMilliseconds(fixTimeMs).UtcDateTime;
                if (age.TotalSeconds < 5) return "just now";
                if (age.TotalSeconds < 60) return $"{(int)age.TotalSeconds}s ago";
                if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes} min ago";
                return $"{(int)age.TotalHours}h ago";
            }
            catch { return "unknown"; }
        }

        // ─────────────────────────────────────────────────────────────────────
        // GeoJSON report handler
        // ─────────────────────────────────────────────────────────────────────

        private async Task HandleGeoJsonReportAsync(AppSettings settings, DateTime date)
        {
            try
            {
                string filePath = _geoJsonManager.GetFilePathForDate(date);
                if (!File.Exists(filePath))
                {
                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        $"📂 No data for {date:yyyy-MM-dd}.");
                    return;
                }

                using var content = new MultipartFormDataContent();
                using var fileStream = File.OpenRead(filePath);
                using var fileContent = new System.Net.Http.StreamContent(fileStream);
                fileContent.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                content.Add(fileContent, "document", Path.GetFileName(filePath));

                string url =
                    $"https://api.telegram.org/bot{settings.BotToken}" +
                    $"/sendDocument?chat_id={settings.ChatId}" +
                    $"&caption=📍 GeoJSON report for {date:yyyy-MM-dd}";
                await _httpClient.PostAsync(url, content);
            }
            catch (Exception ex)
            {
                await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                    $"❌ Report error: {ex.Message}");
            }
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
            var editor = PreferenceManager.GetDefaultSharedPreferences(_context).Edit();
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
                return lm?.IsProviderEnabled(DroidLocation.LocationManager.GpsProvider) == true;
            }
            catch { return false; }
        }

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

                var builder = new NotificationCompat.Builder(_context, GPS_ALERT_CHANNEL_ID)
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
        // Telegram messaging
        // ─────────────────────────────────────────────────────────────────────

        private async Task SendTelegramMessageAsync(
            string botToken, string chatId, string message)
        {
            try
            {
                string url = $"https://api.telegram.org/bot{botToken}/sendMessage" +
                             $"?chat_id={chatId}" +
                             $"&text={Uri.EscapeDataString(message)}" +
                             $"&parse_mode=Markdown";
                await _httpClient.GetStringAsync(url);
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
                File.WriteAllText(_settingsFilePath,
                    JsonConvert.SerializeObject(settings));
            }
            catch { }
        }

        private static string MaskToken(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length <= 8) return "***";
            return token[..4] + "****" + token[^4..];
        }

        // ─────────────────────────────────────────────────────────────────────
        // Notification channel
        // ─────────────────────────────────────────────────────────────────────

        private void CreateGpsAlertNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            try
            {
                var channel = new NotificationChannel(
                    GPS_ALERT_CHANNEL_ID, "GPS Alerts", NotificationImportance.High)
                { Description = "Alerts for GPS state changes" };
                channel.EnableVibration(true);
                var nm = (NotificationManager)_context
                    .GetSystemService(Context.NotificationService);
                nm?.CreateNotificationChannel(channel);
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Auto-update handler
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Full handler for the /update [version] [url] command.
        ///
        /// Flow:
        ///   1. Validate the command parameters (version string + URL).
        ///   2. Compare the requested version against the installed AssemblyVersion.
        ///   3. If newer: download the APK with progress notifications.
        ///   4. On success: trigger the Android package installer.
        ///   5. At each step: send a Telegram status reply to the admin.
        /// </summary>
        private async Task HandleUpdateCommandAsync(string param, AppSettings settings)
        {
            try
            {
                // ── Cooldown guard ────────────────────────────────────────────
                // Prevent rapid repeated /update commands during a download
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
                        "Examples:\n" +
                        "`/update 1.0.2 https://drive.google.com/uc?export=download&id=FILE_ID`\n" +
                        "`/update 1.0.2 https://www.dropbox.com/s/xxx/Finder.apk?dl=1`");
                    return;
                }

                // Split into exactly 2 parts: version + url
                // Use StringSplitOptions.None to preserve the URL even if it has spaces
                string[] updateParts = param.Split(new[] { ' ' }, 2,
                    StringSplitOptions.RemoveEmptyEntries);

                if (updateParts.Length < 2 ||
                    string.IsNullOrWhiteSpace(updateParts[0]) ||
                    string.IsNullOrWhiteSpace(updateParts[1]))
                {
                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        "❌ *Invalid format.*\n\n" +
                        "Usage: `/update [version] [url]`\n\n" +
                        "Both version and URL are required.\n" +
                        "Example: `/update 1.0.2 https://...`");
                    return;
                }

                string requestedVersionStr = updateParts[0].Trim();
                string apkUrl = updateParts[1].Trim();

                // ── Version parsing ───────────────────────────────────────────
                if (!Version.TryParse(requestedVersionStr, out Version requestedVersion))
                {
                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        $"❌ *Invalid version format:* `{requestedVersionStr}`\n\n" +
                        "Use dot-separated numbers: `1.0.2` or `1.0.2.0`");
                    return;
                }

                // ── Version comparison ────────────────────────────────────────
                // Read the version embedded in AssemblyInfo.cs at build time
                var installedVersion = System.Reflection.Assembly
                    .GetExecutingAssembly()
                    .GetName()
                    .Version;

                // Normalize both to Major.Minor.Build for comparison
                var installed3 = new Version(
                    installedVersion.Major,
                    installedVersion.Minor,
                    installedVersion.Build);

                var requested3 = new Version(
                    requestedVersion.Major,
                    requestedVersion.Minor,
                    requestedVersion.Build > 0 ? requestedVersion.Build : 0);

                if (requested3 <= installed3)
                {
                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        $"✅ *Already up to date.*\n\n" +
                        $"Installed: `{installed3.Major}.{installed3.Minor}.{installed3.Build}`\n" +
                        $"Requested: `{requestedVersionStr}` — not newer.\n\n" +
                        "Send `/version` to confirm the running version.");
                    return;
                }

                // ── All checks passed — begin update process ──────────────────
                _lastUpdateCommand = DateTime.Now;

                // Announce update to admin
                await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                    $"📥 *Update queued!*\n\n" +
                    $"Installed: `{installed3.Major}.{installed3.Minor}.{installed3.Build}`\n" +
                    $"New:       `{requestedVersionStr}`\n\n" +
                    "⬇️ Downloading APK now…\n" +
                    "_You will receive another message when it is ready to install._");

                // Show Android progress notification on the device
                ShowUpdateProgressNotification("Downloading Finder update…", 0, done: false);

                // ── Download ──────────────────────────────────────────────────
                var downloader = new Finder.Droid.Services.ApkDownloaderService(_context);

                string apkPath = await downloader.DownloadApkAsync(
                    apkUrl,
                    progress =>
                    {
                        // Update the progress notification every % change
                        ShowUpdateProgressNotification(
                            $"Downloading Finder update… {progress}%",
                            progress,
                            done: false);
                    });

                // ── Download failed ───────────────────────────────────────────
                if (string.IsNullOrEmpty(apkPath))
                {
                    CancelUpdateNotification();
                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        "❌ *Download failed.*\n\n" +
                        "Possible causes:\n" +
                        "• The URL is incorrect or expired\n" +
                        "• Google Drive link is not set to 'Anyone with link'\n" +
                        "• The APK is too large and Google Drive requires manual confirm\n" +
                        "• No internet connection on the device\n\n" +
                        "Please check the URL and try again.");
                    return;
                }

                // ── Download complete — check install permission ──────────────
                ShowUpdateProgressNotification(
                    "Download complete! Tap to install.", 100, done: true);

                // On API 26+: check if the user has enabled "Install unknown apps"
                if (!Finder.Droid.Services.ApkInstaller.CanInstallPackages(_context))
                {
                    // Open the settings screen — the user must enable it manually
                    Finder.Droid.Services.ApkInstaller
                        .OpenInstallPermissionSettings(_context);

                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        "⚠️ *Permission required.*\n\n" +
                        "The device needs 'Install unknown apps' enabled for Finder.\n\n" +
                        "A settings screen has been opened on the device.\n" +
                        "Enable it, then send `/update` again to retry.");
                    return;
                }

                // ── Trigger install ───────────────────────────────────────────
                Finder.Droid.Services.ApkInstaller.Install(_context, apkPath);

                await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                    $"✅ *APK ready! Install prompt launched.*\n\n" +
                    $"Version: `{requestedVersionStr}`\n\n" +
                    "📲 Tap *Install* on the device to complete the update.\n" +
                    "_The app will restart automatically after installation._");
            }
            catch (Exception ex)
            {
                // Catch-all — never let an update error crash the polling loop
                try
                {
                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        $"❌ *Unexpected update error:*\n`{ex.Message}`");
                }
                catch { /* Silent fail — Telegram may be unreachable */ }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Update notification helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Shows or updates the APK download progress notification.
        /// Uses an indeterminate progress bar when progress = 0 (connecting).
        /// Shows a static "done" icon when done = true.
        /// </summary>
        private void ShowUpdateProgressNotification(string message, int progress, bool done)
        {
            try
            {
                var builder = new NotificationCompat.Builder(_context, UPDATE_CHANNEL_ID)
                    .SetContentTitle("Finder Update")
                    .SetContentText(message)
                    .SetSmallIcon(done
                        ? Android.Resource.Drawable.StatSysDownloadDone
                        : Android.Resource.Drawable.StatSysDownload)
                    .SetOngoing(!done)   // Ongoing = cannot be dismissed while downloading
                    .SetAutoCancel(done) // Auto-dismiss when tapped after completion
                    .SetOnlyAlertOnce(true); // No repeated sound/vibration on each update

                if (!done)
                {
                    // Indeterminate bar while connecting (progress == 0),
                    // determinate bar once we have a file size
                    builder.SetProgress(100, progress, progress == 0);
                }

                var nm = (NotificationManager)_context
                    .GetSystemService(Context.NotificationService);
                nm?.Notify(UPDATE_NOTIFICATION_ID, builder.Build());
            }
            catch { /* Silent fail — notification must never crash the download */ }
        }

        /// <summary>Dismisses the update progress notification immediately.</summary>
        private void CancelUpdateNotification()
        {
            try
            {
                var nm = (NotificationManager)_context
                    .GetSystemService(Context.NotificationService);
                nm?.Cancel(UPDATE_NOTIFICATION_ID);
            }
            catch { }
        }

        /// <summary>
        /// Creates the Android notification channel for update progress.
        /// Required on API 26+ (Android 8.0+) — no-op on older versions.
        /// Called once from the constructor.
        /// </summary>
        private void CreateUpdateNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
            try
            {
                var channel = new NotificationChannel(
                    UPDATE_CHANNEL_ID,
                    "App Updates",
                    NotificationImportance.Low) // Low = no sound, just a progress bar
                {
                    Description = "Shows APK download and install progress"
                };

                // No vibration for a progress bar — that would be annoying
                channel.EnableVibration(false);

                var nm = (NotificationManager)_context
                    .GetSystemService(Context.NotificationService);
                nm?.CreateNotificationChannel(channel);
            }
            catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SingleShotLocationListener
    // One-shot raw GPS listener used exclusively by the /location command.
    // Implements DroidLocation.ILocationListener to avoid Gms namespace clash.
    // ─────────────────────────────────────────────────────────────────────────

    internal class SingleShotLocationListener
        : Java.Lang.Object, DroidLocation.ILocationListener
    {
        private readonly Action<AndroidLocation> _onFix;
        private bool _delivered = false;

        public SingleShotLocationListener(Action<AndroidLocation> onFix)
            => _onFix = onFix;

        public void OnLocationChanged(AndroidLocation location)
        {
            if (_delivered) return;
            _delivered = true;
            _onFix?.Invoke(location);
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