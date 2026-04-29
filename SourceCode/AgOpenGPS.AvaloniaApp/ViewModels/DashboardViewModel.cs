namespace AgOpenGPS.AvaloniaApp.ViewModels
{
    public sealed class DashboardViewModel : ViewModelBase
    {
        public HeaderBarViewModel Header { get; }
        public LeftPorSurcoViewModel LeftPorSurco { get; }
        public CenterViewportViewModel CenterViewport { get; }
        public RightControlViewModel RightControl { get; }
        public BottomAlertsViewModel BottomAlerts { get; }
        public BottomToolbarViewModel BottomToolbar { get; }

        public DashboardViewModel(
            HeaderBarViewModel header,
            LeftPorSurcoViewModel leftPorSurco,
            CenterViewportViewModel centerViewport,
            RightControlViewModel rightControl,
            BottomAlertsViewModel bottomAlerts,
            BottomToolbarViewModel bottomToolbar)
        {
            Header = header;
            LeftPorSurco = leftPorSurco;
            CenterViewport = centerViewport;
            RightControl = rightControl;
            BottomAlerts = bottomAlerts;
            BottomToolbar = bottomToolbar;
        }
    }
}
