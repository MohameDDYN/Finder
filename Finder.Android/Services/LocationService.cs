using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Preferences;
using Finder.Droid.Managers;
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

        /// <summary>Starts the foreground location tracking service.</summary>
        public Task StartTracking()
        {
            try
            {
                // Stop the app-side handler before the service starts.
                // The service will create its own TelegramCommandHandler instance.
                AppCommandHandler.Stop();

                // Tag the intent so BackgroundLocationService knows this is an
                // explicit user action and should send the Telegram startup message.
                var intent = new Intent(_context, typeof(BackgroundLocationService));
                intent.PutExtra("explicit_user_start", true);

                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    _context.StartForegroundService(intent);
                else
                    _context.StartService(intent);

                SetTrackingPreference(true);
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
                // Set before StopService() so OnDestroy() skips the auto-restart logic.
                BackgroundLocationService.IsStoppingByUserRequest = true;

                SetTrackingPreference(false);
                _context.StopService(_serviceIntent);
                WatchdogJobService.Cancel(_context);

                // Hand Telegram polling back to the app now that the service is stopped.
                // MainActivity.OnResume also does this as a safety net, but calling it
                // here ensures the bot responds immediately while the app is visible.
                AppCommandHandler.Start(_context, sendStartupMessage: false);

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
            finally
            {
                // Reset after a delay so OnDestroy() has time to read the flag.
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
        /// Uses PreferenceManager.GetDefaultSharedPreferences — the same storage
        /// used by BackgroundLocationService and BootReceiver.
        /// </summary>
        public Task<bool> IsTrackingActive()
        {
            var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
            bool running = prefs.GetBoolean("is_tracking_service_running", false);
            return Task.FromResult(running);
        }

        private void SetTrackingPreference(bool running)
        {
            var prefs = PreferenceManager.GetDefaultSharedPreferences(_context);
            var editor = prefs.Edit();
            editor.PutBoolean("is_tracking_service_running", running);
            editor.Apply();
        }
    }
}