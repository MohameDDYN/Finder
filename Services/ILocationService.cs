namespace Finder.Services
{
    /// <summary>
    /// Shared contract for starting/stopping the on-demand location service.
    /// The real implementation is Android-only (Platforms/Android/Services/LocationService.cs);
    /// register it conditionally in MauiProgram.cs behind #if ANDROID.
    /// </summary>
    public interface ILocationService
    {
        bool IsRunning { get; }
        Task StartTracking();
        Task StopTracking();
    }
}