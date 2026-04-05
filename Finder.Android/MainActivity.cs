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

        // Ensures the install-permission dialog is shown only once on first launch.
        private const string PREF_INSTALL_PERM_PROMPTED = "install_perm_prompted";

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

            // ── Step 1: Location permissions (always required) ────────────────
            RequestLocationPermissions();

            // ── Step 2: Install-unknown-apps permission (for remote updates) ──
            // Shows a one-time rationale dialog on first launch. After the user
            // grants or skips it, the /update command handles future checks.
            RequestInstallPackagesPermissionIfNeeded();

            // ── Step 3: Post-update service recovery (safety net) ─────────────
            // PackageReplacedReceiver handles this as the primary mechanism.
            // This call is a secondary guard in case the receiver fired before
            // the new process was fully ready, or was delayed by the OS.
            RestartServiceIfNeededAfterUpdate();
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
            // Fires every time the user returns to the app — including after
            // coming back from the "Install Unknown Apps" settings screen.
            // ResumePendingUpdateAsync is a no-op when there is no pending update.
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await TelegramCommandHandler.ResumePendingUpdateAsync(this);
                }
                catch { /* Silent fail — retryable via /update */ }
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
        // Post-update service recovery — safety net
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Secondary guard against a dead-service / stale-UI mismatch after an
        /// APK self-update.
        ///
        /// Primary mechanism: PackageReplacedReceiver fires MY_PACKAGE_REPLACED
        /// and restarts the service before MainActivity even opens.
        ///
        /// This method covers edge cases where the receiver was delayed or the
        /// OS did not deliver the broadcast before OnCreate ran:
        ///   • SharedPreferences says the service should be running  (true)
        ///   • BackgroundLocationService.IsRunning is false (process was killed)
        ///   → Restart the service so the live state matches the stored state.
        ///
        /// If the receiver already started the service this is effectively a
        /// no-op because StartForegroundService on an already-running service
        /// just delivers a new Intent to OnStartCommand, which is harmless.
        /// </summary>
        private void RestartServiceIfNeededAfterUpdate()
        {
            try
            {
                var prefs = PreferenceManager.GetDefaultSharedPreferences(this);
                bool shouldBeRunning = prefs.GetBoolean(
                    "is_tracking_service_running", false);

                // Service is exactly where it should be — nothing to do.
                if (!shouldBeRunning) return;
                if (BackgroundLocationService.IsRunning) return;

                System.Diagnostics.Debug.WriteLine(
                    "[MainActivity] Service should be running but is dead — " +
                    "restarting after update.");

                // Re-schedule watchdog so it survives the fresh install
                try { WatchdogJobService.Schedule(this); } catch { }

                var intent = new Intent(this, typeof(BackgroundLocationService));
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    StartForegroundService(intent);
                else
                    StartService(intent);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MainActivity] RestartServiceIfNeededAfterUpdate error: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Install-unknown-apps permission — first-launch prompt
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Shows a one-time rationale dialog on first launch asking the user to
        /// grant the "Install Unknown Apps" permission for Finder.
        ///
        /// This permission is required on API 26+ (Android 8.0+) for the remote
        /// APK update feature triggered by the Telegram /update command.
        ///
        /// On API 21-25 (Android 5.0-7.1) the permission is global and covered
        /// by the REQUEST_INSTALL_PACKAGES manifest entry — no dialog needed.
        ///
        /// The dialog is shown exactly once. If the user skips it, the /update
        /// Telegram command will re-prompt when they actually request an update.
        /// </summary>
        private void RequestInstallPackagesPermissionIfNeeded()
        {
            // API 21-25: no per-app toggle — manifest permission is enough.
            if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;

            // Already granted — nothing to do.
            if (ApkInstaller.CanInstallPackages(this)) return;

            // Only show the dialog once.
            var prefs = PreferenceManager.GetDefaultSharedPreferences(this);
            bool alreadyShown = prefs.GetBoolean(PREF_INSTALL_PERM_PROMPTED, false);
            if (alreadyShown) return;

            // Mark as shown before displaying so a force-close during the dialog
            // doesn't cause it to reappear on the next launch.
            var ed = prefs.Edit();
            ed.PutBoolean(PREF_INSTALL_PERM_PROMPTED, true);
            ed.Apply();

            new AlertDialog.Builder(this)
                .SetTitle("Allow Installing Updates")
                .SetIcon(Android.Resource.Drawable.IcDialogInfo)
                .SetMessage(
                    "Finder can receive automatic APK updates remotely via Telegram " +
                    "using the /update command.\n\n" +
                    "To enable this, Finder needs permission to install apps from " +
                    "unknown sources.\n\n" +
                    "Tap \"Allow\" to open settings and toggle it on for Finder, " +
                    "or tap \"Later\" to skip — you can grant it when you send " +
                    "your first /update command.")
                .SetPositiveButton("Allow", (sender, args) =>
                {
                    ApkInstaller.OpenInstallPermissionSettings(this);
                })
                .SetNegativeButton("Later", (sender, args) =>
                {
                    // User skips — /update will handle it when needed.
                })
                .SetCancelable(false)
                .Show();
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
        // Runtime permission requests — Location
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
            // Background location permission only exists on API 29+
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
            int requestCode,
            string[] permissions,
            [GeneratedEnum] Permission[] grantResults)
        {
            Xamarin.Essentials.Platform.OnRequestPermissionsResult(
                requestCode, permissions, grantResults);

            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

            if (requestCode == RC_LOCATION)
                RequestBackgroundLocationIfNeeded();
        }
    }
}