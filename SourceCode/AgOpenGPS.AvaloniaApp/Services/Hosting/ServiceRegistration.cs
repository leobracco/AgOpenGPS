using System;
using System.Collections.Generic;
using AgOpenGPS.AvaloniaApp.Services.Branding;
using AgOpenGPS.AvaloniaApp.Services.Seeding;
using AgOpenGPS.AvaloniaApp.ViewModels;
using AgOpenGPS.AvaloniaApp.Views;
using AgOpenGPS.Seeding;
using AgOpenGPS.Seeding.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenGPS.AvaloniaApp.Services.Hosting
{
    public static class ServiceRegistration
    {
        public static IServiceProvider Build()
        {
            var services = new ServiceCollection();

            services.AddLogging(b => b.AddDebug());

            services.AddSingleton<IBrandingService, BrandingService>();

            services.AddSingleton<SeedingTargets>(_ =>
                new SeedingTargets(targetSeedsPerHectare: 80000, rowSpacingCm: 52.5, boomWidthMeters: 12.6));

            services.AddSingleton<SeedingMonitor>(sp =>
            {
                var targets = sp.GetRequiredService<SeedingTargets>();
                const int rowCount = 24;
                var rows = new List<PlanterRow>(rowCount);
                double offset = -((rowCount - 1) * targets.RowSpacingCm / 200.0);
                for (int i = 0; i < rowCount; i++)
                {
                    int sectionIndex = i / (rowCount / 8);
                    rows.Add(new PlanterRow(i, offset + i * (targets.RowSpacingCm / 100.0), sectionIndex, targets.RowSpacingCm));
                }
                return new SeedingMonitor(rows, targets);
            });

            services.AddSingleton<ISeedingDataSource>(sp =>
            {
                var targets = sp.GetRequiredService<SeedingTargets>();
                return new SimulatedSeedingDataSource(rowCount: 24, targets: targets);
            });

            services.AddSingleton<SeedingRuntimeHost>();
            services.AddSingleton<SeedingViewModelAdapter>();

            services.AddSingleton<HeaderBarViewModel>();
            services.AddSingleton<LeftPorSurcoViewModel>();
            services.AddSingleton<CenterViewportViewModel>();
            services.AddSingleton<RightControlViewModel>();
            services.AddSingleton<BottomAlertsViewModel>();
            services.AddSingleton<BottomToolbarViewModel>();
            services.AddSingleton<DashboardViewModel>();
            services.AddSingleton<MainWindowViewModel>();

            services.AddSingleton<MainWindow>();

            return services.BuildServiceProvider();
        }
    }
}
