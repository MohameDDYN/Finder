using Finder.ViewModels;

namespace Finder.Views
{
    public partial class MainPage : ContentPage
    {
        private readonly MainViewModel _viewModel;

        public MainPage(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            BindingContext = _viewModel;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await _viewModel.LoadAsync();
            _viewModel.OnAppearing();
        }

        protected override void OnDisappearing()
        {
            _viewModel.OnDisappearing();
            base.OnDisappearing();
        }
    }
}