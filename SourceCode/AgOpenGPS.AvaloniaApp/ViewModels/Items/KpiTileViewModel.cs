using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenGPS.AvaloniaApp.ViewModels.Items
{
    public partial class KpiTileViewModel : ViewModelBase
    {
        [ObservableProperty]
        private string _label = string.Empty;

        [ObservableProperty]
        private string _value = string.Empty;

        [ObservableProperty]
        private string _unit = string.Empty;

        [ObservableProperty]
        private string _badge = string.Empty;

        public KpiTileViewModel() { }

        public KpiTileViewModel(string label, string unit)
        {
            _label = label;
            _unit = unit;
        }
    }
}
