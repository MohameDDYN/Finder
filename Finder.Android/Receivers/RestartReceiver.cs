using Android.App;
using Android.Content;
using Android.OS;
using Android.Preferences;
using Finder.Droid.Services;

namespace Finder.Droid.Receivers
{
    /// <summary>
    /// Layer 3 — Receives the AlarmManager broadcast scheduled by
    /// BackgroundLocationService.OnTaskRemoved().
    ///
    /// Fires 3 seconds after the user swipes the app away from Recent Tasks,
    /// giving the OS time to clean up before the service is restarted.
    /// </summary>
    [BroadcastReceiver(Enabled = true, Exported = false)]
    public class RestartReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            // Only restart if the preference says tracking should be active.
            var prefs = PreferenceManager.GetDefaultSharedPreferences(context);
            bool shouldBeRunning = prefs.GetBoolean("is_tracking_service_running", false);

            if (!shouldBeRunning) return;

            System.Diagnostics.Debug.WriteLine(
                "[RestartReceiver] Restarting service after task removal.");

            var serviceIntent = new Intent(context, typeof(BackgroundLocationService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(serviceIntent);
            else
                context.StartService(serviceIntent);
        }
    }
}