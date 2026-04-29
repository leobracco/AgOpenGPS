using System.Collections.ObjectModel;
using AgOpenGPS.AvaloniaApp.ViewModels.Items;

namespace AgOpenGPS.AvaloniaApp.ViewModels
{
    public partial class BottomAlertsViewModel : ViewModelBase
    {
        public ObservableCollection<AlertBannerViewModel> Alerts { get; } = new();
    }
}
