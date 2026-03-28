using Android.App;
using Android.Content;
using Android.OS;
using Android.Preferences;
using Finder.Droid.Services;

namespace Finder.Droid.Receivers
{
    /// <summary>
    /// Receives the AlarmManager broadcast scheduled by OnTaskRemoved().
    /// Fires 3 seconds after the user swipes the app away from Recent Tasks
    /// and restarts the tracking service if it should still be running.
    /// </summary>
    [BroadcastReceiver(Enabled = true, Exported = false)]
    public class RestartReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
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