using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Preferences;
using Finder.Droid.Services;
using Finder.Models;
using Newtonsoft.Json;
using Xamarin.Essentials;

namespace Finder.Droid.Managers
{
    /// <summary>
    /// Polls Telegram for bot commands and executes them.
    /// Runs a 10-second polling timer.
    /// Supported commands: /interval, /status, /start, /stop,
    ///   /restart, /token, /chatid, /report, /today,
    ///   /yesterday, /files, /cleanup, /cmd
    /// </summary>
    public class TelegramCommandHandler
    {
        // ── Anti-spam cooldowns ────────────────────────────────────────────
        private static DateTime _lastIntervalUpdate = DateTime.MinValue;
        private static DateTime _lastStartupMessage = DateTime.MinValue;
        private static DateTime _lastRestartCommand = DateTime.MinValue;
        private const int INTERVAL_COOLDOWN_S = 30;
        private const int STARTUP_COOLDOWN_S = 60;
        private const int RESTART_COOLDOWN_S = 120;

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
        }

        // ── Start / Stop ───────────────────────────────────────────────────

        public void Start()
        {
            try
            {
                var settings = LoadSettings();
                if (string.IsNullOrEmpty(settings.BotToken) ||
                    string.IsNullOrEmpty(settings.ChatId)) return;

                _pollTimer = new Timer(PollForCommands, null, 0, 10000);

                // Send startup notification (with cooldown to prevent spam on restart)
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
                        await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                            "🤖 Finder service started");
                    });
                }
            }
            catch { /* Silent fail */ }
        }

        public void Stop()
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
        }

        // ── Polling ────────────────────────────────────────────────────────

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

                    // Only process messages from the configured chat
                    if (update.Message?.Chat?.Id.ToString() != settings.ChatId) continue;

                    string text = update.Message?.Text;
                    if (!string.IsNullOrEmpty(text) && text.StartsWith("/"))
                        await ProcessCommandAsync(text, settings);
                }
            }
            catch { /* Silent fail */ }
        }

        // ── Command dispatch ───────────────────────────────────────────────

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
                    // ── /interval ─────────────────────────────────────────
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

                            // Notify running service via broadcast
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

                    // ── /status ───────────────────────────────────────────
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

                    // ── /start ────────────────────────────────────────────
                    case "/start":
                        StartService();
                        response = "✅ Location tracking started";
                        break;

                    // ── /stop ─────────────────────────────────────────────
                    case "/stop":
                        StopService();
                        response = "⏹ Location tracking stopped";
                        break;

                    // ── /restart ──────────────────────────────────────────
                    case "/restart":
                        if ((DateTime.Now - _lastRestartCommand).TotalSeconds < RESTART_COOLDOWN_S)
                        {
                            int remaining = RESTART_COOLDOWN_S -
                                (int)(DateTime.Now - _lastRestartCommand).TotalSeconds;
                            response = $"⏳ Please wait {remaining}s before restarting again.";
                            break;
                        }

                        _lastRestartCommand = DateTime.Now;
                        await SendTelegramMessageAsync(currentSettings.BotToken,
                            currentSettings.ChatId, "🔄 Restarting service…");

                        // Suppress startup message on the next launch
                        var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
                        var editor = prefs.Edit();
                        editor.PutBoolean("suppress_next_startup_message", true);
                        editor.Apply();

                        Stop();
                        StopService();
                        await Task.Delay(2000);
                        StartService();

                        response = "✅ Service restarted successfully";
                        break;

                    // ── /token ────────────────────────────────────────────
                    case "/token":
                        if (!string.IsNullOrEmpty(param))
                        {
                            currentSettings.BotToken = param;
                            SaveSettings(currentSettings);
                            await SecureStorage.SetAsync("bot_token", param);
                            response = "🔑 Bot token updated. Restart tracking to apply.";
                        }
                        else response = "❌ Usage: /token [your_bot_token]";
                        break;

                    // ── /chatid ───────────────────────────────────────────
                    case "/chatid":
                        if (!string.IsNullOrEmpty(param))
                        {
                            currentSettings.ChatId = param;
                            SaveSettings(currentSettings);
                            await SecureStorage.SetAsync("chat_id", param);
                            response = "💬 Chat ID updated. Restart tracking to apply.";
                        }
                        else response = "❌ Usage: /chatid [your_chat_id]";
                        break;

                    // ── /report ───────────────────────────────────────────
                    case "/report":
                        if (!string.IsNullOrEmpty(param) &&
                            DateTime.TryParse(param, out DateTime rDate))
                        {
                            await SendGeoJsonReportAsync(rDate, currentSettings);
                            return;
                        }
                        else response = "❌ Usage: /report YYYY-MM-DD";
                        break;

                    case "/today":
                        await SendGeoJsonReportAsync(DateTime.Today, currentSettings);
                        return;

                    case "/yesterday":
                        await SendGeoJsonReportAsync(DateTime.Today.AddDays(-1), currentSettings);
                        return;

                    // ── /files ────────────────────────────────────────────
                    case "/files":
                        var avail = _geoJsonManager.GetAvailableDataFiles();
                        if (avail.Count == 0)
                        {
                            response = "📁 No data files found yet.";
                        }
                        else
                        {
                            response = $"📁 {avail.Count} file(s):\n\n";
                            foreach (var f in avail.Take(10))
                            {
                                string datePart = Path.GetFileNameWithoutExtension(f);
                                if (datePart.StartsWith("locations_"))
                                    datePart = datePart.Substring(10);
                                response += $"📄 {datePart}\n";
                            }
                            if (avail.Count > 10)
                                response += $"… and {avail.Count - 10} more";
                            response += "\n\nUse /report YYYY-MM-DD to retrieve a file.";
                        }
                        break;

                    // ── /cleanup ──────────────────────────────────────────
                    case "/cleanup":
                        int keepDays = int.TryParse(param, out int d) && d > 0 ? d : 30;
                        int before = _geoJsonManager.GetAvailableDataFiles().Count;
                        await _geoJsonManager.CleanupOldFiles(keepDays);
                        int after = _geoJsonManager.GetAvailableDataFiles().Count;
                        response = $"🧹 Cleanup done · Removed {before - after} files older than {keepDays} days";
                        break;

                    // ── /cmd ──────────────────────────────────────────────
                    case "/cmd":
                    default:
                        response = "📋 Available commands:\n" +
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
                                   "/cleanup [days] — Delete old files";
                        break;
                }

                await SendTelegramMessageAsync(currentSettings.BotToken,
                    currentSettings.ChatId, response);
            }
            catch { /* Silent fail */ }
        }

        // ── GeoJSON report sender ──────────────────────────────────────────

        private async Task SendGeoJsonReportAsync(DateTime date, AppSettings settings)
        {
            try
            {
                string geoJson = await _geoJsonManager.GenerateGeoJsonForDate(date);

                if (string.IsNullOrEmpty(geoJson))
                {
                    await SendTelegramMessageAsync(settings.BotToken, settings.ChatId,
                        $"📊 No data for {date:yyyy-MM-dd}");
                    return;
                }

                var collection = JsonConvert.DeserializeObject<GeoJsonFeatureCollection>(geoJson);
                string summary = $"📊 Report: {date:yyyy-MM-dd}\n" +
                                 $"Points: {collection.Metadata.TotalPoints}\n" +
                                 $"Distance: {collection.Metadata.DistanceTraveledKm:F2} km\n" +
                                 $"Duration: {collection.Metadata.TrackingDurationHours:F1} h";

                await SendTelegramMessageAsync(settings.BotToken, settings.ChatId, summary);

                string tempFile = Path.Combine(
                    Path.GetTempPath(), $"finder_report_{date:yyyy-MM-dd}.geojson");
                await File.WriteAllTextAsync(tempFile, geoJson);
                await SendFileToTelegramAsync(tempFile, $"GeoJSON · {date:yyyy-MM-dd}", settings);

                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
            catch { /* Silent fail */ }
        }

        private async Task SendFileToTelegramAsync(string path, string caption, AppSettings settings)
        {
            try
            {
                using (var form = new MultipartFormDataContent())
                {
                    var bytes = await File.ReadAllBytesAsync(path);
                    var content = new ByteArrayContent(bytes);
                    content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue("application/geo+json");
                    form.Add(content, "document", Path.GetFileName(path));
                    form.Add(new StringContent(settings.ChatId), "chat_id");
                    form.Add(new StringContent(caption), "caption");

                    await _httpClient.PostAsync(
                        $"https://api.telegram.org/bot{settings.BotToken}/sendDocument", form);
                }
            }
            catch { /* Silent fail */ }
        }

        // ── Telegram messaging ─────────────────────────────────────────────

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
            catch { /* Silent fail */ }
        }

        // ── Service control ────────────────────────────────────────────────

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
                if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O)
                    _context.StartForegroundService(intent);
                else
                    _context.StartService(intent);

                var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
                var editor = prefs.Edit();
                editor.PutBoolean("is_tracking_service_running", true);
                editor.Apply();
            }
            catch { /* Silent fail */ }
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
            catch { /* Silent fail */ }
        }

        // ── Settings helpers ───────────────────────────────────────────────

        private AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                    return JsonConvert.DeserializeObject<AppSettings>(
                        File.ReadAllText(_settingsFilePath)) ?? new AppSettings();
            }
            catch { /* Silent fail */ }
            return new AppSettings();
        }

        private void SaveSettings(AppSettings settings)
        {
            try { File.WriteAllText(_settingsFilePath, JsonConvert.SerializeObject(settings)); }
            catch { /* Silent fail */ }
        }

        private string MaskToken(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length < 8) return "Not set";
            return token.Substring(0, 6) + "•••" + token.Substring(token.Length - 4);
        }
    }
}