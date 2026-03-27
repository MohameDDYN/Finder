using Android.App;
using Android.App.Job;
using Android.Content;
using Android.OS;
using Android.Preferences;

namespace Finder.Droid.Services
{
    /// <summary>
    /// Layer 4 — Periodic watchdog that runs every 15 minutes.
    ///
    /// Checks whether the tracking service should be running (per SharedPreferences)
    /// but has died unexpectedly (IsRunning == false), and restarts it if so.
    ///
    /// Covers scenarios that OnDestroy auto-restart misses, such as:
    ///   • The OS killed the process without calling OnDestroy
    ///   • The service was restarted by the OS but failed silently
    ///
    /// NOTE: JobScheduler jobs are cancelled when the user force-stops the app
    /// from Android Settings. This is enforced by Android and cannot be bypassed.
    /// After a force-stop, only a manual app launch or phone reboot (BootReceiver)
    /// will restore tracking.
    /// </summary>
    [Service(Name = "com.finder.app.WatchdogJobService",
             Permission = "android.permission.BIND_JOB_SERVICE",
             Exported = true)]
    public class WatchdogJobService : JobService
    {
        public const int JOB_ID = 2001;

        // Watchdog fires every 15 minutes.
        // 15 min is the minimum interval Android enforces for periodic JobScheduler jobs.
        private const long PERIOD_MS = 15 * 60 * 1000L;

        public override bool OnStartJob(JobParameters @params)
        {
            try
            {
                var prefs = PreferenceManager.GetDefaultSharedPreferences(this);
                bool shouldBeRunning = prefs.GetBoolean("is_tracking_service_running", false);

                // If the preference says "running" but no live service instance exists
                // in this process, restart the service.
                if (shouldBeRunning && !BackgroundLocationService.IsRunning)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[WatchdogJob] Service dead but should be running — restarting.");

                    var intent = new Intent(this, typeof(BackgroundLocationService));
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                        StartForegroundService(intent);
                    else
                        StartService(intent);
                }
            }
            catch { /* Silent fail */ }

            // Work is synchronous — signal completion immediately.
            JobFinished(@params, false);
            return false;
        }

        public override bool OnStopJob(JobParameters @params)
        {
            // Return true to reschedule if the job is interrupted before finishing.
            return true;
        }

        /// <summary>
        /// Schedules (or re-schedules) the periodic watchdog job.
        /// Safe to call multiple times — cancels any existing job with the same ID first.
        /// </summary>
        public static void Schedule(Context context)
        {
            try
            {
                var scheduler = (JobScheduler)context.GetSystemService(
                    Context.JobSchedulerService);

                // Cancel the existing job before re-scheduling to avoid duplicates.
                scheduler.Cancel(JOB_ID);

                var component = new ComponentName(
                    context, Java.Lang.Class.FromType(typeof(WatchdogJobService)));

                var builder = new JobInfo.Builder(JOB_ID, component)
                    .SetPeriodic(PERIOD_MS)
                    // SetPersisted(true) survives reboots.
                    // Requires RECEIVE_BOOT_COMPLETED permission (already declared).
                    .SetPersisted(true)
                    // Run even on low battery — tracking must stay alive.
                    .SetRequiresBatteryNotLow(false);

                // Require network only on API 21 (our min) — not needed for the
                // watchdog itself, so set to none.
                builder.SetRequiredNetworkType(NetworkType.None);

                int result = scheduler.Schedule(builder.Build());
                System.Diagnostics.Debug.WriteLine(
                    $"[WatchdogJob] Scheduled: {(result == JobScheduler.ResultSuccess ? "OK" : "FAILED")}");
            }
            catch { /* Silent fail */ }
        }

        /// <summary>Cancels the watchdog job (called when user stops tracking).</summary>
        public static void Cancel(Context context)
        {
            try
            {
                var scheduler = (JobScheduler)context.GetSystemService(
                    Context.JobSchedulerService);
                scheduler.Cancel(JOB_ID);
            }
            catch { /* Silent fail */ }
        }
    }
}