using Android.Content;
using Finder.Services;
using AndroidApp = Android.App.Application;

namespace Finder.Platforms.Android.Services
{
    /// <summary>
    /// Android implementation of the shared ILocationService contract.
    /// Just starts/stops BackgroundLocationService — all the actual
    /// foreground-service lifecycle logic lives there.
    /// </summary>
    public class LocationService : ILocationService
    {
        public bool IsRunning => BackgroundLocationService.IsRunning;

        public Task StartTracking()
        {
            Microsoft.Maui.Storage.Preferences.Default.Set(PreferenceKeys.WasRunning, true);

            var context = AndroidApp.Context;
            var intent = new Intent(context, typeof(BackgroundLocationService));
            intent.PutExtra("explicit_user_start", true);

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                context.StartForegroundService(intent);
            else
                context.StartService(intent);

            return Task.CompletedTask;
        }

        public Task StopTracking()
        {
            // Must be set BEFORE StopService — OnDestroy checks this flag to
            // decide whether to treat the stop as user-requested or OS-killed.
            BackgroundLocationService.RequestStop();

            var context = AndroidApp.Context;
            var intent = new Intent(context, typeof(BackgroundLocationService));
            context.StopService(intent);

            Microsoft.Maui.Storage.Preferences.Default.Set(PreferenceKeys.WasRunning, false);

            return Task.CompletedTask;
        }
    }
}