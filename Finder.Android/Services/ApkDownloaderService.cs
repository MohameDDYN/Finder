using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Android.Content;

namespace Finder.Droid.Services
{
    // ─────────────────────────────────────────────────────────────────────────
    // Result object — replaces the illegal `out` parameter on the async method
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returned by ApkDownloaderService.DownloadApkAsync().
    /// Contains either a valid file path (on success) or a failure reason (on error).
    /// </summary>
    public class ApkDownloadResult
    {
        /// <summary>Absolute path to the saved APK file. Null on failure.</summary>
        public string FilePath { get; set; }

        /// <summary>Human-readable failure reason. Empty on success.</summary>
        public string FailReason { get; set; }

        /// <summary>True when the download and APK validation both succeeded.</summary>
        public bool IsSuccess
        {
            get { return !string.IsNullOrEmpty(FilePath); }
        }

        /// <summary>Creates a successful result.</summary>
        public static ApkDownloadResult Success(string filePath)
        {
            return new ApkDownloadResult { FilePath = filePath, FailReason = string.Empty };
        }

        /// <summary>Creates a failed result with a specific reason.</summary>
        public static ApkDownloadResult Failure(string reason)
        {
            return new ApkDownloadResult { FilePath = null, FailReason = reason };
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ApkDownloaderService
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads an APK from Google Drive, Dropbox, or any direct HTTPS URL.
    ///
    /// Key features:
    ///   • Uses drive.usercontent.google.com — Google's newer download domain
    ///     that reliably bypasses the virus-scan confirmation page.
    ///   • Cookie container support — required for Google Drive large-file downloads.
    ///   • APK magic byte validation — rejects HTML pages saved as .apk files,
    ///     which previously caused "There was a problem parsing the package".
    ///   • Returns ApkDownloadResult instead of using an illegal async out param.
    /// </summary>
    public class ApkDownloaderService
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const string UPDATE_FOLDER = "update";
        private const string APK_FILE_NAME = "Finder_update.apk";
        private const int BUFFER_SIZE = 16384;   // 16 KB read buffer
        private const long MIN_APK_BYTES = 100000L; // 100 KB — HTML pages are smaller

        // All APK files are ZIP archives and start with these 4 bytes: PK..
        private static readonly byte[] ZIP_MAGIC =
            new byte[] { 0x50, 0x4B, 0x03, 0x04 };

        private static readonly TimeSpan DOWNLOAD_TIMEOUT = TimeSpan.FromMinutes(15);

        // ── Fields ────────────────────────────────────────────────────────────

        private readonly Context _context;

        // ── Constructor ───────────────────────────────────────────────────────

        public ApkDownloaderService(Context context)
        {
            _context = context;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Downloads and validates an APK from the given URL.
        ///
        /// Returns ApkDownloadResult.IsSuccess == true and a valid FilePath on success.
        /// Returns ApkDownloadResult.IsSuccess == false and a FailReason on any error.
        ///
        /// Parameters:
        ///   url        — Google Drive, Dropbox, or direct HTTPS URL.
        ///   onProgress — Optional 0–100 integer progress callback.
        /// </summary>
        public async Task<ApkDownloadResult> DownloadApkAsync(
            string url,
            Action<int> onProgress)
        {
            try
            {
                // ── Step 1: Prepare destination path ──────────────────────────
                string destPath = GetApkDestinationPath();
                if (destPath == null)
                    return ApkDownloadResult.Failure("Cannot access device storage.");

                string updateDir = Path.GetDirectoryName(destPath);
                if (!Directory.Exists(updateDir))
                    Directory.CreateDirectory(updateDir);

                // Remove any previous partial download to avoid corruption
                if (File.Exists(destPath))
                    File.Delete(destPath);

                // ── Step 2: Resolve URL to a direct download link ─────────────
                string resolvedUrl = ResolveDownloadUrl(url);
                System.Diagnostics.Debug.WriteLine(
                    "[ApkDownloader] Resolved URL: " + resolvedUrl);

                // ── Step 3: Build HttpClient with cookie container ─────────────
                // CookieContainer is required for Google Drive large-file downloads.
                // Google sets a cookie on the first request; the actual APK download
                // only works when that cookie is echoed back on the second request.
                var cookieContainer = new CookieContainer();
                var handler = new HttpClientHandler
                {
                    CookieContainer = cookieContainer,
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 10
                };

                using (var client = new HttpClient(handler)
                { Timeout = DOWNLOAD_TIMEOUT })
                {
                    // Mimic a real browser — helps bypass CDN and Drive restrictions
                    client.DefaultRequestHeaders.Add("User-Agent",
                        "Mozilla/5.0 (Linux; Android 10; Mobile) " +
                        "AppleWebKit/537.36 (KHTML, like Gecko) " +
                        "Chrome/120.0.0.0 Mobile Safari/537.36");

                    client.DefaultRequestHeaders.Add("Accept",
                        "text/html,application/xhtml+xml,application/xml;" +
                        "q=0.9,*/*;q=0.8");

                    // ── Step 4: First request ─────────────────────────────────
                    var response = await client.GetAsync(
                        resolvedUrl,
                        HttpCompletionOption.ResponseHeadersRead);

                    // ── Step 5: Handle Google Drive HTML confirm page ──────────
                    string contentType = GetContentType(response);
                    bool isHtml = contentType.IndexOf(
                        "text/html", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (isHtml)
                    {
                        // Read the HTML to extract the confirm token if present
                        string html = await response.Content.ReadAsStringAsync();
                        response.Dispose();

                        string confirmUrl = ExtractConfirmUrl(resolvedUrl, html);

                        System.Diagnostics.Debug.WriteLine(
                            "[ApkDownloader] HTML page received, retrying: " + confirmUrl);

                        // Second attempt with cookies set by the first request
                        response = await client.GetAsync(
                            confirmUrl,
                            HttpCompletionOption.ResponseHeadersRead);

                        contentType = GetContentType(response);
                        isHtml = contentType.IndexOf(
                            "text/html", StringComparison.OrdinalIgnoreCase) >= 0;

                        if (isHtml)
                        {
                            // Still HTML — file is not publicly shared
                            response.Dispose();
                            return ApkDownloadResult.Failure(
                                "Google Drive returned a login/permission page.\n" +
                                "Make sure the file is shared as " +
                                "'Anyone with the link' → Viewer.");
                        }
                    }

                    response.EnsureSuccessStatusCode();

                    // ── Step 6: Stream to file with progress reporting ─────────
                    long totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    long downloadedBytes = 0L;
                    int lastReported = -1;

                    using (var networkStream =
                        await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(
                        destPath, FileMode.Create, FileAccess.Write,
                        FileShare.None, BUFFER_SIZE, useAsync: true))
                    {
                        byte[] buffer = new byte[BUFFER_SIZE];
                        int bytesRead;

                        while ((bytesRead = await networkStream
                            .ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            downloadedBytes += bytesRead;

                            if (totalBytes > 0 && onProgress != null)
                            {
                                int pct = (int)((downloadedBytes * 100L) / totalBytes);
                                if (pct != lastReported)
                                {
                                    lastReported = pct;
                                    onProgress(pct);
                                }
                            }
                        }

                        await fileStream.FlushAsync();
                    }

                    response.Dispose();
                }

                // Always fire 100% so the UI bar reaches the end
                onProgress?.Invoke(100);

                // ── Step 7: Validate the file is a real APK ───────────────────
                string validationError = ValidateApkFile(destPath);
                if (validationError != null)
                {
                    // Delete the invalid file — do not leave a corrupt APK on disk
                    try { File.Delete(destPath); } catch { }
                    return ApkDownloadResult.Failure(validationError);
                }

                System.Diagnostics.Debug.WriteLine(
                    "[ApkDownloader] APK validated successfully: " + destPath);

                return ApkDownloadResult.Success(destPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ApkDownloader] Exception: " + ex.Message);
                return ApkDownloadResult.Failure(ex.Message);
            }
        }

        /// <summary>Returns the absolute path where the APK will be saved.</summary>
        public string GetApkDestinationPath()
        {
            try
            {
                var externalDir = _context.GetExternalFilesDir(null);
                if (externalDir == null) return null;
                return Path.Combine(
                    externalDir.AbsolutePath, UPDATE_FOLDER, APK_FILE_NAME);
            }
            catch { return null; }
        }

        // ── APK validation ────────────────────────────────────────────────────

        /// <summary>
        /// Validates the downloaded file is a real APK (ZIP archive).
        ///
        /// Checks performed:
        ///   1. File exists on disk.
        ///   2. File size is at least MIN_APK_BYTES (100 KB).
        ///      An HTML confirmation page is typically only a few KB.
        ///   3. First 4 bytes match ZIP magic number: 50 4B 03 04 (PK..)
        ///      Every APK is a ZIP and must start with these bytes.
        ///
        /// Returns null if the file is valid.
        /// Returns a human-readable error string if the file is invalid.
        /// </summary>
        private static string ValidateApkFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return "Downloaded file not found on device storage.";

                long fileSize = new FileInfo(path).Length;

                if (fileSize == 0)
                    return "Downloaded file is empty (0 bytes).";

                if (fileSize < MIN_APK_BYTES)
                {
                    return string.Format(
                        "File too small ({0} KB) — not a valid APK.\n" +
                        "Google Drive likely returned an HTML page.\n" +
                        "Ensure the file is shared as 'Anyone with the link'.",
                        fileSize / 1024);
                }

                // Read and compare the 4-byte ZIP magic header
                byte[] header = new byte[4];
                using (var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int read = fs.Read(header, 0, 4);
                    if (read < 4)
                        return "Cannot read file header — file may be corrupted.";
                }

                bool validMagic =
                    header[0] == ZIP_MAGIC[0] &&
                    header[1] == ZIP_MAGIC[1] &&
                    header[2] == ZIP_MAGIC[2] &&
                    header[3] == ZIP_MAGIC[3];

                if (!validMagic)
                {
                    string got = string.Format(
                        "{0:X2} {1:X2} {2:X2} {3:X2}",
                        header[0], header[1], header[2], header[3]);

                    return string.Format(
                        "Not a valid APK — bad file header ({0}).\n" +
                        "Google Drive returned an HTML page instead of the APK.\n" +
                        "Use this URL format:\n" +
                        "drive.usercontent.google.com/download?id=FILE_ID" +
                        "&export=download&confirm=t", got);
                }

                return null; // All checks passed — file is valid
            }
            catch (Exception ex)
            {
                return "Validation error: " + ex.Message;
            }
        }

        // ── URL resolution ────────────────────────────────────────────────────

        /// <summary>
        /// Converts Google Drive share/view URLs and Dropbox links to direct
        /// download URLs. Uses drive.usercontent.google.com for reliability.
        /// </summary>
        private static string ResolveDownloadUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;

            // Google Drive: /file/d/{ID}/view → usercontent download URL
            if (url.IndexOf("drive.google.com/file/d/",
                StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string fileId = ExtractDriveFileId(url);
                if (!string.IsNullOrEmpty(fileId))
                    return BuildDriveUrl(fileId);
            }

            // Google Drive: uc?export=download → rewrite to usercontent
            if (url.IndexOf("drive.google.com/uc",
                StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string fileId = ExtractDriveIdParam(url);
                if (!string.IsNullOrEmpty(fileId))
                    return BuildDriveUrl(fileId);
                return EnsureConfirmParam(url);
            }

            // drive.usercontent.google.com — already correct domain
            if (url.IndexOf("drive.usercontent.google.com",
                StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return EnsureConfirmParam(url);
            }

            // Dropbox: force direct download
            if (url.IndexOf("dropbox.com",
                StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (url.IndexOf("?dl=0", StringComparison.OrdinalIgnoreCase) >= 0)
                    return url.Replace("?dl=0", "?dl=1");
                if (url.IndexOf("dl=0", StringComparison.OrdinalIgnoreCase) >= 0)
                    return url.Replace("dl=0", "dl=1");
                return url;
            }

            return url; // Any other URL — return unchanged
        }

        private static string BuildDriveUrl(string fileId)
        {
            return "https://drive.usercontent.google.com/download" +
                   "?id=" + fileId +
                   "&export=download" +
                   "&confirm=t" +
                   "&authuser=0";
        }

        private static string ExtractDriveFileId(string url)
        {
            try
            {
                int marker = url.IndexOf("/file/d/", StringComparison.Ordinal);
                if (marker < 0) return string.Empty;

                int idStart = marker + 8;
                int slashEnd = url.IndexOf("/", idStart, StringComparison.Ordinal);
                int queryEnd = url.IndexOf("?", idStart, StringComparison.Ordinal);

                int idEnd;
                if (slashEnd >= 0 && queryEnd >= 0) idEnd = Math.Min(slashEnd, queryEnd);
                else if (slashEnd >= 0) idEnd = slashEnd;
                else if (queryEnd >= 0) idEnd = queryEnd;
                else idEnd = url.Length;

                return url.Substring(idStart, idEnd - idStart);
            }
            catch { return string.Empty; }
        }

        private static string ExtractDriveIdParam(string url)
        {
            try
            {
                int pos = url.IndexOf("id=", StringComparison.OrdinalIgnoreCase);
                if (pos < 0) return string.Empty;

                int idStart = pos + 3;
                int idEnd = url.IndexOf("&", idStart, StringComparison.Ordinal);
                if (idEnd < 0) idEnd = url.Length;

                return url.Substring(idStart, idEnd - idStart);
            }
            catch { return string.Empty; }
        }

        private static string ExtractConfirmUrl(string originalUrl, string html)
        {
            try
            {
                int pos = html.IndexOf("confirm=", StringComparison.OrdinalIgnoreCase);
                if (pos >= 0)
                {
                    int tokenStart = pos + 8;
                    int ampEnd = html.IndexOf("&amp;", tokenStart,
                        StringComparison.Ordinal);
                    int quoteEnd = html.IndexOf("\"", tokenStart,
                        StringComparison.Ordinal);

                    int tokenEnd;
                    if (ampEnd >= 0 && quoteEnd >= 0) tokenEnd = Math.Min(ampEnd, quoteEnd);
                    else if (ampEnd >= 0) tokenEnd = ampEnd;
                    else if (quoteEnd >= 0) tokenEnd = quoteEnd;
                    else tokenEnd = tokenStart + 20;

                    string token = html.Substring(tokenStart, tokenEnd - tokenStart);

                    string fileId = ExtractDriveIdParam(originalUrl);
                    if (string.IsNullOrEmpty(fileId))
                        fileId = ExtractDriveFileId(originalUrl);

                    if (!string.IsNullOrEmpty(fileId) && !string.IsNullOrEmpty(token))
                        return BuildDriveUrl(fileId).Replace("confirm=t", "confirm=" + token);
                }
            }
            catch { }

            return EnsureConfirmParam(originalUrl);
        }

        private static string EnsureConfirmParam(string url)
        {
            if (url.IndexOf("confirm=", StringComparison.OrdinalIgnoreCase) >= 0)
                return url;
            return url + (url.IndexOf("?", StringComparison.Ordinal) >= 0
                ? "&confirm=t"
                : "?confirm=t");
        }

        private static string GetContentType(HttpResponseMessage response)
        {
            try
            {
                return response?.Content?.Headers?.ContentType?.MediaType
                       ?? string.Empty;
            }
            catch { return string.Empty; }
        }
    }
}