using Android.App;
using Android.Content;
using Microsoft.Maui.Storage;

namespace Finder.Platforms.Android.Services
{
    /// <summary>
    /// Restarts BackgroundLocationService after the app updates itself (either
    /// via Step 8's /update flow, or a normal manual/Play-Store update), so the
    /// user doesn't have to manually reopen the app and tap Start again.
    ///
    /// Checks the same "was running" flag as BootReceiver — if the user had
    /// explicitly stopped tracking before the update, an update completing
    /// shouldn't silently turn it back on.
    /// </summary>
    [BroadcastReceiver(Name = "Finder.Platforms.Android.Services.PackageReplacedReceiver")]
    public class PackageReplacedReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (context is null) return;

            var wasRunning = Preferences.Default.Get(PreferenceKeys.WasRunning, false);
            if (!wasRunning) return;

            var serviceIntent = new Intent(context, typeof(BackgroundLocationService));

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                context.StartForegroundService(serviceIntent);
            else
                context.StartService(serviceIntent);
        }
    }
}