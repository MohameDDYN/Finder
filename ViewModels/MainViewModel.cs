using CommunityToolkit.Mvvm.Messaging;
using Finder.Models;
using Finder.Services;
using Microsoft.Maui.Graphics;

namespace Finder.ViewModels
{
    public class MainViewModel : ObservableObject, IDisposable, IRecipient<PermissionDeniedMessage>
    {
        private readonly ISettingsService _settingsService;
        private readonly ILocationService _locationService;
        private System.Threading.Timer? _statusTimer;

        private string _botToken = string.Empty;
        private string _chatId = string.Empty;
        private string _statusText = "Stopped";
        private Color _statusColor = Colors.Gray;
        private bool _isBusy;

        public MainViewModel(ISettingsService settingsService, ILocationService locationService)
        {
            _settingsService = settingsService;
            _locationService = locationService;

            SaveCommand = new RelayCommand(SaveAsync, () => !IsBusy);
            StartServiceCommand = new RelayCommand(StartServiceAsync, () => !IsBusy && !_locationService.IsRunning);
            StopServiceCommand = new RelayCommand(StopServiceAsync, () => !IsBusy && _locationService.IsRunning);

            WeakReferenceMessenger.Default.Register<PermissionDeniedMessage>(this);
        }

        public void Receive(PermissionDeniedMessage message)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (Application.Current?.MainPage is not null)
                {
                    var detail = message.PermissionName switch
                    {
                        "Location" => "Location permission was denied. GPS commands won't work until it's granted in Settings.",
                        "BackgroundLocation" => "\"Allow all the time\" location wasn't granted. Start/Stop still works, but the service won't be able to restart itself automatically after a reboot or app update. You can grant this later in Settings → Apps → Finder → Permissions → Location → Allow all the time.",
                        _ => "Notification permission was denied. The background service can still run, but its status notification won't be visible."
                    };

                    await Application.Current.MainPage.DisplayAlert("Permission needed", detail, "OK");
                }
            });
        }

        public string BotToken
        {
            get => _botToken;
            set => SetProperty(ref _botToken, value);
        }

        public string ChatId
        {
            get => _chatId;
            set => SetProperty(ref _chatId, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        public Color StatusColor
        {
            get => _statusColor;
            private set => SetProperty(ref _statusColor, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                    RaiseCommandStates();
            }
        }

        public RelayCommand SaveCommand { get; }
        public RelayCommand StartServiceCommand { get; }
        public RelayCommand StopServiceCommand { get; }

        public async Task LoadAsync()
        {
            var settings = await _settingsService.LoadAsync();
            BotToken = settings.BotToken;
            ChatId = settings.ChatId;
            RefreshStatus();
        }

        public void OnAppearing()
        {
            RefreshStatus();
            _statusTimer = new System.Threading.Timer(
                _ => MainThread.BeginInvokeOnMainThread(RefreshStatus),
                null,
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(3));
        }

        public void OnDisappearing()
        {
            _statusTimer?.Dispose();
            _statusTimer = null;
        }

        private async Task SaveAsync()
        {
            IsBusy = true;
            try
            {
                await _settingsService.SaveAsync(new AppSettings
                {
                    BotToken = BotToken,
                    ChatId = ChatId
                });
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task StartServiceAsync()
        {
            IsBusy = true;
            try
            {
                await _locationService.StartTracking();
            }
            catch (Exception ex)
            {
                if (Application.Current?.MainPage is not null)
                {
                    await Application.Current.MainPage.DisplayAlert("Couldn't start service", ex.Message, "OK");
                }
            }
            finally
            {
                IsBusy = false;
                RefreshStatus();
            }
        }

        private async Task StopServiceAsync()
        {
            IsBusy = true;
            try
            {
                await _locationService.StopTracking();
            }
            catch (Exception ex)
            {
                if (Application.Current?.MainPage is not null)
                {
                    await Application.Current.MainPage.DisplayAlert("Couldn't stop service", ex.Message, "OK");
                }
            }
            finally
            {
                IsBusy = false;
                RefreshStatus();
            }
        }

        private void RefreshStatus()
        {
            var running = _locationService.IsRunning;
            StatusText = running ? "Running" : "Stopped";
            StatusColor = running ? Colors.Green : Colors.Gray;
            RaiseCommandStates();
        }

        private void RaiseCommandStates()
        {
            StartServiceCommand.RaiseCanExecuteChanged();
            StopServiceCommand.RaiseCanExecuteChanged();
        }

        public void Dispose()
        {
            OnDisappearing();
            WeakReferenceMessenger.Default.Unregister<PermissionDeniedMessage>(this);
        }
    }
}