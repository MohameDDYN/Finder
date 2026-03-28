using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;

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
        // Dangerous permission request codes — normal permissions are granted at install time.
        private const int RC_LOCATION = 100;  // ACCESS_FINE_LOCATION + ACCESS_COARSE_LOCATION
        private const int RC_BACKGROUND = 101;  // ACCESS_BACKGROUND_LOCATION (API 29+ only)
        private const int RC_BIOMETRIC = 102;  // USE_BIOMETRIC (API 28+) / USE_FINGERPRINT

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            Xamarin.Essentials.Platform.Init(this, savedInstanceState);
            global::Xamarin.Forms.Forms.Init(this, savedInstanceState);
            Plugin.Fingerprint.CrossFingerprint.SetCurrentActivityResolver(() => this);

            LoadApplication(new App());

            // Request permissions on first launch.
            // Each step chains into the next via OnRequestPermissionsResult,
            // respecting Android's one-dangerous-group-at-a-time rule.
            RequestLocationPermissions();
        }

        // Step 1: Fine + Coarse location
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

        // Step 2: Background location (API 29+ only).
        // Must be requested after ACCESS_FINE_LOCATION is already granted.
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

        // Step 3: Biometric. USE_BIOMETRIC (API 28+) replaces USE_FINGERPRINT.
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
                    // Permission chain complete. Biometric is optional.
                    break;
            }
        }

        // Shown when the user denies location permission.
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

        // Opens the app's system Settings page so the user can grant permissions manually.
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

        protected override void OnDestroy()
        {
            base.OnDestroy();
        }
    }
}