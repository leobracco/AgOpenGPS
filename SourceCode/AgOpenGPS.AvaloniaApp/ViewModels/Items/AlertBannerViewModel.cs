using AgOpenGPS.Seeding;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenGPS.AvaloniaApp.ViewModels.Items
{
    public partial class AlertBannerViewModel : ViewModelBase
    {
        [ObservableProperty]
        private string _title = string.Empty;

        [ObservableProperty]
        private string _detail = string.Empty;

        [ObservableProperty]
        private RowAlertSeverity _severity;

        public AlertBannerViewModel() { }

        public AlertBannerViewModel(string title, string detail, RowAlertSeverity severity)
        {
            _title = title;
            _detail = detail;
            _severity = severity;
        }
    }
}
