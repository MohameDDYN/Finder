using Android.App;
using Android.App.Job;
using Android.Content;
using Android.OS;
using Android.Preferences;

namespace Finder.Droid.Services
{
    /// <summary>
    /// Periodic job that runs every 15 minutes and restarts the tracking service
    /// if it has died unexpectedly while SharedPreferences indicates it should be running.
    /// </summary>
    [Service(Name = "com.finder.app.WatchdogJobService",
             Permission = "android.permission.BIND_JOB_SERVICE",
             Exported = true)]
    public class WatchdogJobService : JobService
    {
        public const int JOB_ID = 2001;

        // 15 minutes is the minimum period Android enforces for periodic JobScheduler jobs.
        private const long PERIOD_MS = 15 * 60 * 1000L;

        public override bool OnStartJob(JobParameters @params)
        {
            try
            {
                var prefs = PreferenceManager.GetDefaultSharedPreferences(this);
                bool shouldBeRunning = prefs.GetBoolean("is_tracking_service_running", false);

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

            JobFinished(@params, false);
            return false;
        }

        public override bool OnStopJob(JobParameters @params)
        {
            // Returning true reschedules the job if it is interrupted before finishing.
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

                scheduler.Cancel(JOB_ID);

                var component = new ComponentName(
                    context, Java.Lang.Class.FromType(typeof(WatchdogJobService)));

                var builder = new JobInfo.Builder(JOB_ID, component)
                    .SetPeriodic(PERIOD_MS)
                    .SetPersisted(true)            // survives reboots
                    .SetRequiresBatteryNotLow(false)
                    .SetRequiredNetworkType(NetworkType.None);

                int result = scheduler.Schedule(builder.Build());
                System.Diagnostics.Debug.WriteLine(
                    $"[WatchdogJob] Scheduled: {(result == JobScheduler.ResultSuccess ? "OK" : "FAILED")}");
            }
            catch { /* Silent fail */ }
        }

        /// <summary>Cancels the watchdog job. Called when the user stops tracking.</summary>
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