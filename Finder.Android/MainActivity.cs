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
        // ── Runtime permission request codes ──────────────────────────────────
        //
        //  Only DANGEROUS permissions need runtime requests.
        //
        //  RC_LOCATION   → ACCESS_FINE_LOCATION + ACCESS_COARSE_LOCATION
        //  RC_BACKGROUND → ACCESS_BACKGROUND_LOCATION (API 29+ only,
        //                  must follow RC_LOCATION being granted)
        //  RC_BIOMETRIC  → USE_BIOMETRIC (API 28+) / USE_FINGERPRINT (API < 28)
        //
        //  Normal permissions (INTERNET, ACCESS_NETWORK_STATE, FOREGROUND_SERVICE,
        //  INSTANT_APP_FOREGROUND_SERVICE, WAKE_LOCK, RECEIVE_BOOT_COMPLETED)
        //  are granted automatically at install — no runtime request needed.
        //
        //  NOTE: TelegramCommandHandler has been removed from MainActivity entirely.
        //  The BackgroundLocationService manages its own handler instance.
        //  Having a second handler here was causing a duplicate "Finder service
        //  started" Telegram message on every app launch and created two
        //  competing polling loops reading the same bot updates.
        // ──────────────────────────────────────────────────────────────────────
        private const int RC_LOCATION = 100;
        private const int RC_BACKGROUND = 101;
        private const int RC_BIOMETRIC = 102;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Initialize Xamarin.Essentials
            Xamarin.Essentials.Platform.Init(this, savedInstanceState);

            // Initialize Xamarin.Forms
            global::Xamarin.Forms.Forms.Init(this, savedInstanceState);

            // Set current activity resolver for Plugin.Fingerprint
            Plugin.Fingerprint.CrossFingerprint.SetCurrentActivityResolver(() => this);

            // Load the shared App
            LoadApplication(new App());

            // ── Kick off the permission chain ─────────────────────────────────
            // Order: Location → Background Location → Biometric
            // Each step is triggered inside OnRequestPermissionsResult so
            // Android's "one dangerous group at a time" rule is respected.
            RequestLocationPermissions();
        }

        // ── Step 1: Fine + Coarse location ───────────────────────────────────
        private void RequestLocationPermissions()
        {
            bool fineGranted = CheckSelfPermission(Android.Manifest.Permission.AccessFineLocation)
                                 == Permission.Granted;
            bool coarseGranted = CheckSelfPermission(Android.Manifest.Permission.AccessCoarseLocation)
                                 == Permission.Granted;

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
                // Already granted — advance to next step
                RequestBackgroundLocationPermission();
            }
        }

        // ── Step 2: Background location (API 29+ only) ───────────────────────
        // Android requires ACCESS_FINE_LOCATION to be granted BEFORE this
        // request is made — the OS silently ignores it otherwise.
        private void RequestBackgroundLocationPermission()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.Q)
            {
                // API < 29: background location is implicitly included with
                // ACCESS_FINE_LOCATION — skip directly to biometric step.
                RequestBiometricPermission();
                return;
            }

            bool backgroundGranted =
                CheckSelfPermission(Android.Manifest.Permission.AccessBackgroundLocation)
                == Permission.Granted;

            if (!backgroundGranted)
            {
                // On Android 11+ (API 30+) the system redirects the user to
                // Settings to enable "Allow all the time".
                RequestPermissions(
                    new[] { Android.Manifest.Permission.AccessBackgroundLocation },
                    RC_BACKGROUND);
            }
            else
            {
                RequestBiometricPermission();
            }
        }

        // ── Step 3: Biometric / Fingerprint ──────────────────────────────────
        // USE_BIOMETRIC replaces USE_FINGERPRINT on API 28+.
        // Both are declared in the manifest for maximum compatibility.
        private void RequestBiometricPermission()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.P) // API 28+
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

        // ── Handle results and chain to the next step ─────────────────────────
        public override void OnRequestPermissionsResult(
            int requestCode,
            string[] permissions,
            [GeneratedEnum] Permission[] grantResults)
        {
            // Always forward to Xamarin.Essentials first
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
                    // Whether granted or denied, continue.
                    // App still works without "Allow all the time"
                    // (foreground-only tracking).
                    RequestBiometricPermission();
                    break;

                case RC_BIOMETRIC:
                    // All permission steps complete.
                    // Biometric is optional — the fingerprint button will
                    // simply be hidden if not granted.
                    break;
            }
        }

        // ── Rationale dialog: shown when location is denied ───────────────────
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

        // ── Opens the app's system Settings page ──────────────────────────────
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