using System.Globalization;
using System.Windows;
using HisabTak.Application;
using HisabTak.Infrastructure;
using HisabTak.Infrastructure.Persistence;
using HisabTak.WPF.Views;
using HisabTak.WPF.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HisabTak.WPF;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("ar-YE");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("ar-YE");

        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = e.Args,
                DisableDefaults = true,
                ContentRootPath = AppContext.BaseDirectory
            });
            // No configuration watchers, development secrets, or external services.
            builder.Configuration.Sources.Clear();
            builder.Configuration
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables("HISABTAK_");

            builder.Logging.ClearProviders();
            builder.Services.AddApplication();
            builder.Services.AddInfrastructure(builder.Configuration);
            var connection = builder.Configuration["Database:ConnectionString"];
            if (!string.IsNullOrWhiteSpace(connection)) builder.Services.AddSqlServerFoundation(connection);
            builder.Services.AddTransient<MainWindowViewModel>();
            builder.Services.AddTransient<MainWindow>();
            _host = builder.Build();
            await _host.StartAsync();

            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("Application started.");
            MainWindow = _host.Services.GetRequiredService<MainWindow>();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            // Do not print configuration or exception messages that may contain secrets.
            System.Diagnostics.Debug.WriteLine($"Startup failed ({exception.GetType().Name}).");
            MessageBox.Show("تعذر تشغيل البرنامج. راجع إعداداته وصلاحية الوصول إلى مجلد السجلات.",
                "تعذر التشغيل", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                // There are no UI-dependent hosted services; do not capture this dispatcher.
                Task.Run(() => _host.StopAsync(timeout.Token)).GetAwaiter().GetResult();
            }
        }
        catch (Exception exception)
        {
            // Disposal still runs even when a hosted service fails to stop.
            System.Diagnostics.Debug.WriteLine($"Shutdown failed ({exception.GetType().Name}).");
        }
        finally
        {
            _host?.Dispose();
            base.OnExit(e);
        }
    }
}
