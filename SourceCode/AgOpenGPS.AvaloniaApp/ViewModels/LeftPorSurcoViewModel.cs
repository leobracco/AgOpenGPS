using System.Collections.ObjectModel;
using AgOpenGPS.AvaloniaApp.ViewModels.Items;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenGPS.AvaloniaApp.ViewModels
{
    public partial class LeftPorSurcoViewModel : ViewModelBase
    {
        [ObservableProperty]
        private string _title = "POR SURCO";

        public ObservableCollection<RowStatusItemViewModel> Rows { get; } = new();

        public LeftPorSurcoViewModel() { }
    }
}
