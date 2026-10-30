using HisabTak.Application.Security;
using HisabTak.Application.Setup;
using HisabTak.Application.Backup;
using HisabTak.Infrastructure.Backup;
using HisabTak.Infrastructure.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HisabTak.Infrastructure.Persistence;

public static class DatabaseRegistration
{
    public static IServiceCollection AddSqlServerFoundation(this IServiceCollection services, string connectionString)
    {
        var connection = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(connection.DataSource) || string.IsNullOrWhiteSpace(connection.InitialCatalog)
            || new[] { "master", "model", "msdb", "tempdb" }.Contains(connection.InitialCatalog, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("An explicit application database is required.", nameof(connectionString));
        if (connection.TrustServerCertificate || connection.Encrypt == SqlConnectionEncryptOption.Optional)
            throw new ArgumentException("Encrypted SQL connections with certificate verification are required.", nameof(connectionString));

        // Each provider owns one database. There is no mutable global tenant selector.
        services.AddDbContext<HisabTakDbContext>(options => options.UseSqlServer(connection.ConnectionString,
            sql => sql.UseCompatibilityLevel(160)));
        services.AddScoped<ReferenceDataSeeder>();
        services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
        services.AddScoped<IAuthorizationService, AuthorizationService>();
        services.AddScoped<IInitialOwnerService, InitialOwnerService>();
        services.AddScoped<IAuthenticationService, AuthenticationService>();
        services.AddScoped<ICompanySetupService, CompanySetupService>();
        services.AddScoped<IBranchSetupService, BranchSetupService>();
        services.AddScoped<IDatabaseBackupService, SqlServerBackupService>();
        return services;
    }
}
