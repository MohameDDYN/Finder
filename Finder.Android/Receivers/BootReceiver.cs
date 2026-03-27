using Android.App;
using Android.Content;
using Android.OS;
using Android.Preferences;
using Finder.Droid.Services;

namespace Finder.Droid.Receivers
{
    /// <summary>
    /// Listens for the BOOT_COMPLETED broadcast and restarts
    /// BackgroundLocationService if it was running before the reboot.
    /// </summary>
    [BroadcastReceiver(Enabled = true, Exported = true)]
    [IntentFilter(new[] { Intent.ActionBootCompleted })]
    public class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            if (intent?.Action != Intent.ActionBootCompleted) return;

            // Check if tracking was active before the reboot
            var prefs = PreferenceManager.GetDefaultSharedPreferences(context);
            bool wasRunning = prefs.GetBoolean("is_tracking_service_running", false);

            if (!wasRunning) return;

            var serviceIntent = new Intent(context, typeof(BackgroundLocationService));

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(serviceIntent);
            else
                context.StartService(serviceIntent);
        }
    }
}