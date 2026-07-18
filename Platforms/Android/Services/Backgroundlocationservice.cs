using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Finder.Platforms.Android.Managers;
using Java.Lang;
using AndroidApp = Android.App.Application;

namespace Finder.Platforms.Android.Services
{
    /// <summary>
    /// The always-alive foreground service. StartForeground fires immediately
    /// with a persistent notification (required within seconds of service start
    /// on modern Android or the OS kills it). Returns Sticky so Android recreates
    /// it if the process is killed, and layers on AlarmManager + immediate-restart
    /// fallbacks in OnTaskRemoved/OnDestroy for the cases Sticky alone doesn't
    /// reliably cover (recents swipe, aggressive OEM battery managers).
    ///
    /// The explicit Name below is required: without it, .NET-for-Android
    /// generates a crc64-hash-prefixed Java class name by default, which won't
    /// match the android:name declared in AndroidManifest.xml and causes a
    /// ClassNotFoundException at runtime ("Unable to start service ... not found").
    /// </summary>
    [Service(Name = "Finder.Platforms.Android.Services.BackgroundLocationService")]
    public class BackgroundLocationService : Service
    {
        public const string ChannelId = "finder_service";
        private const int NotificationId = 1001;
        private const string ExtraExplicitUserStart = "explicit_user_start";

        public static bool IsRunning { get; private set; }
        public static bool IsStoppingByUserRequest { get; private set; }

        private TelegramCommandHandler? _commandHandler;

        public override IBinder? OnBind(Intent? intent) => null;

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            IsRunning = true;
            IsStoppingByUserRequest = false;

            CreateNotificationChannel();
            StartForeground(NotificationId, BuildNotification());

            Microsoft.Maui.Storage.Preferences.Default.Set(PreferenceKeys.WasRunning, true);

            var explicitUserStart = intent?.GetBooleanExtra(ExtraExplicitUserStart, false) ?? false;

            if (_commandHandler is null)
            {
                _commandHandler = new TelegramCommandHandler(ApplicationContext!);
                _ = _commandHandler.StartAsync(explicitUserStart);
            }

            return StartCommandResult.Sticky;
        }

        public override void OnTaskRemoved(Intent? rootIntent)
        {
            base.OnTaskRemoved(rootIntent);

            // App swiped from recents. If the user didn't explicitly stop us,
            // this was not a request to stop tracking — schedule a restart.
            if (!IsStoppingByUserRequest)
            {
                ScheduleRestart();
            }
        }

        public override void OnDestroy()
        {
            IsRunning = false;

            if (!IsStoppingByUserRequest)
            {
                // Killed by the OS (low memory, OEM battery optimizer, etc.),
                // not by the user. Try to come back immediately, and schedule
                // an alarm as a backup in case the immediate attempt is blocked.
                TryImmediateRestart();
                ScheduleRestart();
            }
            else
            {
                Microsoft.Maui.Storage.Preferences.Default.Set(PreferenceKeys.WasRunning, false);
            }

            _commandHandler?.Stop();
            base.OnDestroy();
        }

        /// <summary>
        /// Called by LocationService.StopTracking() BEFORE StopService() runs,
        /// so OnDestroy can tell "user stopped this" apart from "OS killed this".
        /// </summary>
        public static void RequestStop()
        {
            IsStoppingByUserRequest = true;
        }

        private void ScheduleRestart()
        {
            var context = AndroidApp.Context;
            var restartIntent = new Intent(context, typeof(BackgroundLocationService));

            var flags = OperatingSystem.IsAndroidVersionAtLeast(23)
                ? PendingIntentFlags.Immutable
                : PendingIntentFlags.UpdateCurrent;

            var pendingIntent = OperatingSystem.IsAndroidVersionAtLeast(26)
                ? PendingIntent.GetForegroundService(context, 0, restartIntent, flags)
                : PendingIntent.GetService(context, 0, restartIntent, flags);

            if (pendingIntent is null) return;

            var alarmManager = context.GetSystemService(Context.AlarmService) as AlarmManager;
            alarmManager?.Set(AlarmType.RtcWakeup, JavaSystem.CurrentTimeMillis() + 1000, pendingIntent);
        }

        private void TryImmediateRestart()
        {
            try
            {
                var context = AndroidApp.Context;
                var intent = new Intent(context, typeof(BackgroundLocationService));

                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                    context.StartForegroundService(intent);
                else
                    context.StartService(intent);
            }
            catch
            {
                // Best-effort only — the AlarmManager fallback scheduled above
                // will retry shortly regardless of whether this throws.
            }
        }

        private void CreateNotificationChannel()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

            var channel = new NotificationChannel(ChannelId, "Finder location service", NotificationImportance.Low)
            {
                Description = "Keeps the on-demand GPS locator responsive to Telegram commands."
            };

            var manager = GetSystemService(NotificationService) as NotificationManager;
            manager?.CreateNotificationChannel(channel);
        }

        private Notification BuildNotification()
        {
            return new NotificationCompat.Builder(this, ChannelId)
                .SetContentTitle("Finder")
                .SetContentText("Listening for location commands")
                .SetSmallIcon(Resource.Mipmap.appicon)
                .SetOngoing(true)
                .SetPriority((int)NotificationPriority.Low)
                .Build();
        }
    }
}