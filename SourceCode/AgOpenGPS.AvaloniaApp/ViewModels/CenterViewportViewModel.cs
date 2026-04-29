using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenGPS.AvaloniaApp.ViewModels
{
    public partial class CenterViewportViewModel : ViewModelBase
    {
        [ObservableProperty]
        private string _placeholder = "Vista 3D del campo (placeholder — viewport OpenGL en Fase 5)";
    }
}
