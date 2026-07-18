using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using CommunityToolkit.Mvvm.Messaging;
using Finder.Models;
using Finder.Platforms.Android.Permissions;

namespace Finder
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override async void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            await RequestStartupPermissionsAsync();
        }

        private async Task RequestStartupPermissionsAsync()
        {
            await RequestLocationPermissionAsync();
            await RequestNotificationPermissionAsync();
        }

        private async Task RequestLocationPermissionAsync()
        {
            var status = await Microsoft.Maui.ApplicationModel.Permissions.CheckStatusAsync<Microsoft.Maui.ApplicationModel.Permissions.LocationWhenInUse>();

            if (status != Microsoft.Maui.ApplicationModel.PermissionStatus.Granted)
            {
                status = await Microsoft.Maui.ApplicationModel.Permissions.RequestAsync<Microsoft.Maui.ApplicationModel.Permissions.LocationWhenInUse>();
            }

            if (status != Microsoft.Maui.ApplicationModel.PermissionStatus.Granted)
            {
                WeakReferenceMessenger.Default.Send(new PermissionDeniedMessage("Location"));
            }
        }

        private async Task RequestNotificationPermissionAsync()
        {
            // POST_NOTIFICATIONS only exists as a runtime permission on API 33+.
            // On older versions this is a no-op and notifications just work.
            if (!OperatingSystem.IsAndroidVersionAtLeast(33))
                return;

            var status = await Microsoft.Maui.ApplicationModel.Permissions.CheckStatusAsync<PostNotificationsPermission>();

            if (status != Microsoft.Maui.ApplicationModel.PermissionStatus.Granted)
            {
                status = await Microsoft.Maui.ApplicationModel.Permissions.RequestAsync<PostNotificationsPermission>();
            }

            if (status != Microsoft.Maui.ApplicationModel.PermissionStatus.Granted)
            {
                // Non-fatal: the foreground service notification just won't show.
                // The service itself still runs.
                WeakReferenceMessenger.Default.Send(new PermissionDeniedMessage("Notifications"));
            }
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Android.Content.PM.Permission[] grantResults)
        {
            Microsoft.Maui.ApplicationModel.Platform.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        }
    }
}