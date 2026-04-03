using System;
using Android.Content;
using Android.OS;
using AndroidX.Core.Content;

namespace Finder.Droid.Services
{
    /// <summary>
    /// Triggers the Android system package installer for a locally-stored APK file.
    ///
    /// API compatibility:
    ///   • API 24+ (Android 7.0+) — Uses FileProvider to create a safe content:// URI.
    ///     Direct file:// URIs are blocked by StrictMode on API 24+.
    ///   • API 21–23 (Android 5.0–6.0) — Uses file:// URI directly (allowed on these APIs).
    ///
    /// FileProvider authority: "com.finder.app.fileprovider"
    ///   → Must match android:authorities in AndroidManifest.xml
    ///   → Must match the path defined in Resources/xml/file_paths.xml
    ///
    /// Install permission:
    ///   • API 26+ — User must enable "Install unknown apps" for Finder.
    ///     Call CanInstallPackages() to check, and OpenInstallPermissionSettings()
    ///     to guide the user if it is not yet enabled.
    ///   • API 21–25 — "Install unknown apps" is a global setting (not per-app).
    ///     The REQUEST_INSTALL_PACKAGES permission in the manifest covers this.
    /// </summary>
    public static class ApkInstaller
    {
        // ─────────────────────────────────────────────────────────────────────
        // Constants
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// FileProvider authority — MUST match android:authorities in AndroidManifest.xml
        /// and the package name defined in file_paths.xml.
        /// </summary>
        private const string FILE_PROVIDER_AUTHORITY = "com.finder.app.fileprovider";

        // ─────────────────────────────────────────────────────────────────────
        // Install
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Launches the Android system package installer for the APK at apkFilePath.
        ///
        /// On API 26+ (Android 8.0+), the user must have previously enabled
        /// "Install unknown apps" for Finder. If not, the system will show a
        /// settings prompt automatically.
        ///
        /// On any API, the user must tap "Install" in the system dialog —
        /// silent/unattended installation is NOT possible without root or MDM.
        /// </summary>
        /// <param name="context">Any valid Android Context (Activity or Service).</param>
        /// <param name="apkFilePath">Absolute path to the locally-stored APK file.</param>
        public static void Install(Context context, string apkFilePath)
        {
            try
            {
                var apkFile = new Java.IO.File(apkFilePath);

                // Guard: file must exist before attempting install
                if (!apkFile.Exists())
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ApkInstaller] APK not found at: {apkFilePath}");
                    return;
                }

                Android.Net.Uri apkUri;

                if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                {
                    // ── API 24+ ─────────────────────────────────────────────
                    // file:// URIs are blocked — must use FileProvider to create
                    // a content:// URI that the installer app is granted access to.
                    apkUri = FileProvider.GetUriForFile(
                        context,
                        FILE_PROVIDER_AUTHORITY,
                        apkFile);
                }
                else
                {
                    // ── API 21–23 ────────────────────────────────────────────
                    // Direct file:// URI is allowed on these older API levels.
                    apkUri = Android.Net.Uri.FromFile(apkFile);
                }

                // Build the install intent
                var intent = new Intent(Intent.ActionView);
                intent.SetDataAndType(apkUri, "application/vnd.android.package-archive");

                // Required for FileProvider URIs — grants the installer app
                // temporary read permission for the content:// URI
                intent.AddFlags(ActivityFlags.GrantReadUriPermission);

                // Required when starting an Activity from a non-Activity context
                // (e.g., from a Service or BroadcastReceiver)
                intent.AddFlags(ActivityFlags.NewTask);

                context.StartActivity(intent);

                System.Diagnostics.Debug.WriteLine(
                    $"[ApkInstaller] Install intent launched for: {apkFilePath}");
            }
            catch (Exception ex)
            {
                // Log but never crash — the user can retry by sending /update again
                System.Diagnostics.Debug.WriteLine(
                    $"[ApkInstaller] Install failed: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Permission helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if the app currently has permission to install APKs.
        ///
        /// On API 26+ this is a per-app setting the user must grant explicitly.
        /// On API 21–25 the REQUEST_INSTALL_PACKAGES manifest permission is sufficient.
        /// </summary>
        public static bool CanInstallPackages(Context context)
        {
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                    return context.PackageManager.CanRequestPackageInstalls();

                // API 21–25: always allowed if manifest permission is declared
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Opens the system settings screen where the user can enable
        /// "Install unknown apps" for this specific app.
        ///
        /// Only available on API 26+. No-op on older versions.
        /// Call this when CanInstallPackages() returns false.
        /// </summary>
        public static void OpenInstallPermissionSettings(Context context)
        {
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    var intent = new Intent(
                        Android.Provider.Settings.ActionManageUnknownAppSources,
                        Android.Net.Uri.Parse($"package:{context.PackageName}"));
                    intent.AddFlags(ActivityFlags.NewTask);
                    context.StartActivity(intent);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ApkInstaller] OpenInstallPermissionSettings failed: {ex.Message}");
            }
        }
    }
}