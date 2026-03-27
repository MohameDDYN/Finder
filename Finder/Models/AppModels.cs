using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Finder.Models
{
    // ─────────────────────────────────────────────────────────────────────────
    // Application configuration model
    // ─────────────────────────────────────────────────────────────────────────

    public class AppSettings
    {
        public string BotToken { get; set; } = string.Empty;
        public string ChatId { get; set; } = string.Empty;
        public string Interval { get; set; } = "60000";
        public bool EnableDailyReports { get; set; } = true;
        public int ReportHour { get; set; } = 0;
        public int DataRetentionDays { get; set; } = 30;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Internal location storage model
    // ─────────────────────────────────────────────────────────────────────────

    public class LocationData
    {
        public DateTime Timestamp { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public float? Accuracy { get; set; }
        public float? Speed { get; set; }
        public float? Bearing { get; set; }
        public double? Altitude { get; set; }
        public int? BatteryLevel { get; set; }
        public string UpdateType { get; set; }
        public bool SentToTelegram { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UI model for location history list
    // ─────────────────────────────────────────────────────────────────────────

    public class LocationFileInfo
    {
        public string FileName { get; set; }
        public DateTime Date { get; set; }
        public string DisplayName { get; set; }
        public string DayOfWeek { get; set; }
        public string FilePath { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Telegram API response models
    // ─────────────────────────────────────────────────────────────────────────

    public class TelegramUpdateResponse
    {
        [JsonProperty("ok")]
        public bool Ok { get; set; }

        [JsonProperty("result")]
        public TelegramUpdate[] Result { get; set; }
    }

    public class TelegramUpdate
    {
        [JsonProperty("update_id")]
        public long UpdateId { get; set; }

        [JsonProperty("message")]
        public TelegramMessage Message { get; set; }
    }

    public class TelegramMessage
    {
        [JsonProperty("message_id")]
        public long MessageId { get; set; }

        [JsonProperty("from")]
        public TelegramUser From { get; set; }

        [JsonProperty("chat")]
        public TelegramChat Chat { get; set; }

        [JsonProperty("date")]
        public long Date { get; set; }

        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("document")]
        public TelegramDocument Document { get; set; }

        [JsonProperty("location")]
        public TelegramLocation Location { get; set; }
    }

    public class TelegramUser
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("is_bot")]
        public bool IsBot { get; set; }

        [JsonProperty("first_name")]
        public string FirstName { get; set; }

        [JsonProperty("last_name")]
        public string LastName { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }
    }

    public class TelegramChat
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("first_name")]
        public string FirstName { get; set; }

        [JsonProperty("last_name")]
        public string LastName { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }
    }

    public class TelegramDocument
    {
        [JsonProperty("file_name")]
        public string FileName { get; set; }

        [JsonProperty("mime_type")]
        public string MimeType { get; set; }

        [JsonProperty("file_id")]
        public string FileId { get; set; }

        [JsonProperty("file_unique_id")]
        public string FileUniqueId { get; set; }

        [JsonProperty("file_size")]
        public long FileSize { get; set; }
    }

    public class TelegramLocation
    {
        [JsonProperty("longitude")]
        public double Longitude { get; set; }

        [JsonProperty("latitude")]
        public double Latitude { get; set; }

        [JsonProperty("live_period")]
        public int? LivePeriod { get; set; }

        [JsonProperty("heading")]
        public int? Heading { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GeoJSON standard models
    // ─────────────────────────────────────────────────────────────────────────

    public class GeoJsonFeatureCollection
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "FeatureCollection";

        [JsonProperty("features")]
        public List<GeoJsonFeature> Features { get; set; } = new List<GeoJsonFeature>();

        [JsonProperty("metadata")]
        public GeoJsonMetadata Metadata { get; set; } = new GeoJsonMetadata();
    }

    public class GeoJsonFeature
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "Feature";

        [JsonProperty("geometry")]
        public GeoJsonGeometry Geometry { get; set; }

        [JsonProperty("properties")]
        public GeoJsonProperties Properties { get; set; }
    }

    public class GeoJsonGeometry
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "Point";

        [JsonProperty("coordinates")]
        public object Coordinates { get; set; }
    }

    public class GeoJsonProperties
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; }

        [JsonProperty("sequence_number")]
        public int? SequenceNumber { get; set; }

        [JsonProperty("time_label")]
        public string TimeLabel { get; set; }

        [JsonProperty("elapsed_minutes")]
        public double? ElapsedMinutes { get; set; }

        [JsonProperty("accuracy")]
        public float? Accuracy { get; set; }

        [JsonProperty("speed")]
        public float? Speed { get; set; }

        [JsonProperty("bearing")]
        public float? Bearing { get; set; }

        [JsonProperty("altitude")]
        public double? Altitude { get; set; }

        [JsonProperty("battery_level")]
        public int? BatteryLevel { get; set; }

        [JsonProperty("update_type")]
        public string UpdateType { get; set; }

        [JsonProperty("color")]
        public string Color { get; set; }

        [JsonProperty("folder")]
        public string Folder { get; set; }
    }

    public class GeoJsonMetadata
    {
        [JsonProperty("device_id")]
        public string DeviceId { get; set; }

        [JsonProperty("app_version")]
        public string AppVersion { get; set; }

        [JsonProperty("tracking_date")]
        public DateTime TrackingDate { get; set; }

        [JsonProperty("total_points")]
        public int TotalPoints { get; set; }

        [JsonProperty("distance_traveled_km")]
        public double DistanceTraveledKm { get; set; }

        [JsonProperty("tracking_duration_hours")]
        public double TrackingDurationHours { get; set; }
    }
}