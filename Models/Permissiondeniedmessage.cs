namespace Finder.Models
{
    /// <summary>
    /// Sent via WeakReferenceMessenger when the user denies a location permission,
    /// so MainViewModel can surface an alert without MainActivity needing a direct
    /// reference to the page/viewmodel.
    /// </summary>
    public class PermissionDeniedMessage
    {
        public PermissionDeniedMessage(string permissionName)
        {
            PermissionName = permissionName;
        }

        public string PermissionName { get; }
    }
}