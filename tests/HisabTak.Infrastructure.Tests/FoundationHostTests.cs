using HisabTak.Application;
using HisabTak.Infrastructure.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HisabTak.Infrastructure.Tests;

public sealed class FoundationHostTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hisabtak-foundation", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Host_starts_resolves_application_and_flushes_diagnostic_file_on_disposal()
    {
        var marker = "foundation-" + Guid.NewGuid().ToString("N");
        using (var host = CreateBuilder().Build())
        {
            await host.StartAsync();
            Assert.NotNull(host.Services.GetRequiredService<ApplicationIdentity>());
            var options = host.Services.GetRequiredService<IOptions<DiagnosticsOptions>>().Value;
            Assert.Equal(_directory, DiagnosticPaths.ResolveDirectory(options));
            host.Services.GetRequiredService<ILogger<FoundationHostTests>>()
                .LogInformation("Foundation verification {Marker}", marker);
            await host.StopAsync();
        }

        var files = Directory.GetFiles(_directory, "hisabtak-*.log");
        Assert.NotEmpty(files);
        Assert.Contains(files, file => File.ReadAllText(file).Contains(marker, StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_directory, ".write-check-*"));
    }

    [Theory]
    [InlineData("Diagnostics:Directory", "relative/logs")]
    [InlineData("Diagnostics:RetainedFileCountLimit", "0")]
    [InlineData("Diagnostics:RetainedFileCountLimit", "366")]
    public async Task Invalid_configuration_prevents_startup(string key, string value)
    {
        var builder = CreateBuilder(new Dictionary<string, string?> { [key] = value });
        await Assert.ThrowsAsync<OptionsValidationException>(async () =>
        {
            using var host = builder.Build();
            await host.StartAsync();
        });
    }

    [Fact]
    public void Default_diagnostics_location_is_user_data_not_application_directory()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.False(string.IsNullOrWhiteSpace(localData));
        var path = DiagnosticPaths.ResolveDirectory(new DiagnosticsOptions());
        Assert.Equal(Path.Combine(localData, "HisabTak", "Logs"), path);
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.False(path.StartsWith(AppContext.BaseDirectory, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unwritable_diagnostics_target_prevents_startup()
    {
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "occupied");
        await File.WriteAllTextAsync(file, "not a directory");
        var builder = CreateBuilder(new Dictionary<string, string?> { ["Diagnostics:Directory"] = file });
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            using var host = builder.Build();
            await host.StartAsync();
        });
    }

    private HostApplicationBuilder CreateBuilder(Dictionary<string, string?>? overrides = null)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Diagnostics:Directory"] = _directory,
            ["Diagnostics:RetainedFileCountLimit"] = "3"
        });
        if (overrides is not null)
        {
            builder.Configuration.AddInMemoryCollection(overrides);
        }

        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        return builder;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
