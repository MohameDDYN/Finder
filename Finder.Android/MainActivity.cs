using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Preferences;
using Android.Runtime;
using Finder.Droid.Managers;
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

        // App-level Telegram command handler.
        // Active only while the app is in the foreground AND the service is not running.
        private TelegramCommandHandler _appCommandHandler;

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

        // ── App-level Telegram handler ────────────────────────────────────

        /// <summary>
        /// Starts the app-level Telegram command handler if the background
        /// service is not running. Ensures only one handler is active at a time.
        /// </summary>
        private void StartAppHandlerIfNeeded()
        {
            if (_appCommandHandler != null) return;

            bool serviceRunning = IsServiceRunning();
            if (serviceRunning) return;

            try
            {
                _appCommandHandler = new TelegramCommandHandler(this);
                _appCommandHandler.Start(sendStartupMessage: false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MainActivity] App handler start error: {ex.Message}");
            }
        }

        /// <summary>
        /// Stops the app-level Telegram command handler.
        /// Called when the service starts (it takes over) or when the app goes to background.
        /// </summary>
        private void StopAppHandler()
        {
            _appCommandHandler?.Stop();
            _appCommandHandler = null;
        }

        /// <summary>
        /// Reads the SharedPreferences key written by LocationService and BackgroundLocationService.
        /// Uses the same storage as all other components to stay in sync.
        /// </summary>
        private bool IsServiceRunning()
        {
            var prefs = PreferenceManager.GetDefaultSharedPreferences(this);
            return prefs.GetBoolean("is_tracking_service_running", false);
        }

        // ── MessagingCenter subscriptions ─────────────────────────────────

        /// <summary>
        /// Subscribes to service state change messages published by MainViewModel.
        /// ServiceStarted → stop app handler (service takes over polling).
        /// ServiceStopped → start app handler (service is gone).
        /// </summary>
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

        // ── Activity lifecycle ────────────────────────────────────────────

        protected override void OnResume()
        {
            base.OnResume();

            // Re-evaluate handler state every time the app comes to foreground.
            // The service may have started or stopped while the app was in background.
            if (IsServiceRunning())
                StopAppHandler();
            else
                StartAppHandlerIfNeeded();
        }

        protected override void OnPause()
        {
            base.OnPause();

            // Stop app-level polling when the app goes to background.
            // If the service is running it continues unaffected.
            // If the service is not running, nobody polls until the app returns.
            StopAppHandler();
        }

        protected override void OnDestroy()
        {
            StopAppHandler();
            UnsubscribeFromServiceStateMessages();
            base.OnDestroy();
        }

        // ── Permission chain ──────────────────────────────────────────────

        private void RequestLocationPermissions()
        {
            bool fineGranted = CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation)
                                 == Permission.Granted;
            bool coarseGranted = CheckSelfPermission(Android.Manifest.Permission.AccessCoarseLocation)
                                 == Permission.Granted;

            if (!fineGranted || !coarseGranted)
                RequestPermissions(new[]
                {
                    Android.Manifest.Permission.AccessFineLocation,
                    Android.Manifest.Permission.AccessCoarseLocation
                }, RC_LOCATION);
            else
                RequestBackgroundLocationPermission();
        }

        private void RequestBackgroundLocationPermission()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.Q)
            {
                RequestBiometricPermission();
                return;
            }

            bool granted = CheckSelfPermission(Android.Manifest.Permission.AccessBackgroundLocation)
                           == Permission.Granted;

            if (!granted)
                RequestPermissions(
                    new[] { Android.Manifest.Permission.AccessBackgroundLocation },
                    RC_BACKGROUND);
            else
                RequestBiometricPermission();
        }

        private void RequestBiometricPermission()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
            {
                const string useBiometric = "android.permission.USE_BIOMETRIC";
                if (CheckSelfPermission(useBiometric) != Permission.Granted)
                    RequestPermissions(new[] { useBiometric }, RC_BIOMETRIC);
            }
            else
            {
                if (CheckSelfPermission(Android.Manifest.Permission.UseFingerprint)
                    != Permission.Granted)
                    RequestPermissions(
                        new[] { Android.Manifest.Permission.UseFingerprint },
                        RC_BIOMETRIC);
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

            switch (requestCode)
            {
                case RC_LOCATION:
                    bool fineGranted = grantResults.Length > 0
                                       && grantResults[0] == Permission.Granted;
                    if (fineGranted)
                        RequestBackgroundLocationPermission();
                    else
                        ShowLocationRationaleDialog();
                    break;

                case RC_BACKGROUND:
                    RequestBiometricPermission();
                    break;

                case RC_BIOMETRIC:
                    break;
            }
        }

        private void ShowLocationRationaleDialog()
        {
            new AlertDialog.Builder(this)
                .SetTitle("Location Permission Required")
                .SetMessage(
                    "Finder needs Location permission to track and share your " +
                    "position via Telegram.\n\n" +
                    "Without this permission the tracking service cannot start.")
                .SetPositiveButton("Grant", (s, e) =>
                {
                    RequestPermissions(new[]
                    {
                        Android.Manifest.Permission.AccessFineLocation,
                        Android.Manifest.Permission.AccessCoarseLocation
                    }, RC_LOCATION);
                })
                .SetNegativeButton("Open Settings", (s, e) => OpenAppSettings())
                .SetCancelable(false)
                .Show();
        }

        private void OpenAppSettings()
        {
            try
            {
                var intent = new Intent(
                    Android.Provider.Settings.ActionApplicationDetailsSettings);
                intent.SetData(Android.Net.Uri.Parse($"package:{PackageName}"));
                StartActivity(intent);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[MainActivity] OpenAppSettings error: {ex.Message}");
            }
        }
    }
}