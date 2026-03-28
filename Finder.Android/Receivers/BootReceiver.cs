using Android.App;
using Android.Content;
using Android.OS;
using Android.Preferences;
using Finder.Droid.Services;

namespace Finder.Droid.Receivers
{
    /// <summary>
    /// Fires on BOOT_COMPLETED. Restarts the tracking service if it was
    /// running before the device was rebooted, and re-schedules the watchdog job.
    /// </summary>
    [BroadcastReceiver(Enabled = true, Exported = true)]
    [IntentFilter(new[] { Intent.ActionBootCompleted })]
    public class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            if (intent?.Action != Intent.ActionBootCompleted) return;

            var prefs = PreferenceManager.GetDefaultSharedPreferences(context);
            bool wasRunning = prefs.GetBoolean("is_tracking_service_running", false);

            if (!wasRunning) return;

            System.Diagnostics.Debug.WriteLine(
                "[BootReceiver] Boot completed — restarting tracking service.");

            var serviceIntent = new Intent(context, typeof(BackgroundLocationService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(serviceIntent);
            else
                context.StartService(serviceIntent);

            WatchdogJobService.Schedule(context);
        }
    }
}