using Android.App;
using Android.Content;
using Android.OS;
using Android.Preferences;
using Finder.Droid.Services;

namespace Finder.Droid.Receivers
{
    [BroadcastReceiver(Enabled = true, Exported = true)]
    [IntentFilter(new[] { Intent.ActionBootCompleted })]
    public class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            if (intent?.Action != Intent.ActionBootCompleted) return;

            // Read the same key that BackgroundLocationService and LocationService write to.
            // Must use PreferenceManager.GetDefaultSharedPreferences — identical to the other two files.
            var preferences = PreferenceManager.GetDefaultSharedPreferences(context);
            bool wasRunning = preferences.GetBoolean("is_tracking_service_running", false);

            if (!wasRunning) return;

            // typeof(BackgroundLocationService) resolves correctly because of
            // the "using Finder.Droid.Services" directive above.
            var serviceIntent = new Intent(context, typeof(BackgroundLocationService));

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(serviceIntent);
            else
                context.StartService(serviceIntent);
        }
    }
}