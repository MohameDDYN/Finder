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
    /// </summary>
    public class GeoJsonManager
    {
        private readonly string _dataDirectory;
        private readonly string _currentDayFile;
        private readonly Context _context;
        private readonly object _lockObject = new object();

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

        /// <summary>Adds a new GPS point to today's data file.</summary>
        public void AddLocationPoint(AndroidLocation location, string updateType = "automatic")
        {
            try
            {
                lock (_lockObject)
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

                    var list = LoadDayLocations(_currentDayFile);
                    list.Add(point);
                    SaveLocationData(list, _currentDayFile);
                }
            }
            catch { /* Silent fail */ }
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
            catch
            {
                return new List<string>();
            }
        }

        // ── GeoJSON generation ─────────────────────────────────────────────

        /// <summary>Generates a GeoJSON FeatureCollection for the given date.</summary>
        public async Task<string> GenerateGeoJsonForDate(DateTime date)
        {
            try
            {
                string filePath = Path.Combine(_dataDirectory, $"locations_{date:yyyy-MM-dd}.json");
                if (!File.Exists(filePath)) return null;

                var locations = JsonConvert.DeserializeObject<List<LocationData>>(
                    await File.ReadAllTextAsync(filePath));

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

                // Add path LineString if more than one point
                if (sorted.Count > 1)
                {
                    var coords = sorted.Select(l => new double[] { l.Longitude, l.Latitude }).ToArray();
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
                            Description = $"From {start:HH:mm} to {end:HH:mm} · {sorted.Count} points",
                            Timestamp = start,
                            UpdateType = "path_line",
                            Color = "#FF0000",
                            Folder = "Routes"
                        }
                    });
                }

                // Add individual point features
                int seq = 1;
                foreach (var loc in sorted)
                {
                    double elapsed = (loc.Timestamp - start).TotalMinutes;
                    string timeLabel = loc.Timestamp.ToString("HH:mm:ss");

                    string color = "#0000FF";
                    string folder = "Tracking Points";
                    string name = $"#{seq} @ {timeLabel}";

                    if (seq == 1) { color = "#00FF00"; folder = "Start/End Points"; name += " 🏁"; }
                    else if (seq == sorted.Count) { color = "#FF0000"; folder = "Start/End Points"; name += " 🎯"; }

                    geoJson.Features.Add(new GeoJsonFeature
                    {
                        Geometry = new GeoJsonGeometry
                        {
                            Type = "Point",
                            Coordinates = new double[] { loc.Longitude, loc.Latitude }
                        },
                        Properties = new GeoJsonProperties
                        {
                            Name = name,
                            Description = BuildPointDescription(seq, timeLabel, elapsed, loc),
                            Timestamp = loc.Timestamp,
                            SequenceNumber = seq,
                            TimeLabel = timeLabel,
                            ElapsedMinutes = Math.Round(elapsed, 1),
                            Accuracy = loc.Accuracy,
                            Speed = loc.Speed,
                            Bearing = loc.Bearing,
                            Altitude = loc.Altitude,
                            BatteryLevel = loc.BatteryLevel,
                            UpdateType = loc.UpdateType,
                            Color = color,
                            Folder = folder
                        }
                    });
                    seq++;
                }

                return JsonConvert.SerializeObject(geoJson, Formatting.Indented);
            }
            catch
            {
                return null;
            }
        }

        // ── Cleanup ────────────────────────────────────────────────────────

        public async Task CleanupOldFiles(int keepDays = 30)
        {
            await Task.Run(() =>
            {
                try
                {
                    var cutoff = DateTime.Now.AddDays(-keepDays);
                    foreach (var file in Directory.GetFiles(_dataDirectory, "locations_*.json"))
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
                catch { /* Silent fail */ }
            });
        }

        // ── Private helpers ────────────────────────────────────────────────

        private List<LocationData> LoadDayLocations(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                    return JsonConvert.DeserializeObject<List<LocationData>>(
                        File.ReadAllText(filePath)) ?? new List<LocationData>();
            }
            catch { /* Silent fail */ }
            return new List<LocationData>();
        }

        private void SaveLocationData(List<LocationData> locations, string filePath)
        {
            try { File.WriteAllText(filePath, JsonConvert.SerializeObject(locations, Formatting.Indented)); }
            catch { /* Silent fail */ }
        }

        private string BuildPointDescription(int seq, string time, double elapsed, LocationData loc)
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
                total += HaversineMeters(locs[i - 1].Latitude, locs[i - 1].Longitude,
                                         locs[i].Latitude, locs[i].Longitude);
            return total / 1000.0;
        }

        private double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000;
            double φ1 = lat1 * Math.PI / 180;
            double φ2 = lat2 * Math.PI / 180;
            double Δφ = (lat2 - lat1) * Math.PI / 180;
            double Δλ = (lon2 - lon1) * Math.PI / 180;
            double a = Math.Sin(Δφ / 2) * Math.Sin(Δφ / 2)
                      + Math.Cos(φ1) * Math.Cos(φ2)
                      * Math.Sin(Δλ / 2) * Math.Sin(Δλ / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        private double CalculateDurationHours(List<LocationData> locs)
        {
            if (locs.Count < 2) return 0;
            return (locs.Last().Timestamp - locs.First().Timestamp).TotalHours;
        }

        private int GetBatteryLevel()
        {
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
                {
                    var bm = (BatteryManager)_context.GetSystemService(Context.BatteryService);
                    return bm.GetIntProperty((int)BatteryProperty.Capacity);
                }
                else
                {
                    var filter = new IntentFilter(Intent.ActionBatteryChanged);
                    var status = _context.RegisterReceiver(null, filter);
                    if (status != null)
                    {
                        int level = status.GetIntExtra(BatteryManager.ExtraLevel, -1);
                        int scale = status.GetIntExtra(BatteryManager.ExtraScale, -1);
                        return (int)((level / (float)scale) * 100);
                    }
                }
            }
            catch { /* Silent fail */ }
            return -1;
        }

        private string GetDeviceId() =>
            Android.Provider.Settings.Secure.GetString(
                _context.ContentResolver,
                Android.Provider.Settings.Secure.AndroidId);

        private string GetAppVersion()
        {
            try
            {
                var pkg = _context.PackageManager.GetPackageInfo(_context.PackageName, 0);
                return pkg.VersionName;
            }
            catch { return "Unknown"; }
        }
    }
}