using System;
using AgOpenGPS.AvaloniaApp.ViewModels.Items;
using AgOpenGPS.Seeding;
using Avalonia.Controls;

namespace AgOpenGPS.AvaloniaApp.Views.Items
{
    public partial class AlertBannerView : UserControl
    {
        public AlertBannerView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (DataContext is AlertBannerViewModel vm)
            {
                ApplySeverity(vm.Severity);
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(AlertBannerViewModel.Severity))
                        ApplySeverity(vm.Severity);
                };
            }
        }

        private void ApplySeverity(RowAlertSeverity severity)
        {
            var border = this.FindControl<Border>("Root");
            if (border is null) return;
            border.Classes.Remove("warning");
            border.Classes.Remove("critical");
            if (severity == RowAlertSeverity.Warning) border.Classes.Add("warning");
            else if (severity == RowAlertSeverity.Critical) border.Classes.Add("critical");
        }
    }
}
