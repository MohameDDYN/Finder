using Android.App;
using Android.Content;
using Microsoft.Maui.Storage;

namespace Finder.Platforms.Android.Services
{
    /// <summary>
    /// Restarts BackgroundLocationService after a device reboot, but only if
    /// the user had it running before the reboot (PreferenceKeys.WasRunning).
    /// Does NOT auto-start on first install — only after a genuine reboot when
    /// a previously user-started service needs to come back.
    ///
    /// The explicit Name below matches AndroidManifest.xml's &lt;receiver&gt; entry;
    /// without it, .NET-for-Android generates a crc64-hashed Java class name that
    /// wouldn't match, causing the same ClassNotFoundException hit with
    /// BackgroundLocationService in Step 3.
    /// </summary>
    [BroadcastReceiver(Name = "Finder.Platforms.Android.Services.BootReceiver")]
    public class BootReceiver : BroadcastReceiver
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