using AgOpenGPS.Seeding;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenGPS.AvaloniaApp.ViewModels.Items
{
    public partial class RowStatusItemViewModel : ViewModelBase
    {
        [ObservableProperty]
        private int _rowNumber;

        [ObservableProperty]
        private double _seedsPerMeter;

        [ObservableProperty]
        private RowAlertSeverity _severity;

        public RowStatusItemViewModel() { }

        public RowStatusItemViewModel(int rowNumber)
        {
            _rowNumber = rowNumber;
            _severity = RowAlertSeverity.Ok;
        }
    }
}
