using System;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Finder.Droid.Managers;

namespace Finder.Droid
{
    [Activity(
        Label = "Finder",
        Icon = "@mipmap/icon",
        Theme = "@style/MainTheme",
        MainLauncher = true,
        ConfigurationChanges =
            ConfigChanges.ScreenSize |
            ConfigChanges.Orientation |
            ConfigChanges.UiMode |
            ConfigChanges.ScreenLayout |
            ConfigChanges.SmallestScreenSize)]
    public class MainActivity : global::Xamarin.Forms.Platform.Android.FormsAppCompatActivity
    {
        private TelegramCommandHandler _commandHandler;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Initialize Xamarin.Essentials
            Xamarin.Essentials.Platform.Init(this, savedInstanceState);

            // Initialize Xamarin.Forms
            global::Xamarin.Forms.Forms.Init(this, savedInstanceState);

            // Set current activity resolver for Plugin.Fingerprint
            Plugin.Fingerprint.CrossFingerprint.SetCurrentActivityResolver(() => this);

            // Load the shared App
            LoadApplication(new App());

            // Start the Telegram command handler for receiving bot commands
            try
            {
                _commandHandler = new TelegramCommandHandler(this);
                _commandHandler.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainActivity] CommandHandler start error: {ex.Message}");
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            _commandHandler?.Stop();
        }

        public override void OnRequestPermissionsResult(
            int requestCode,
            string[] permissions,
            [GeneratedEnum] Permission[] grantResults)
        {
            Xamarin.Essentials.Platform.OnRequestPermissionsResult(
                requestCode, permissions, grantResults);
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        }
    }
}