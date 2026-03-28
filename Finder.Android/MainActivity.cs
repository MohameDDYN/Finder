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

        protected override void OnResume()
        {
            base.OnResume();

            if (IsServiceRunning())
                StopAppHandler();
            else
                StartAppHandlerIfNeeded();
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
            catch { }
        }
    }
}