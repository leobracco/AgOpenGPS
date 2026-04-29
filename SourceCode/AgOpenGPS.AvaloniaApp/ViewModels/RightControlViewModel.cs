using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenGPS.AvaloniaApp.ViewModels
{
    public partial class RightControlViewModel : ViewModelBase
    {
        [ObservableProperty]
        private string _title = "CONTROL";

        [ObservableProperty]
        private double _targetSeedsPerMeter = 5.2;

        [ObservableProperty]
        private double _variationPercent;

        [ObservableProperty]
        private string _doserStatus = "OK";

        [ObservableProperty]
        private string _currentLine = "1 L";

        [ObservableProperty]
        private double _overlapCm = 2.3;
    }
}
