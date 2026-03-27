using System.Threading.Tasks;
using Xamarin.Essentials;

namespace Finder.Services
{
    /// <summary>
    /// Contract for location tracking service, implemented per platform.
    /// </summary>
    public interface ILocationService
    {
        /// <summary>Starts the background location tracking foreground service.</summary>
        Task StartTracking();

        /// <summary>Stops the background location tracking service.</summary>
        Task StopTracking();

        /// <summary>Returns the most recent known device location.</summary>
        Task<Location> GetCurrentLocation();

        /// <summary>Returns true if the tracking service is currently active.</summary>
        Task<bool> IsTrackingActive();
    }
}