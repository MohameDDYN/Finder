using System.Text.Json;
using Finder.Models;
using Microsoft.Maui.Storage;

namespace Finder.Services
{
    /// <summary>
    /// Persists AppSettings across app restarts.
    /// - BotToken (a credential) goes into SecureStorage, which is encrypted at rest
    ///   on Android (backed by the Android Keystore).
    /// - ChatId is not sensitive on its own, so it's kept in a small JSON file for
    ///   simplicity and so it's easy to inspect/back up if needed.
    /// </summary>
    public class SettingsService : ISettingsService
    {
        private const string BotTokenKey = "finder_bot_token";
        private static readonly string SettingsFilePath =
            Path.Combine(FileSystem.AppDataDirectory, "settings.json");

        public async Task<AppSettings> LoadAsync()
        {
            var settings = new AppSettings();

            try
            {
                settings.BotToken = await SecureStorage.Default.GetAsync(BotTokenKey) ?? string.Empty;
            }
            catch
            {
                // SecureStorage can throw on some devices/OEM ROMs if the keystore
                // entry was invalidated (e.g. after a lock-screen change). Treat as
                // "no token saved yet" rather than crashing the page.
                settings.BotToken = string.Empty;
            }

            if (File.Exists(SettingsFilePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(SettingsFilePath);
                    var stored = JsonSerializer.Deserialize<StoredNonSecretSettings>(json);
                    if (stored is not null)
                    {
                        settings.ChatId = stored.ChatId;
                    }
                }
                catch
                {
                    // Corrupt or unreadable file — fall back to defaults rather than
                    // blocking the user from opening the page at all.
                }
            }

            return settings;
        }

        public async Task SaveAsync(AppSettings settings)
        {
            await SecureStorage.Default.SetAsync(BotTokenKey, settings.BotToken ?? string.Empty);

            var stored = new StoredNonSecretSettings { ChatId = settings.ChatId ?? string.Empty };
            var json = JsonSerializer.Serialize(stored);
            await File.WriteAllTextAsync(SettingsFilePath, json);
        }

        private class StoredNonSecretSettings
        {
            public string ChatId { get; set; } = string.Empty;
        }
    }
}