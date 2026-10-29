using HisabTak.Infrastructure.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog;

namespace HisabTak.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DiagnosticsOptions>()
            .Bind(configuration.GetSection(DiagnosticsOptions.SectionName))
            .Validate(o => string.IsNullOrWhiteSpace(o.Directory) || Path.IsPathFullyQualified(o.Directory),
                "Diagnostics:Directory must be an absolute path when specified.")
            .Validate(o => o.RetainedFileCountLimit is >= 1 and <= 365,
                "Diagnostics:RetainedFileCountLimit must be between 1 and 365.")
            .ValidateOnStart();

        services.AddSerilog((provider, logger) =>
        {
            var options = provider.GetRequiredService<IOptions<DiagnosticsOptions>>().Value;
            var directory = DiagnosticPaths.ResolveDirectory(options);
            // Fail startup explicitly if diagnostics cannot be written.
            Directory.CreateDirectory(directory);
            using (new FileStream(Path.Combine(directory, $".write-check-{Guid.NewGuid():N}"), FileMode.CreateNew,
                FileAccess.Write, FileShare.ReadWrite, 1, FileOptions.DeleteOnClose))
            {
            }

            logger.MinimumLevel.Information()
                .Enrich.FromLogContext()
                .WriteTo.File(Path.Combine(directory, "hisabtak-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: options.RetainedFileCountLimit,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    shared: true);
        }, preserveStaticLogger: true);

        return services;
    }
}
