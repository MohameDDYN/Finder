using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using Finder.Models;
using Xamarin.Forms;

namespace Finder.ViewModels
{
    /// <summary>
    /// ViewModel for LocationHistoryPage — shows list of recorded location files.
    /// </summary>
    public class LocationHistoryViewModel : BaseViewModel
    {
        private readonly string _dataDirectory;

        // ── Events ──────────────────────────────────────────────────────────
        public event EventHandler<string> ShowAlert;
        public event EventHandler<LocationFileInfo> RequestReport;

        public LocationHistoryViewModel()
        {
            Title = "Location History";
            _dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                "LocationData");

            Files = new ObservableCollection<LocationFileInfo>();
            RefreshCommand = new Command(async () => await LoadFilesAsync());
            GetReportCommand = new Command<LocationFileInfo>(OnGetReport);
        }

        // ── Bindable properties ────────────────────────────────────────────

        public ObservableCollection<LocationFileInfo> Files { get; }

        private bool _hasFiles;
        public bool HasFiles
        {
            get => _hasFiles;
            set => SetProperty(ref _hasFiles, value);
        }

        private string _emptyMessage = "No location history found.\nStart tracking to record data.";
        public string EmptyMessage
        {
            get => _emptyMessage;
            set => SetProperty(ref _emptyMessage, value);
        }

        // ── Commands ───────────────────────────────────────────────────────
        public ICommand RefreshCommand { get; }
        public ICommand GetReportCommand { get; }

        // ── Data loading ───────────────────────────────────────────────────

        public async Task LoadFilesAsync()
        {
            try
            {
                IsBusy = true;
                Files.Clear();

                if (!Directory.Exists(_dataDirectory))
                {
                    HasFiles = false;
                    return;
                }

                var files = Directory.GetFiles(_dataDirectory, "locations_*.json");
                Array.Sort(files, (a, b) => string.Compare(b, a, StringComparison.Ordinal)); // newest first

                foreach (var file in files)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName.StartsWith("locations_") &&
                        DateTime.TryParseExact(
                            fileName.Substring(10), "yyyy-MM-dd",
                            null,
                            System.Globalization.DateTimeStyles.None,
                            out DateTime fileDate))
                    {
                        bool isToday = fileDate.Date == DateTime.Today;
                        bool isYesterday = fileDate.Date == DateTime.Today.AddDays(-1);

                        string displayName = isToday ? $"Today — {fileDate:MMM dd, yyyy}"
                                           : isYesterday ? $"Yesterday — {fileDate:MMM dd, yyyy}"
                                           : fileDate.ToString("MMM dd, yyyy");

                        Files.Add(new LocationFileInfo
                        {
                            FileName = Path.GetFileName(file),
                            Date = fileDate,
                            DisplayName = displayName,
                            DayOfWeek = fileDate.DayOfWeek.ToString(),
                            FilePath = file
                        });
                    }
                }

                HasFiles = Files.Count > 0;

                if (!HasFiles)
                    EmptyMessage = "No location history yet.\nStart tracking to begin recording data.";
            }
            catch (Exception ex)
            {
                ShowAlert?.Invoke(this, $"Could not load history: {ex.Message}");
                HasFiles = false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void OnGetReport(LocationFileInfo fileInfo)
        {
            if (fileInfo == null) return;
            RequestReport?.Invoke(this, fileInfo);
        }
    }
}