using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Preferences;
using Android.Runtime;
using Finder.Droid.Managers;
using Finder.Droid.Services;
using Finder.ViewModels;
using Xamarin.Forms;

namespace Finder.Droid
{
    [Activity(
        Label = "Finder",
        Icon = "@mipmap/icon",
        Theme = "@style/MainTheme",
        MainLauncher = true,
        ConfigurationChanges =
            ConfigChanges.ScreenSize |
            ConfigChanges.Orientation |
            ConfigChanges.UiMode |
            ConfigChanges.ScreenLayout |
            ConfigChanges.SmallestScreenSize)]
    public class MainActivity : global::Xamarin.Forms.Platform.Android.FormsAppCompatActivity
    {
        private const int RC_LOCATION = 100;
        private const int RC_BACKGROUND = 101;
        private const int RC_BIOMETRIC = 102;

        // ── Tracks whether this resume cycle should check for a pending update ──
        // Set to true every time OnResume fires so we only attempt once per return.
        private bool _checkPendingUpdateOnResume = false;

        // ─────────────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────────────

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            Xamarin.Essentials.Platform.Init(this, savedInstanceState);
            global::Xamarin.Forms.Forms.Init(this, savedInstanceState);
            Plugin.Fingerprint.CrossFingerprint.SetCurrentActivityResolver(() => this);

            LoadApplication(new App());

            SubscribeToServiceStateMessages();
            RequestLocationPermissions();
        }

        protected override void OnResume()
        {
            base.OnResume();

            // ── Telegram command handler ──────────────────────────────────────
            if (IsServiceRunning())
                StopAppHandler();
            else
                StartAppHandlerIfNeeded();

            // ── Auto-resume pending update ────────────────────────────────────
            // Fires every time the user returns to the app from ANY screen
            // (including the "Install Unknown Apps" settings screen).
            // TelegramCommandHandler.ResumePendingUpdateAsync is a no-op when
            // there is no stored pending update, so this is always safe to call.
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await TelegramCommandHandler.ResumePendingUpdateAsync(this);
                }
                catch { /* Silent fail — update can be retried manually */ }
            });
        }

        protected override void OnPause()
        {
            base.OnPause();
            StopAppHandler();
        }

        protected override void OnDestroy()
        {
            StopAppHandler();
            UnsubscribeFromServiceStateMessages();
            base.OnDestroy();
        }

        // ─────────────────────────────────────────────────────────────────────
        // App-side command handler management
        // ─────────────────────────────────────────────────────────────────────

        private void StartAppHandlerIfNeeded()
        {
            if (IsServiceRunning()) return;
            AppCommandHandler.Start(this, sendStartupMessage: false);
        }

        private void StopAppHandler()
        {
            AppCommandHandler.Stop();
        }

        private bool IsServiceRunning()
        {
            var prefs = PreferenceManager.GetDefaultSharedPreferences(this);
            return prefs.GetBoolean("is_tracking_service_running", false);
        }

        // ─────────────────────────────────────────────────────────────────────
        // MessagingCenter subscriptions — keep handler and service in sync
        // ─────────────────────────────────────────────────────────────────────

        private void SubscribeToServiceStateMessages()
        {
            MessagingCenter.Subscribe<MainViewModel>(this, "ServiceStarted", sender =>
            {
                StopAppHandler();
            });

            MessagingCenter.Subscribe<MainViewModel>(this, "ServiceStopped", sender =>
            {
                StartAppHandlerIfNeeded();
            });
        }

        private void UnsubscribeFromServiceStateMessages()
        {
            MessagingCenter.Unsubscribe<MainViewModel>(this, "ServiceStarted");
            MessagingCenter.Unsubscribe<MainViewModel>(this, "ServiceStopped");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Runtime permission requests
        // ─────────────────────────────────────────────────────────────────────

        private void RequestLocationPermissions()
        {
            bool fineGranted = CheckSelfPermission(
                Android.Manifest.Permission.AccessFineLocation) == Permission.Granted;
            bool coarseGranted = CheckSelfPermission(
                Android.Manifest.Permission.AccessCoarseLocation) == Permission.Granted;

            if (!fineGranted || !coarseGranted)
            {
                RequestPermissions(new[]
                {
                    Android.Manifest.Permission.AccessFineLocation,
                    Android.Manifest.Permission.AccessCoarseLocation
                }, RC_LOCATION);
            }
            else
            {
                RequestBackgroundLocationIfNeeded();
            }
        }

        private void RequestBackgroundLocationIfNeeded()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.Q) return;

            bool bgGranted = CheckSelfPermission(
                Android.Manifest.Permission.AccessBackgroundLocation) == Permission.Granted;

            if (!bgGranted)
            {
                RequestPermissions(new[]
                {
                    Android.Manifest.Permission.AccessBackgroundLocation
                }, RC_BACKGROUND);
            }
        }

        public override void OnRequestPermissionsResult(
            int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
        {
            Xamarin.Essentials.Platform.OnRequestPermissionsResult(
                requestCode, permissions, grantResults);

            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

            if (requestCode == RC_LOCATION)
                RequestBackgroundLocationIfNeeded();
        }
    }
}