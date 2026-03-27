using System;
using Finder.ViewModels;
using Xamarin.Forms;

namespace Finder.Views
{
    public partial class FirstRunSetupPage : ContentPage
    {
        private readonly FirstRunSetupViewModel _viewModel;

        public FirstRunSetupPage()
        {
            InitializeComponent();

            _viewModel = new FirstRunSetupViewModel();
            BindingContext = _viewModel;

            _viewModel.SetupCompleted += OnSetupCompleted;
            _viewModel.ShowAlert += OnShowAlert;
        }

        private void OnSetupCompleted(object sender, EventArgs e)
        {
            // Navigate to main page, replacing the navigation stack
            Application.Current.MainPage = App.CreateNavPage(new MainPage());
        }

        private async void OnShowAlert(object sender, string message)
        {
            await DisplayAlert("Setup Error", message, "OK");
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _viewModel.SetupCompleted -= OnSetupCompleted;
            _viewModel.ShowAlert -= OnShowAlert;
        }
    }
}