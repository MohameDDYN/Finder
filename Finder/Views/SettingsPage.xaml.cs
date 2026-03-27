using System;
using Finder.ViewModels;
using Plugin.Fingerprint;
using Xamarin.Forms;

namespace Finder.Views
{
    public partial class SettingsPage : ContentPage
    {
        private readonly SettingsViewModel _viewModel;

        public SettingsPage()
        {
            Resources = new ResourceDictionary();
            Resources.Add("InverseBoolConverter", new Finder.Converters.InverseBoolConverter());

            InitializeComponent();

            _viewModel = new SettingsViewModel();
            BindingContext = _viewModel;

            _viewModel.SettingsSaved += OnSettingsSaved;
            _viewModel.ShowAlert += OnShowAlert;
            _viewModel.ShowSuccess += OnShowSuccess;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // Check biometric availability and load settings
            bool biometricAvailable = false;
            try
            {
                biometricAvailable = await CrossFingerprint.Current.IsAvailableAsync();
            }
            catch { /* Device doesn't support biometrics */ }

            await _viewModel.LoadSettingsAsync(biometricAvailable);
        }

        private async void OnSettingsSaved(object sender, EventArgs e)
        {
            await Navigation.PopAsync();
        }

        private async void OnShowAlert(object sender, string message)
        {
            await DisplayAlert("Notice", message, "OK");
        }

        private async void OnShowSuccess(object sender, string message)
        {
            await DisplayAlert("✓ Saved", message, "OK");
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _viewModel.SettingsSaved -= OnSettingsSaved;
            _viewModel.ShowAlert -= OnShowAlert;
            _viewModel.ShowSuccess -= OnShowSuccess;
        }
    }
}