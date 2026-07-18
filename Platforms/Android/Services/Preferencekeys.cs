namespace Finder.Platforms.Android.Services
{
    /// <summary>
    /// Shared Preferences keys. Centralized so BackgroundLocationService,
    /// LocationService, and BootReceiver (Step 6) all agree on the same names.
    /// </summary>
    public static class PreferenceKeys
    {
        /// <summary>True if the user asked the service to be running — read by
        /// BootReceiver after a reboot to decide whether to restart it.</summary>
        public const string WasRunning = "finder_was_running";

        /// <summary>Telegram getUpdates offset, so a restart doesn't reprocess
        /// old messages. Owned by TelegramCommandHandler (Step 4).</summary>
        public const string LastUpdateId = "finder_last_update_id";
    }
}