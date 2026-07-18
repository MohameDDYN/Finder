#if ANDROID
using Android;

namespace Finder.Platforms.Android.Permissions
{
    /// <summary>
    /// MAUI's Permissions API doesn't include POST_NOTIFICATIONS out of the box
    /// (it's an Android 13+ runtime permission), so we declare it the same way
    /// Microsoft's own docs recommend: subclass BasePlatformPermission and list
    /// the manifest permission it maps to.
    /// </summary>
    public class PostNotificationsPermission : Microsoft.Maui.ApplicationModel.Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            new List<(string androidPermission, bool isRuntime)>
            {
                (Manifest.Permission.PostNotifications, true)
            }.ToArray();
    }
}
#endif