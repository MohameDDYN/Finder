using Finder.Services;
using Finder.ViewModels;
using Finder.Views;
using Microsoft.Extensions.Logging;

namespace Finder
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

            builder.Services.AddSingleton<ISettingsService, SettingsService>();

#if ANDROID
            builder.Services.AddSingleton<ILocationService, Finder.Platforms.Android.Services.LocationService>();
#else
            builder.Services.AddSingleton<ILocationService, StubLocationService>();
#endif

            builder.Services.AddTransient<MainViewModel>();
            builder.Services.AddTransient<MainPage>();
            builder.Services.AddSingleton<AppShell>();

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}