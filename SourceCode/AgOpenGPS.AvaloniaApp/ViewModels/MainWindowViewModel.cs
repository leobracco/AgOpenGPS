namespace AgOpenGPS.AvaloniaApp.ViewModels
{
    public sealed class MainWindowViewModel : ViewModelBase
    {
        public DashboardViewModel Dashboard { get; }

        public MainWindowViewModel(DashboardViewModel dashboard)
        {
            Dashboard = dashboard;
        }
    }
}
