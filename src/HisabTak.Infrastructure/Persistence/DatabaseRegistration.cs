using HisabTak.Application.Security;
using HisabTak.Domain.Accounting;
using HisabTak.Application.Accounting;
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
        services.AddScoped<IFiscalPeriodService, FiscalPeriodService>();
        services.AddScoped<IChartOfAccountsService, ChartOfAccountsService>();
        services.AddScoped<JournalService>();
        services.AddScoped<IJournalService>(provider => provider.GetRequiredService<JournalService>());
        foreach (var (code, kind) in new[] { ("JOURNAL_VOUCHER", JournalKind.Standard), ("JOURNAL_OPENING", JournalKind.Opening),
            ("RECEIPT_VOUCHER", JournalKind.Standard), ("PAYMENT_VOUCHER", JournalKind.Standard) })
            services.AddScoped<IJournalDocumentStrategy>(provider => new JournalDocumentStrategy(provider.GetRequiredService<HisabTakDbContext>(), code, kind));
        services.AddScoped<IJournalDocumentService, JournalDocumentService>();
        services.AddScoped<IAccountingWorkspaceService, AccountingWorkspaceService>();
        services.AddScoped<HisabTak.Application.Inventory.IInventorySetupService, InventorySetupService>();
        services.AddScoped<IDatabaseBackupService, SqlServerBackupService>();
        return services;
    }
}
