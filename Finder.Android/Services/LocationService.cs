using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Finder.Services;
using Xamarin.Essentials;
using Xamarin.Forms;
using Application = Android.App.Application;

// Register this implementation with Xamarin.Forms DependencyService
[assembly: Dependency(typeof(Finder.Droid.Services.LocationService))]
namespace Finder.Droid.Services
{
    /// <summary>
    /// Android implementation of ILocationService.
    /// Starts/stops the BackgroundLocationService foreground service.
    /// </summary>
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
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    _context.StartForegroundService(_serviceIntent);
                else
                    _context.StartService(_serviceIntent);

                // Persist running state so BootReceiver can restore it
                SetTrackingPreference(true);

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
                // Signal the service that this is a user-requested stop (prevents auto-restart)
                BackgroundLocationService.IsStoppingByUserRequest = true;

                SetTrackingPreference(false);
                _context.StopService(_serviceIntent);

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
            finally
            {
                // Reset the flag after a short delay
                Task.Delay(2000).ContinueWith(_ =>
                {
                    BackgroundLocationService.IsStoppingByUserRequest = false;
                });
            }
        }

        /// <summary>Gets the last known or current GPS location.</summary>
        public async Task<Location> GetCurrentLocation()
        {
            try
            {
                // Try last known location first (faster)
                var location = await Geolocation.GetLastKnownLocationAsync();

                if (location == null)
                {
                    // Fall back to fresh location request
                    var request = new GeolocationRequest(
                        GeolocationAccuracy.Medium,
                        TimeSpan.FromSeconds(10));
                    location = await Geolocation.GetLocationAsync(request);
                }

                return location;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LocationService] GetCurrentLocation error: {ex.Message}");
                return null;
            }
        }

        /// <summary>Returns true if the background service is currently running.</summary>
        public Task<bool> IsTrackingActive()
        {
            var prefs = Android.Preferences.PreferenceManager
                .GetDefaultSharedPreferences(_context);
            bool isRunning = prefs.GetBoolean("is_tracking_service_running", false);
            return Task.FromResult(isRunning);
        }

        // ── Helper ─────────────────────────────────────────────────────────

        private void SetTrackingPreference(bool running)
        {
            var prefs = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(_context);
            var editor = prefs.Edit();
            editor.PutBoolean("is_tracking_service_running", running);
            editor.Apply();
        }
    }
}