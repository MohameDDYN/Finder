using Android.App;
using Android.Content;
using Android.OS;
using Android.Preferences;
using Finder.Droid.Services;

namespace Finder.Droid.Receivers
{
    /// <summary>
    /// Fires immediately after Finder itself is updated (replaced) by a new APK.
    ///
    /// Android sends ACTION_MY_PACKAGE_REPLACED only to the package that was
    /// just replaced — no other app can trigger this receiver.
    ///
    /// Problem it solves:
    ///   When Android installs a new APK it kills the running process, which
    ///   resets BackgroundLocationService.IsRunning (static field) to false.
    ///   SharedPreferences still holds is_tracking_service_running = true, so
    ///   the UI incorrectly shows the service as running while it is dead.
    ///
    /// Solution:
    ///   If SharedPreferences says the service should be running, restart it
    ///   here — before MainActivity even opens — so tracking resumes
    ///   seamlessly and the UI state matches reality.
    ///
    /// No extra permissions required. MY_PACKAGE_REPLACED is delivered by the
    /// system directly to the replaced package and does not need RECEIVE_BOOT_COMPLETED.
    /// </summary>
    [BroadcastReceiver(Enabled = true, Exported = true)]
    [IntentFilter(new[] { Intent.ActionMyPackageReplaced })]
    public class PackageReplacedReceiver : BroadcastReceiver
    {
        private const string PREF_KEY_RUNNING = "is_tracking_service_running";

        public override void OnReceive(Context context, Intent intent)
        {
            if (intent?.Action != Intent.ActionMyPackageReplaced) return;

            var prefs = PreferenceManager.GetDefaultSharedPreferences(context);
            bool shouldBeRunning = prefs.GetBoolean(PREF_KEY_RUNNING, false);

            System.Diagnostics.Debug.WriteLine(
                $"[PackageReplacedReceiver] App updated. " +
                $"Service should be running: {shouldBeRunning}");

            if (!shouldBeRunning) return;

            // Re-schedule the watchdog job first so it survives the new install
            try { WatchdogJobService.Schedule(context); } catch { }

            // Restart the tracking service
            try
            {
                var serviceIntent = new Intent(context, typeof(BackgroundLocationService));

                // Do NOT set "explicit_user_start" — this is an automatic restart,
                // not a user action. BackgroundLocationService uses this to decide
                // whether to send the Telegram startup message.
                // The startup message will still be sent because the service reads
                // the suppress flag, which is false at this point.

                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    context.StartForegroundService(serviceIntent);
                else
                    context.StartService(serviceIntent);

                System.Diagnostics.Debug.WriteLine(
                    "[PackageReplacedReceiver] Service restart issued.");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PackageReplacedReceiver] Failed to restart service: {ex.Message}");
            }
        }
    }
}