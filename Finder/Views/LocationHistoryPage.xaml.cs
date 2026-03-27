using System;
using Finder.Models;
using Finder.ViewModels;
using Xamarin.Forms;

namespace Finder.Views
{
    public partial class LocationHistoryPage : ContentPage
    {
        private readonly LocationHistoryViewModel _viewModel;

        public LocationHistoryPage()
        {
            Resources = new ResourceDictionary();
            Resources.Add("InverseBoolConverter", new Finder.Converters.InverseBoolConverter());

            InitializeComponent();

            _viewModel = new LocationHistoryViewModel();
            BindingContext = _viewModel;

            _viewModel.ShowAlert += OnShowAlert;
            _viewModel.RequestReport += OnRequestReport;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await _viewModel.LoadFilesAsync();
        }

        private async void OnRefreshClicked(object sender, EventArgs e)
        {
            await _viewModel.LoadFilesAsync();
        }

        private async void OnGetReportClicked(object sender, EventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is LocationFileInfo fileInfo)
            {
                bool confirm = await DisplayAlert(
                    "Get Report",
                    $"Request GeoJSON report for {fileInfo.DisplayName}?\nIt will be sent to your Telegram chat.",
                    "Send", "Cancel");

                if (confirm)
                    _viewModel.GetReportCommand.Execute(fileInfo);
            }
        }

        private async void OnRequestReport(object sender, LocationFileInfo fileInfo)
        {
            await DisplayAlert(
                "Report Requested",
                $"The GeoJSON report for {fileInfo.DisplayName} has been requested.\n\n" +
                "Use the /today or /report YYYY-MM-DD command in your Telegram bot to receive it.",
                "OK");
        }

        private async void OnShowAlert(object sender, string message)
        {
            await DisplayAlert("Notice", message, "OK");
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _viewModel.ShowAlert -= OnShowAlert;
            _viewModel.RequestReport -= OnRequestReport;
        }
    }
}