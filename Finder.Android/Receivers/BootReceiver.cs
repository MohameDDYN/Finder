using Android.App;
using Android.Content;
using Android.OS;
using Android.Preferences;
using Finder.Droid.Services;

namespace Finder.Droid.Receivers
{
    /// <summary>
    /// Layer 5 — Fires on BOOT_COMPLETED.
    ///
    /// If the tracking service was running before the phone was restarted,
    /// this receiver restarts it automatically and re-schedules the watchdog job.
    /// </summary>
    [BroadcastReceiver(Enabled = true, Exported = true)]
    [IntentFilter(new[] { Intent.ActionBootCompleted })]
    public class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            if (intent?.Action != Intent.ActionBootCompleted) return;

            // Read the same key that BackgroundLocationService and LocationService write to.
            // Must use PreferenceManager.GetDefaultSharedPreferences — identical to the other files.
            var prefs = PreferenceManager.GetDefaultSharedPreferences(context);
            bool wasRunning = prefs.GetBoolean("is_tracking_service_running", false);

            if (!wasRunning) return;

            System.Diagnostics.Debug.WriteLine(
                "[BootReceiver] Boot completed — restarting tracking service.");

            // Restart the foreground tracking service.
            var serviceIntent = new Intent(context, typeof(BackgroundLocationService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(serviceIntent);
            else
                context.StartService(serviceIntent);

            // Re-schedule the periodic watchdog job.
            // (JobScheduler jobs with SetPersisted(true) survive reboots on their own,
            // but calling Schedule() here is a safe guard in case the job was cancelled.)
            WatchdogJobService.Schedule(context);
        }
    }
}