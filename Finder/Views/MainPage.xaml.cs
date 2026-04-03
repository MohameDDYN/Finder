using System;
using Finder.ViewModels;
using Xamarin.Forms;

namespace Finder.Views
{
    public partial class MainPage : ContentPage
    {
        private readonly MainViewModel _viewModel;

        public MainPage()
        {
            Resources = new ResourceDictionary();
            Resources.Add("InverseBoolConverter",
                new Finder.Converters.InverseBoolConverter());

            InitializeComponent();

            _viewModel = new MainViewModel();
            BindingContext = _viewModel;

            _viewModel.RequestOpenSettings += OnRequestOpenSettings;
            _viewModel.RequestViewHistory += OnRequestViewHistory;
            _viewModel.ShowAlert += OnShowAlert;
            _viewModel.ShowSuccess += OnShowSuccess;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // 1. Immediate status check — buttons reflect reality right away
            await _viewModel.InitializeAsync();

            // 2. Start the 5-second polling loop — keeps buttons in sync
            //    with remote /start and /stop Telegram commands while
            //    the page is visible.
            _viewModel.StartStatusPolling();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            _viewModel.StopStatusPolling();

            // ── ADD THIS LINE ──────────────────────────────────────────────────
            // Unsubscribe update messages to prevent memory leaks when page hides
            _viewModel.UnsubscribeUpdateMessages();

            _viewModel.RequestOpenSettings -= OnRequestOpenSettings;
            _viewModel.RequestViewHistory -= OnRequestViewHistory;
            _viewModel.ShowAlert -= OnShowAlert;
            _viewModel.ShowSuccess -= OnShowSuccess;
        }

        // ── Navigation helpers ─────────────────────────────────────────────────

        private async void OnSettingsToolbarClicked(object sender, EventArgs e)
        {
            await Navigation.PushAsync(new PasscodePage(isAppStartup: false));
        }

        private async void OnRequestOpenSettings(object sender, EventArgs e)
        {
            await Navigation.PushAsync(new PasscodePage(isAppStartup: false));
        }

        private async void OnRequestViewHistory(object sender, EventArgs e)
        {
            await Navigation.PushAsync(new LocationHistoryPage());
        }

        private async void OnShowAlert(object sender, string message)
        {
            await DisplayAlert("Notice", message, "OK");
        }

        private async void OnShowSuccess(object sender, string message)
        {
            await DisplayAlert("✓ Success", message, "OK");
        }
    }
}