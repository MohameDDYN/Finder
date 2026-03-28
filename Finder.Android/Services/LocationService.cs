using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Preferences;
using Finder.Droid.Services;
using Finder.Services;
using Xamarin.Essentials;
using Xamarin.Forms;
using Application = Android.App.Application;

[assembly: Dependency(typeof(LocationService))]
namespace Finder.Droid.Services
{
    public class LocationService : ILocationService
    {
        private readonly Context _context;
        private readonly Intent _serviceIntent;

        public LocationService()
        {
            _context = Application.Context;
            _serviceIntent = new Intent(_context, typeof(BackgroundLocationService));
        }

        /// <summary>
        /// Starts the foreground location tracking service.
        /// Tags the intent with explicit_user_start = true so
        /// BackgroundLocationService knows to send the Telegram
        /// "service started" message — auto-restarts never set this flag.
        /// </summary>
        public Task StartTracking()
        {
            try
            {
                // Build a new intent with the explicit user start flag.
                // This is the ONLY code path that sets explicit_user_start = true.
                // BootReceiver, OnDestroy auto-restart, WatchdogJobService and
                // RestartReceiver all start the service WITHOUT this extra, so
                // the startup message is never sent on those paths.
                var intent = new Intent(_context, typeof(BackgroundLocationService));
                intent.PutExtra("explicit_user_start", true);

                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    _context.StartForegroundService(intent);
                else
                    _context.StartService(intent);

                // Write true using the same PreferenceManager as the service
                // and BootReceiver. All three files must use identical storage.
                SetTrackingPreference(true);

                // Ensure the watchdog is scheduled whenever tracking starts.
                WatchdogJobService.Schedule(_context);

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        /// <summary>Stops the foreground location tracking service.</summary>
        public Task StopTracking()
        {
            try
            {
                // Must be set BEFORE StopService() so OnDestroy() sees it as true
                // and skips the auto-restart logic.
                BackgroundLocationService.IsStoppingByUserRequest = true;

                // Authoritative write — ensures the preference is correct immediately.
                // OnDestroy() checks the flag and also writes false, but this
                // write ensures the preference is correct immediately.
                SetTrackingPreference(false);

                _context.StopService(_serviceIntent);

                // Cancel the watchdog — no need to watch a stopped service.
                WatchdogJobService.Cancel(_context);

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
            finally
            {
                // Reset the flag after a delay so OnDestroy() has time to read it.
                Task.Delay(2000).ContinueWith(_ =>
                {
                    BackgroundLocationService.IsStoppingByUserRequest = false;
                });
            }
        }

        /// <summary>Returns the most recent known device location.</summary>
        public async Task<Location> GetCurrentLocation()
        {
            try
            {
                var location = await Geolocation.GetLastKnownLocationAsync();
                if (location == null)
                {
                    var request = new GeolocationRequest(
                        GeolocationAccuracy.Medium,
                        TimeSpan.FromSeconds(10));
                    location = await Geolocation.GetLocationAsync(request);
                }
                return location;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[LocationService] GetCurrentLocation error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns true if the tracking service is currently active.
        /// MUST use PreferenceManager.GetDefaultSharedPreferences — the exact
        /// same API used by BackgroundLocationService and BootReceiver.
        /// Using any other storage (Xamarin.Essentials.Preferences, SecureStorage)
        /// reads from a different file and always returns false.
        /// </summary>
        public Task<bool> IsTrackingActive()
        {
            var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
            bool running = prefs.GetBoolean("is_tracking_service_running", false);
            return Task.FromResult(running);
        }

        // ── Shared helper ─────────────────────────────────────────────────────
        // Same key and same API used by BackgroundLocationService and BootReceiver.
        private void SetTrackingPreference(bool running)
        {
            var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
            var editor = prefs.Edit();
            editor.PutBoolean("is_tracking_service_running", running);
            editor.Apply();
        }
    }
}