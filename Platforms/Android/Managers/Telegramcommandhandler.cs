using System.Text.Json;
using Android.Content;
using Android.Locations;
using Finder.Models.Telegram;
using Finder.Platforms.Android.Services;
using Finder.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;

namespace Finder.Platforms.Android.Managers
{
    /// <summary>
    /// Long-polls Telegram's getUpdates endpoint and dispatches commands.
    /// Runs entirely on plain HttpClient — no Telegram SDK needed.
    ///
    /// This step wires up /start /help /status /version only. /location lands
    /// in Step 5, /update in Step 8.
    /// </summary>
    public class TelegramCommandHandler
    {
        private static readonly HttpClient HttpClient = new()
        {
            // Telegram's own long-poll timeout is 30s; give a little headroom.
            Timeout = TimeSpan.FromSeconds(35)
        };

        private readonly Context _context;
        private CancellationTokenSource? _cts;

        private string _botToken = string.Empty;
        private string _allowedChatId = string.Empty;

        public TelegramCommandHandler(Context context)
        {
            _context = context;
        }

        public async Task StartAsync(bool explicitUserStart)
        {
            // The service runs outside MAUI's page/DI lifecycle, so we reach into
            // the app's service provider directly rather than constructor-injecting.
            var settingsService = IPlatformApplication.Current?.Services.GetService<ISettingsService>();
            if (settingsService is null) return;

            var settings = await settingsService.LoadAsync();
            _botToken = settings.BotToken;
            _allowedChatId = settings.ChatId;

            if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(_allowedChatId))
            {
                // Nothing configured yet — the Save button on MainPage is how
                // these normally get populated before Start is ever pressed.
                return;
            }

            _cts = new CancellationTokenSource();

            if (explicitUserStart)
            {
                await SendMessageAsync(BuildWelcomeText());
            }

            _ = Task.Run(() => PollLoopAsync(_cts.Token));
        }

        public void Stop()
        {
            _cts?.Cancel();
        }

        private async Task PollLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var offset = Preferences.Default.Get(PreferenceKeys.LastUpdateId, 0L);
                    var url = $"https://api.telegram.org/bot{_botToken}/getUpdates?timeout=30&offset={offset}";

                    var response = await HttpClient.GetAsync(url, token);
                    if (!response.IsSuccessStatusCode)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), token);
                        continue;
                    }

                    var json = await response.Content.ReadAsStringAsync(token);
                    var parsed = JsonSerializer.Deserialize<TelegramGetUpdatesResponse>(json);

                    if (parsed?.Result is { Count: > 0 })
                    {
                        foreach (var update in parsed.Result)
                        {
                            await HandleUpdateAsync(update);
                            Preferences.Default.Set(PreferenceKeys.LastUpdateId, update.UpdateId + 1);
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    // Expected on Stop() or a request timeout; loop exits on the
                    // cancellation check above if this came from Stop().
                }
                catch
                {
                    // Network hiccup, Telegram outage, malformed response, etc.
                    // Must never crash the foreground service — back off and retry.
                    try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
                    catch (TaskCanceledException) { }
                }
            }
        }

        private async Task HandleUpdateAsync(TelegramUpdate update)
        {
            var message = update.Message;
            if (message?.Text is null) return;

            // Only respond to the configured chat — this is what stops anyone
            // else who obtains the bot token from being able to issue commands.
            if (message.Chat.Id.ToString() != _allowedChatId) return;

            var command = message.Text.Trim().Split(' ')[0].ToLowerInvariant();

            switch (command)
            {
                case "/start":
                    await SendMessageAsync(BuildWelcomeText());
                    break;
                case "/help":
                    await SendMessageAsync(BuildHelpText());
                    break;
                case "/status":
                    await SendMessageAsync(BuildStatusText());
                    break;
                case "/version":
                    await SendMessageAsync($"App version: {AppInfo.Current.VersionString} (build {AppInfo.Current.BuildString})");
                    break;
                    // /location (Step 5) and /update (Step 8) added later.
            }
        }

        private async Task SendMessageAsync(string text)
        {
            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage" +
                      $"?chat_id={Uri.EscapeDataString(_allowedChatId)}" +
                      $"&text={Uri.EscapeDataString(text)}";

            try
            {
                await HttpClient.GetAsync(url);
            }
            catch
            {
                // Nothing useful to do here but let the next command attempt again.
            }
        }

        private static string BuildWelcomeText() =>
            "Finder is online.\n\n" + BuildHelpText();

        private static string BuildHelpText() =>
            "Available commands:\n" +
            "/status — service, GPS, and battery status\n" +
            "/version — installed app version\n" +
            "/location — get current GPS location (coming soon)\n" +
            "/update <version> <url> — remote update (coming soon)";

        private string BuildStatusText()
        {
            var locationManager = _context.GetSystemService(Context.LocationService) as LocationManager;
            var gpsEnabled = locationManager?.IsProviderEnabled(LocationManager.GpsProvider) ?? false;

            var batteryPercent = (int)(Battery.ChargeLevel * 100);

            return "Status:\n" +
                   "Service: running\n" +
                   $"GPS enabled: {(gpsEnabled ? "yes" : "no")}\n" +
                   $"Battery: {batteryPercent}%\n" +
                   $"App version: {AppInfo.Current.VersionString}\n" +
                   $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
    }
}