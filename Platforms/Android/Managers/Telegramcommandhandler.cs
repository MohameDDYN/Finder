using System.Globalization;
using System.Text.Json;
using Android.Content;
using Android.Locations;
using Android.OS;
using Finder.Models.Telegram;
using Finder.Platforms.Android.Services;
using Finder.Services;
using Java.Lang;
using AndroidLocation = Android.Locations.Location;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace Finder.Platforms.Android.Managers
{
    /// <summary>
    /// Long-polls Telegram's getUpdates endpoint and dispatches commands.
    /// Runs entirely on plain HttpClient — no Telegram SDK needed.
    ///
    /// This step adds /location on top of /start /help /status /version.
    /// /update lands in Step 8.
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
                            try
                            {
                                await HandleUpdateAsync(update);
                            }
                            catch (System.Exception ex) //deyenem
                            {
                                // Surface handler errors back to Telegram instead of
                                // failing completely silently — makes remote debugging
                                // possible without adb/a debugger attached.
                                await SendMessageAsync($"Error handling command: {ex.GetType().Name}: {ex.Message}");
                            }

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
                case "/location":
                    await HandleLocationCommandAsync();
                    break;
                    // /update (Step 8) added later.
            }
        }

        private async Task HandleLocationCommandAsync()
        {
            var status = await Microsoft.Maui.ApplicationModel.Permissions.CheckStatusAsync<Microsoft.Maui.ApplicationModel.Permissions.LocationWhenInUse>();
            if (status != Microsoft.Maui.ApplicationModel.PermissionStatus.Granted)
            {
                await SendMessageAsync("Location permission isn't granted. Open the app and grant it in Settings.");
                return;
            }

            var locationManager = _context.GetSystemService(Context.LocationService) as LocationManager;
            if (locationManager is null)
            {
                await SendMessageAsync("Location service isn't available on this device.");
                return;
            }

            await SendMessageAsync("Getting location…");

            var location = await GetLocationAsync(locationManager);
            if (location is null)
            {
                await SendMessageAsync("Couldn't get a GPS fix. Make sure location is enabled and try again.");
                return;
            }

            await SendLocationPinAsync(location.Latitude, location.Longitude);

            var batteryPercent = GetBatteryPercent();
            var lat = location.Latitude.ToString("F6", CultureInfo.InvariantCulture);
            var lng = location.Longitude.ToString("F6", CultureInfo.InvariantCulture);
            var mapsLink = $"https://www.google.com/maps?q={lat},{lng}";

            var text =
                $"Latitude: {lat}\n" +
                $"Longitude: {lng}\n" +
                $"Altitude: {(location.HasAltitude ? $"{location.Altitude:F1} m" : "n/a")}\n" +
                $"Accuracy: {(location.HasAccuracy ? $"{location.Accuracy:F1} m" : "n/a")}\n" +
                $"Speed: {(location.HasSpeed ? $"{location.Speed:F1} m/s" : "n/a")}\n" +
                $"Battery: {(batteryPercent >= 0 ? $"{batteryPercent}%" : "n/a")}\n" +
                $"Map: {mapsLink}";

            await SendMessageAsync(text);
        }

        /// <summary>
        /// Prefers a recent last-known fix (cheap, instant) over requesting a
        /// fresh one, since a fresh fix costs GPS warm-up time and battery.
        /// Only falls back to a fresh fix if the last-known one is missing or
        /// older than 60 seconds.
        /// </summary>
        private async Task<AndroidLocation?> GetLocationAsync(LocationManager locationManager)
        {
            AndroidLocation? lastKnown = null;
            try { lastKnown = locationManager.GetLastKnownLocation(LocationManager.GpsProvider); }
            catch { /* provider may not exist on this device */ }

            if (lastKnown is null)
            {
                try { lastKnown = locationManager.GetLastKnownLocation(LocationManager.NetworkProvider); }
                catch { /* network location provider may be disabled */ }
            }

            if (lastKnown is not null)
            {
                var ageMs = JavaSystem.CurrentTimeMillis() - lastKnown.Time;
                if (ageMs < 60_000)
                {
                    return lastKnown;
                }
            }

            return await RequestFreshFixAsync(locationManager, TimeSpan.FromSeconds(30));
        }

        private async Task<AndroidLocation?> RequestFreshFixAsync(LocationManager locationManager, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<AndroidLocation?>();
            var listener = new SingleLocationListener(locationManager, tcs);

            try
            {
                var provider = locationManager.IsProviderEnabled(LocationManager.GpsProvider)
                    ? LocationManager.GpsProvider
                    : LocationManager.NetworkProvider;

                // Passing Looper.MainLooper explicitly means this call is safe from
                // any thread — the callback just runs on the main thread instead of
                // requiring the calling thread to have its own Looper.
                locationManager.RequestLocationUpdates(provider, 0, 0, listener, Looper.MainLooper!);
            }
            catch
            {
                return null;
            }

            using var cts = new CancellationTokenSource(timeout);
            using var registration = cts.Token.Register(() => tcs.TrySetResult(null));

            var result = await tcs.Task;
            try { locationManager.RemoveUpdates(listener); } catch { }
            return result;
        }

        /// <summary>
        /// One-shot ILocationListener: resolves the TaskCompletionSource and
        /// immediately unsubscribes itself as soon as a single fix arrives.
        /// </summary>
        private sealed class SingleLocationListener : Java.Lang.Object, ILocationListener
        {
            private readonly LocationManager _locationManager;
            private readonly TaskCompletionSource<AndroidLocation?> _tcs;

            public SingleLocationListener(LocationManager locationManager, TaskCompletionSource<AndroidLocation?> tcs)
            {
                _locationManager = locationManager;
                _tcs = tcs;
            }

            public void OnLocationChanged(AndroidLocation location)
            {
                _tcs.TrySetResult(location);
                try { _locationManager.RemoveUpdates(this); } catch { }
            }

            public void OnProviderDisabled(string provider) { }
            public void OnProviderEnabled(string provider) { }
            public void OnStatusChanged(string? provider, Availability status, Bundle? extras) { }
        }

        private async Task SendLocationPinAsync(double latitude, double longitude)
        {
            var lat = latitude.ToString(CultureInfo.InvariantCulture);
            var lng = longitude.ToString(CultureInfo.InvariantCulture);

            var url = $"https://api.telegram.org/bot{_botToken}/sendLocation" +
                      $"?chat_id={Uri.EscapeDataString(_allowedChatId)}" +
                      $"&latitude={lat}&longitude={lng}";

            try
            {
                await HttpClient.GetAsync(url);
            }
            catch
            {
                // If the pin fails to send, the formatted text message that
                // follows still carries the coordinates.
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
            "/location — get current GPS location\n" +
            "/update <version> <url> — remote update (coming soon)";

        /// <summary>
        /// Reads battery percentage via the ACTION_BATTERY_CHANGED sticky broadcast
        /// instead of MAUI's Battery.ChargeLevel API. Some devices (notably certain
        /// Samsung builds) throw a PermissionException for BATTERY_STATS from that
        /// API's internal implementation — a system/signature permission ordinary
        /// apps can never actually hold. This approach needs no permission at all.
        /// </summary>
        private int GetBatteryPercent()
        {
            try
            {
                var filter = new IntentFilter(Intent.ActionBatteryChanged);
                var batteryStatus = _context.RegisterReceiver(null, filter);
                if (batteryStatus is null) return -1;

                var level = batteryStatus.GetIntExtra(BatteryManager.ExtraLevel, -1);
                var scale = batteryStatus.GetIntExtra(BatteryManager.ExtraScale, -1);

                if (level < 0 || scale <= 0) return -1;

                return (int)(level * 100f / scale);
            }
            catch
            {
                return -1;
            }
        }

        private string BuildStatusText()
        {
            var locationManager = _context.GetSystemService(Context.LocationService) as LocationManager;
            var gpsEnabled = locationManager?.IsProviderEnabled(LocationManager.GpsProvider) ?? false;

            var batteryPercent = GetBatteryPercent();

            return "Status:\n" +
                   "Service: running\n" +
                   $"GPS enabled: {(gpsEnabled ? "yes" : "no")}\n" +
                   $"Battery: {(batteryPercent >= 0 ? $"{batteryPercent}%" : "n/a")}\n" +
                   $"App version: {AppInfo.Current.VersionString}\n" +
                   $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
    }
}