using Android.Content;

namespace Finder.Droid.Managers
{
    /// <summary>
    /// Static singleton that runs a TelegramCommandHandler inside the app process
    /// when the BackgroundLocationService is not running.
    ///
    /// Ownership rules:
    ///   • Service starts  → AppCommandHandler.Stop() is called first
    ///   • Service stops   → AppCommandHandler.Start() is called by LocationService
    ///   • App foreground  → AppCommandHandler.Start() called from MainActivity.OnResume
    ///   • App background  → AppCommandHandler.Stop()  called from MainActivity.OnPause
    ///
    /// This guarantees the handler and the service never poll simultaneously.
    /// </summary>
    public static class AppCommandHandler
    {
        private static TelegramCommandHandler _handler;

        /// <summary>True while this app-side handler is actively polling Telegram.</summary>
        public static bool IsActive => _handler != null;

        /// <summary>
        /// Starts polling if not already active.
        /// Safe to call multiple times — will not create duplicate handlers.
        /// </summary>
        public static void Start(Context context, bool sendStartupMessage = false)
        {
            if (_handler != null) return;

            try
            {
                _handler = new TelegramCommandHandler(context);
                _handler.Start(sendStartupMessage);
            }
            catch { /* Silent fail */ }
        }

        /// <summary>
        /// Stops polling and releases the handler.
        /// Safe to call when already stopped.
        /// </summary>
        public static void Stop()
        {
            try
            {
                _handler?.Stop();
            }
            catch { /* Silent fail */ }
            finally
            {
                _handler = null;
            }
        }
    }
}