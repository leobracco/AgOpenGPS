using System;
using System.Threading.Tasks;
using AgOpenGPS.AvaloniaApp.Services.Seeding;
using AgOpenGPS.AvaloniaApp.ViewModels;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace AgOpenGPS.AvaloniaApp.Views
{
    public partial class MainWindow : Window
    {
        private SeedingRuntimeHost? _runtime;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = App.Services.GetRequiredService<MainWindowViewModel>();
            // Touch the adapter so it subscribes to the monitor before snapshots fire.
            _ = App.Services.GetRequiredService<SeedingViewModelAdapter>();
            _runtime = App.Services.GetRequiredService<SeedingRuntimeHost>();

            Opened += OnOpened;
            Closing += OnClosing;
        }

        private async void OnOpened(object? sender, EventArgs e)
        {
            try
            {
                if (_runtime is not null) await _runtime.StartAsync();
            }
            catch
            {
                // Surface in logging in a future iteration; non-fatal for UI startup.
            }
        }

        private async void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            if (_runtime is null) return;
            var local = _runtime;
            _runtime = null;
            try { await local.StopAsync(); } catch { }
        }
    }
}
