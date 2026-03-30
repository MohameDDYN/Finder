using System;
using Finder.Views;
using Xamarin.Essentials;
using Xamarin.Forms;

namespace Finder
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();

            // Initialize default passcode if none set
            InitializeDefaultPasscode();

            // Check first-run status and navigate accordingly
            CheckFirstRunAsync();
        }

        /// <summary>
        /// Sets a default passcode of "1234" if none has been configured yet.
        /// </summary>
        private async void InitializeDefaultPasscode()
        {
            try
            {
                string existingPasscode = await SecureStorage.GetAsync("settings_passcode");
                if (string.IsNullOrEmpty(existingPasscode))
                {
                    await SecureStorage.SetAsync("settings_passcode", "1234");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] InitializeDefaultPasscode error: {ex.Message}");
            }
        }

        /// <summary>
        /// Decides which page to show on startup based on setup state and security settings.
        /// </summary>
        private async void CheckFirstRunAsync()
        {
            try
            {
                string setupCompleted = await SecureStorage.GetAsync("setup_completed");
                string passcodeAtStartup = await SecureStorage.GetAsync("passcode_at_startup") ?? "true";

                if (string.IsNullOrEmpty(setupCompleted))
                {
                    // First launch — show the setup wizard
                    MainPage = new NavigationPage(new FirstRunSetupPage())
                    {
                        BarBackgroundColor = (Color)Resources["PrimaryColor"],
                        BarTextColor = Color.White
                    };
                }
                else if (passcodeAtStartup == "true")
                {
                    // Passcode required — show passcode gate
                    MainPage = new NavigationPage(new PasscodePage(isAppStartup: true))
                    {
                        BarBackgroundColor = (Color)Resources["PrimaryColor"],
                        BarTextColor = Color.White
                    };
                }
                else
                {
                    // No passcode required — go straight to main screen
                    MainPage = new NavigationPage(new Views.MainPage())
                    {
                        BarBackgroundColor = (Color)Resources["PrimaryColor"],
                        BarTextColor = Color.White
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] CheckFirstRunAsync error: {ex.Message}");
                // Fail safe: show passcode page
                MainPage = new NavigationPage(new PasscodePage(isAppStartup: true))
                {
                    BarBackgroundColor = (Color)Resources["PrimaryColor"],
                    BarTextColor = Color.White
                };
            }
        }

        /// <summary>
        /// Helper to create a styled NavigationPage wrapper.
        /// </summary>
        public static NavigationPage CreateNavPage(Page page)
        {
            return new NavigationPage(page)
            {
                BarBackgroundColor = (Color)Current.Resources["PrimaryColor"],
                BarTextColor = Color.White
            };
        }
    }
}