using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Android.Content;

namespace Finder.Droid.Services
{
    /// <summary>
    /// Downloads an APK from a direct URL (Google Drive, Dropbox, or any HTTPS server).
    ///
    /// Google Drive handling:
    ///   • Converts share/view links to direct download links automatically.
    ///   • Handles the large-file virus-scan confirmation page by appending confirm=t.
    ///
    /// Dropbox handling:
    ///   • Converts ?dl=0 links to ?dl=1 (direct download) automatically.
    ///
    /// The APK is saved to the app's private external storage:
    ///   /sdcard/Android/data/com.finder.app/files/update/Finder_update.apk
    ///
    /// This directory does NOT require WRITE_EXTERNAL_STORAGE on Android 10+ (API 29+).
    /// On API 21–28 the WRITE_EXTERNAL_STORAGE permission is included in the manifest.
    /// </summary>
    public class ApkDownloaderService
    {
        // ─────────────────────────────────────────────────────────────────────
        // Constants
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Sub-folder inside the app's external files directory.</summary>
        private const string UPDATE_FOLDER = "update";

        /// <summary>Local filename for the downloaded APK.</summary>
        private const string APK_FILE_NAME = "Finder_update.apk";

        /// <summary>Read buffer size in bytes — 8 KB is efficient for streaming.</summary>
        private const int BUFFER_SIZE = 8192;

        /// <summary>Maximum download time before the request is aborted.</summary>
        private static readonly TimeSpan DOWNLOAD_TIMEOUT = TimeSpan.FromMinutes(10);

        // ─────────────────────────────────────────────────────────────────────
        // Fields
        // ─────────────────────────────────────────────────────────────────────

        private readonly Context _context;

        // ─────────────────────────────────────────────────────────────────────
        // Constructor
        // ─────────────────────────────────────────────────────────────────────

        public ApkDownloaderService(Context context)
        {
            _context = context;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Public API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Downloads the APK from the given URL to local storage.
        ///
        /// Parameters:
        ///   url        — Direct download URL (Google Drive, Dropbox, or HTTPS server).
        ///   onProgress — Optional callback receiving a 0–100 integer progress value.
        ///
        /// Returns the absolute local file path on success, or null on failure.
        /// </summary>
        public async Task<string> DownloadApkAsync(string url, Action<int> onProgress = null)
        {
            try
            {
                // ── Step 1: Prepare the local destination path ────────────────
                string destPath = GetApkDestinationPath();
                if (destPath == null) return null;

                // Ensure the update sub-folder exists
                string updateDir = Path.GetDirectoryName(destPath);
                if (!Directory.Exists(updateDir))
                    Directory.CreateDirectory(updateDir);

                // Delete any previous partial download to avoid corruption
                if (File.Exists(destPath))
                    File.Delete(destPath);

                // ── Step 2: Resolve the actual download URL ───────────────────
                // Converts share links / handles Google Drive confirmation pages
                string resolvedUrl = ResolveDownloadUrl(url);

                // ── Step 3: Download with streaming + progress reporting ───────
                using var client = new HttpClient { Timeout = DOWNLOAD_TIMEOUT };

                // A browser User-Agent helps bypass some CDN restrictions
                client.DefaultRequestHeaders.Add("User-Agent",
                    "Mozilla/5.0 (Linux; Android 10) AppleWebKit/537.36");

                // Stream the response — do NOT buffer the entire response in memory
                var response = await client.GetAsync(
                    resolvedUrl,
                    HttpCompletionOption.ResponseHeadersRead);

                // ── Step 4: Detect Google Drive HTML confirmation page ─────────
                // Google Drive returns text/html for files >100 MB (virus scan page).
                // Re-request with confirm=t to bypass it.
                string contentType =
                    response.Content.Headers.ContentType?.MediaType ?? string.Empty;

                if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    response.Dispose();
                    string confirmUrl = AppendGoogleDriveConfirm(resolvedUrl);
                    response = await client.GetAsync(
                        confirmUrl,
                        HttpCompletionOption.ResponseHeadersRead);
                }

                response.EnsureSuccessStatusCode();

                // Total size is used for progress calculation (may be -1 if unknown)
                long totalBytes = response.Content.Headers.ContentLength ?? -1L;
                long downloadedBytes = 0L;
                int lastReported = -1;

                // ── Step 5: Write stream to file ──────────────────────────────
                using var networkStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(
                    destPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    BUFFER_SIZE, useAsync: true);

                var buffer = new byte[BUFFER_SIZE];
                int bytesRead;

                while ((bytesRead = await networkStream.ReadAsync(
                    buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    downloadedBytes += bytesRead;

                    // Report progress only when the percentage actually changes
                    if (totalBytes > 0 && onProgress != null)
                    {
                        int progress = (int)((downloadedBytes * 100L) / totalBytes);
                        if (progress != lastReported)
                        {
                            lastReported = progress;
                            onProgress(progress);
                        }
                    }
                }

                // Ensure 100% is always reported on completion
                onProgress?.Invoke(100);

                return destPath;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ApkDownloaderService] Download failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns the absolute path where the APK will be saved.
        /// Used by ApkInstaller.cs and for pre-flight checks.
        /// </summary>
        public string GetApkDestinationPath()
        {
            try
            {
                var externalDir = _context.GetExternalFilesDir(null);
                if (externalDir == null) return null;
                return Path.Combine(externalDir.AbsolutePath, UPDATE_FOLDER, APK_FILE_NAME);
            }
            catch { return null; }
        }

        // ─────────────────────────────────────────────────────────────────────
        // URL resolution helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Converts share/view URLs to direct download URLs and fixes
        /// Dropbox dl=0 links. Does not modify already-direct URLs.
        /// </summary>
        private static string ResolveDownloadUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;

            // ── Google Drive: /file/d/{ID}/view → direct download ─────────────
            // Input:  https://drive.google.com/file/d/1A2B3C/view?usp=sharing
            // Output: https://drive.google.com/uc?export=download&confirm=t&id=1A2B3C
            if (url.Contains("drive.google.com/file/d/", StringComparison.OrdinalIgnoreCase))
            {
                int idStart = url.IndexOf("/file/d/", StringComparison.Ordinal) + 8;
                int idEnd = url.IndexOf("/", idStart, StringComparison.Ordinal);
                if (idEnd < 0) idEnd = url.IndexOf("?", idStart, StringComparison.Ordinal);
                if (idEnd < 0) idEnd = url.Length;

                string fileId = url.Substring(idStart, idEnd - idStart);
                return $"https://drive.google.com/uc?export=download&confirm=t&id={fileId}";
            }

            // ── Google Drive: already uc?export=download — append confirm ─────
            if (url.Contains("drive.google.com/uc", StringComparison.OrdinalIgnoreCase))
                return AppendGoogleDriveConfirm(url);

            // ── Dropbox: ?dl=0 → ?dl=1 ───────────────────────────────────────
            // Input:  https://www.dropbox.com/s/xxx/Finder.apk?dl=0
            // Output: https://www.dropbox.com/s/xxx/Finder.apk?dl=1
            if (url.Contains("dropbox.com", StringComparison.OrdinalIgnoreCase))
                return url.Replace("?dl=0", "?dl=1", StringComparison.OrdinalIgnoreCase);

            // Return the URL unchanged for any other host (direct HTTPS server)
            return url;
        }

        /// <summary>
        /// Appends confirm=t to a Google Drive URL to bypass the virus-scan page.
        /// Safe to call even if confirm= is already present.
        /// </summary>
        private static string AppendGoogleDriveConfirm(string url)
        {
            if (url.Contains("confirm=", StringComparison.OrdinalIgnoreCase))
                return url;
            return url + (url.Contains("?") ? "&confirm=t" : "?confirm=t");
        }
    }
}