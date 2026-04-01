using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android.Content;
using Android.OS;
using Finder.Models;
using Newtonsoft.Json;
using AndroidLocation = Android.Locations.Location;

namespace Finder.Droid.Managers
{
    /// <summary>
    /// Manages daily location data files and generates GeoJSON exports.
    /// Each day's data is stored in a separate JSON file under the LocationData folder.
    /// FIX 5: AddLocationPoint now uses an in-memory write buffer.
    ///         Disk writes happen once per BUFFER_FLUSH_SIZE points (default 5)
    ///         instead of on every GPS fix — 80% reduction in storage I/O.
    /// </summary>
    public class GeoJsonManager
    {
        // ── FIX 5: Write buffer ───────────────────────────────────────────────
        /// <summary>
        /// Number of location points to accumulate before flushing to disk.
        /// 5 points = 80% fewer disk writes with no meaningful data loss risk.
        /// Call FlushBuffer() explicitly before the service stops.
        /// </summary>
        private const int BUFFER_FLUSH_SIZE = 5;

        private readonly List<LocationData> _writeBuffer = new List<LocationData>();
        private readonly object _bufferLock = new object();
        // ─────────────────────────────────────────────────────────────────────

        private readonly string _dataDirectory;
        private readonly string _currentDayFile;
        private readonly Context _context;

        // File-level lock prevents concurrent read-modify-write races
        private readonly object _fileLock = new object();

        public GeoJsonManager(Context context)
        {
            _context = context;
            _dataDirectory = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal),
                "LocationData");

            Directory.CreateDirectory(_dataDirectory);

            _currentDayFile = Path.Combine(
                _dataDirectory,
                $"locations_{DateTime.Now:yyyy-MM-dd}.json");
        }

        // ── Write ──────────────────────────────────────────────────────────

        /// <summary>
        /// FIX 5: Adds a GPS point to the in-memory buffer.
        /// When the buffer reaches BUFFER_FLUSH_SIZE points, it is automatically
        /// flushed to disk in a single read-merge-write operation.
        /// Call FlushBuffer() when the service is stopping to persist remaining points.
        /// </summary>
        public void AddLocationPoint(
            AndroidLocation location, string updateType = "automatic")
        {
            try
            {
                var point = new LocationData
                {
                    Timestamp = DateTime.UtcNow,
                    Latitude = location.Latitude,
                    Longitude = location.Longitude,
                    Accuracy = location.HasAccuracy ? (float?)location.Accuracy : null,
                    Speed = location.HasSpeed ? (float?)location.Speed : null,
                    Bearing = location.HasBearing ? (float?)location.Bearing : null,
                    Altitude = location.HasAltitude ? (double?)location.Altitude : null,
                    BatteryLevel = GetBatteryLevel(),
                    UpdateType = updateType,
                    SentToTelegram = false
                };

                bool shouldFlush;
                lock (_bufferLock)
                {
                    _writeBuffer.Add(point);
                    // Flush when buffer is full
                    shouldFlush = _writeBuffer.Count >= BUFFER_FLUSH_SIZE;
                }

                if (shouldFlush)
                    FlushBuffer();
            }
            catch { /* Silent fail */ }
        }

        /// <summary>
        /// FIX 5: Writes all buffered points to disk in a single operation.
        /// Called automatically when the buffer fills, and explicitly by
        /// BackgroundLocationService.OnDestroy() to prevent data loss on stop.
        /// Thread-safe — safe to call from any thread.
        /// </summary>
        public void FlushBuffer()
        {
            List<LocationData> toWrite;

            lock (_bufferLock)
            {
                if (_writeBuffer.Count == 0) return;

                // Snapshot and clear atomically
                toWrite = new List<LocationData>(_writeBuffer);
                _writeBuffer.Clear();
            }

            // One read-merge-write per flush (instead of per point)
            lock (_fileLock)
            {
                try
                {
                    var existing = LoadDayLocations(_currentDayFile);
                    existing.AddRange(toWrite);
                    SaveLocationData(existing, _currentDayFile);
                }
                catch { /* Silent fail */ }
            }
        }

        // ── Read ───────────────────────────────────────────────────────────

        /// <summary>Returns all saved location file names, newest first.</summary>
        public List<string> GetAvailableDataFiles()
        {
            try
            {
                return Directory.GetFiles(_dataDirectory, "locations_*.json")
                    .Select(Path.GetFileName)
                    .OrderByDescending(f => f)
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        /// <summary>Returns the file path for a given date's location data.</summary>
        public string GetFilePathForDate(DateTime date)
            => Path.Combine(_dataDirectory, $"locations_{date:yyyy-MM-dd}.json");

        // ── GeoJSON generation ─────────────────────────────────────────────

        /// <summary>Generates a GeoJSON FeatureCollection for the given date.</summary>
        public async Task<string> GenerateGeoJsonForDate(DateTime date)
        {
            // Flush buffer first so the latest points are included in the report
            FlushBuffer();

            return await Task.Run(() =>
            {
                try
                {
                    string filePath = GetFilePathForDate(date);
                    if (!File.Exists(filePath)) return null;

                    var locations = JsonConvert.DeserializeObject<List<LocationData>>(
                        File.ReadAllText(filePath));

                    if (locations == null || !locations.Any()) return null;

                    var sorted = locations.OrderBy(l => l.Timestamp).ToList();

                    var geoJson = new GeoJsonFeatureCollection
                    {
                        Metadata = new GeoJsonMetadata
                        {
                            DeviceId = GetDeviceId(),
                            AppVersion = GetAppVersion(),
                            TrackingDate = date.Date,
                            TotalPoints = sorted.Count,
                            DistanceTraveledKm = CalculateTotalDistanceKm(sorted),
                            TrackingDurationHours = CalculateDurationHours(sorted)
                        }
                    };

                    var start = sorted.First().Timestamp;
                    var end = sorted.Last().Timestamp;

                    // Path LineString
                    if (sorted.Count > 1)
                    {
                        var coords = sorted
                            .Select(l => new double[] { l.Longitude, l.Latitude })
                            .ToArray();
                        geoJson.Features.Add(new GeoJsonFeature
                        {
                            Geometry = new GeoJsonGeometry
                            {
                                Type = "LineString",
                                Coordinates = coords
                            },
                            Properties = new GeoJsonProperties
                            {
                                Name = $"Route {date:yyyy-MM-dd}",
                                Description =
                                    $"From {start:HH:mm} to {end:HH:mm} · {sorted.Count} points",
                                Timestamp = start,
                                UpdateType = "path_line",
                                Color = "#FF0000",
                                Folder = "Routes"
                            }
                        });
                    }

                    // Individual point features
                    int seq = 1;
                    foreach (var loc in sorted)
                    {
                        double elapsed = (loc.Timestamp - start).TotalMinutes;
                        geoJson.Features.Add(new GeoJsonFeature
                        {
                            Geometry = new GeoJsonGeometry
                            {
                                Type = "Point",
                                Coordinates = new double[] { loc.Longitude, loc.Latitude }
                            },
                            Properties = new GeoJsonProperties
                            {
                                Name = $"Point #{seq}",
                                Description =
                                    BuildPointDescription(seq, loc.Timestamp.ToString("HH:mm:ss"),
                                        elapsed, loc),
                                Timestamp = loc.Timestamp,
                                UpdateType = loc.UpdateType,
                                Color = "#0000FF",
                                Folder = "Points"
                            }
                        });
                        seq++;
                    }

                    return JsonConvert.SerializeObject(geoJson, Formatting.Indented);
                }
                catch { return null; }
            });
        }

        // ── Cleanup ────────────────────────────────────────────────────────

        public async Task CleanupOldFiles(int keepDays = 30)
        {
            await Task.Run(() =>
            {
                try
                {
                    var cutoff = DateTime.Now.AddDays(-keepDays);
                    foreach (var file in Directory.GetFiles(
                                 _dataDirectory, "locations_*.json"))
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        if (name.StartsWith("locations_") &&
                            DateTime.TryParseExact(
                                name.Substring(10), "yyyy-MM-dd",
                                null,
                                System.Globalization.DateTimeStyles.None,
                                out DateTime fileDate) &&
                            fileDate < cutoff)
                        {
                            File.Delete(file);
                        }
                    }
                }
                catch { }
            });
        }

        // ── Private helpers ────────────────────────────────────────────────

        private List<LocationData> LoadDayLocations(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                    return JsonConvert.DeserializeObject<List<LocationData>>(
                               File.ReadAllText(filePath))
                           ?? new List<LocationData>();
            }
            catch { }
            return new List<LocationData>();
        }

        private void SaveLocationData(List<LocationData> locations, string filePath)
        {
            try
            {
                File.WriteAllText(filePath,
                    JsonConvert.SerializeObject(locations, Formatting.Indented));
            }
            catch { }
        }

        private string BuildPointDescription(
            int seq, string time, double elapsed, LocationData loc)
        {
            string desc = $"Point #{seq}\nTime: {time}\nElapsed: {elapsed:F1} min";
            if (loc.Speed.HasValue) desc += $"\nSpeed: {loc.Speed:F1} m/s";
            if (loc.Accuracy.HasValue) desc += $"\nAccuracy: ±{loc.Accuracy:F0} m";
            if (loc.BatteryLevel > 0) desc += $"\nBattery: {loc.BatteryLevel}%";
            return desc;
        }

        private double CalculateTotalDistanceKm(List<LocationData> locs)
        {
            if (locs.Count < 2) return 0;
            double total = 0;
            for (int i = 1; i < locs.Count; i++)
                total += HaversineMeters(
                    locs[i - 1].Latitude, locs[i - 1].Longitude,
                    locs[i].Latitude, locs[i].Longitude);
            return total / 1000.0;
        }

        private double CalculateDurationHours(List<LocationData> locs)
        {
            if (locs.Count < 2) return 0;
            return (locs.Last().Timestamp - locs.First().Timestamp).TotalHours;
        }

        private static double HaversineMeters(
            double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000;
            double dLat = (lat2 - lat1) * Math.PI / 180;
            double dLon = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                          Math.Cos(lat1 * Math.PI / 180) *
                          Math.Cos(lat2 * Math.PI / 180) *
                          Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        private int GetBatteryLevel()
        {
            try
            {
                var filter = new Android.Content.IntentFilter(
                    Android.Content.Intent.ActionBatteryChanged);
                var battery = _context.RegisterReceiver(null, filter);
                int level = battery?.GetIntExtra(BatteryManager.ExtraLevel, -1) ?? -1;
                int scale = battery?.GetIntExtra(BatteryManager.ExtraScale, 1) ?? 1;
                return scale > 0 ? (int)(level * 100f / scale) : -1;
            }
            catch { return -1; }
        }

        private static string GetDeviceId()
        {
            try { return Build.Model ?? "Unknown"; }
            catch { return "Unknown"; }
        }

        private static string GetAppVersion()
        {
            try { return Build.VERSION.Release ?? "Unknown"; }
            catch { return "Unknown"; }
        }
    }
}