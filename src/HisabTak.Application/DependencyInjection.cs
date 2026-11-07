using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HisabTak.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var version = typeof(DependencyInjection).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        services.TryAddSingleton(new ApplicationIdentity("HisabTak", version));
        return services;
    }
}
