using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Finder.Services;
using Xamarin.Essentials;
using Xamarin.Forms;
using Application = Android.App.Application;

[assembly: Dependency(typeof(Finder.Droid.Services.LocationService))]
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

        public Task StartTracking()
        {
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    _context.StartForegroundService(_serviceIntent);
                else
                    _context.StartService(_serviceIntent);

                // Write true here — OnStartCommand also writes true as a safety net
                SetTrackingPreference(true);

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        public Task StopTracking()
        {
            try
            {
                // Signal OnDestroy that this is a deliberate user stop
                BackgroundLocationService.IsStoppingByUserRequest = true;

                // Write false HERE — this is the authoritative write for the
                // user-stop path. OnDestroy will also check the flag and write
                // false too, but this ensures the preference is updated
                // immediately and atomically with the user's intent.
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
                // Reset the flag after a delay so OnDestroy's auto-restart
                // logic has time to check it before it is cleared
                Task.Delay(2000).ContinueWith(_ =>
                {
                    BackgroundLocationService.IsStoppingByUserRequest = false;
                });
            }
        }

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
                System.Diagnostics.Debug.WriteLine($"[LocationService] GetCurrentLocation error: {ex.Message}");
                return null;
            }
        }

        public Task<bool> IsTrackingActive()
        {
            var prefs = Android.Preferences.PreferenceManager
                .GetDefaultSharedPreferences(_context);
            bool isRunning = prefs.GetBoolean("is_tracking_service_running", false);
            return Task.FromResult(isRunning);
        }

        private void SetTrackingPreference(bool running)
        {
            var prefs = Android.Preferences.PreferenceManager.GetDefaultSharedPreferences(_context);
            var editor = prefs.Edit();
            editor.PutBoolean("is_tracking_service_running", running);
            editor.Apply();
        }
    }
}