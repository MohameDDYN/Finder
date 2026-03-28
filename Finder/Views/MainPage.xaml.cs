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
            Resources.Add("InverseBoolConverter", new Finder.Converters.InverseBoolConverter());

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
            await _viewModel.InitializeAsync();
        }

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

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            _viewModel.RequestOpenSettings -= OnRequestOpenSettings;
            _viewModel.RequestViewHistory -= OnRequestViewHistory;
            _viewModel.ShowAlert -= OnShowAlert;
            _viewModel.ShowSuccess -= OnShowSuccess;
        }
    }
}