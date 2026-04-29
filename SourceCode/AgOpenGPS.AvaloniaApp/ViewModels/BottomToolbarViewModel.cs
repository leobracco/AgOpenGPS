using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgOpenGPS.AvaloniaApp.ViewModels
{
    public partial class BottomToolbarViewModel : ViewModelBase
    {
        [ObservableProperty]
        private bool _isAutoSteerOn;

        [ObservableProperty]
        private bool _isSectionMasterAuto = true;

        [ObservableProperty]
        private bool _isPlaying;

        [RelayCommand]
        private void ToggleAutoSteer() => IsAutoSteerOn = !IsAutoSteerOn;

        [RelayCommand]
        private void ToggleMasterAuto() => IsSectionMasterAuto = !IsSectionMasterAuto;

        [RelayCommand]
        private void TogglePlay() => IsPlaying = !IsPlaying;

        [RelayCommand]
        private void NextLine() { }

        [RelayCommand]
        private void PreviousLine() { }
    }
}
