namespace Finder.Services
{
    /// <summary>
    /// Temporary stand-in for the real Android foreground-service-backed
    /// ILocationService. Lets MainPage/MainViewModel be built and tested before
    /// Platforms/Android/Services/LocationService.cs exists. Swap this out in
    /// MauiProgram.cs once the real implementation is ready:
    ///
    ///   #if ANDROID
    ///   builder.Services.AddSingleton&lt;ILocationService, Platforms.Android.Services.LocationService&gt;();
    ///   #else
    ///   builder.Services.AddSingleton&lt;ILocationService, StubLocationService&gt;();
    ///   #endif
    /// </summary>
    public class StubLocationService : ILocationService
    {
        public bool IsRunning { get; private set; }

        public Task StartTracking()
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopTracking()
        {
            IsRunning = false;
            return Task.CompletedTask;
        }
    }
}