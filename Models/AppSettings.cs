namespace Finder.Models
{
    /// <summary>
    /// Non-sensitive app settings, persisted as JSON in FileSystem.AppDataDirectory.
    /// The BotToken itself is NOT stored here in production — see SettingsService,
    /// which routes BotToken through SecureStorage instead. This class exists so the
    /// rest of the app has a single, simple shape to bind to and pass around.
    /// </summary>
    public class AppSettings
    {
        public string BotToken { get; set; } = string.Empty;
        public string ChatId { get; set; } = string.Empty;
    }
}