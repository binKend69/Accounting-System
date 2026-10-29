using HisabTak.Application.Backup;
using HisabTak.Application.Security;
using HisabTak.Application.Setup;
using HisabTak.Domain.Security;
using HisabTak.Domain.Setup;
using HisabTak.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HisabTak.Infrastructure.Tests;

public sealed class SqlServerIntegrationTests
{
    [SqlServerFact]
    public async Task Migrations_and_seed_are_repeatable_and_preserve_customer_edits()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        await using var database = test.Context();
        var seed = new ReferenceDataSeeder(database);
        await seed.SeedAsync();
        var currency = await database.Currencies.SingleAsync(x => x.Code == "YER");
        currency.Rename("العملة المحلية المخصصة");
        await database.SaveChangesAsync();
        await seed.SeedAsync();
        await database.Database.MigrateAsync();

        Assert.Equal(3, await database.Currencies.CountAsync());
        Assert.Equal("العملة المحلية المخصصة", (await database.Currencies.SingleAsync(x => x.Code == "YER")).NameAr);
        Assert.All(await database.Currencies.ToListAsync(), x => Assert.Null(x.ExchangeRate));
        Assert.False(await database.Currencies.AnyAsync(x => x.IsBaseCurrency));
        Assert.Equal(20, await database.DocumentTypes.CountAsync());
        Assert.Equal(4, await database.Roles.CountAsync());
        Assert.Empty(await database.Users.ToListAsync());
        Assert.Empty(await database.Companies.ToListAsync());
        Assert.Empty(await database.Accounts.ToListAsync());
        Assert.Equal(2, (await database.Database.GetAppliedMigrationsAsync()).Count());
    }

    [SqlServerFact]
    public async Task Migration_upgrade_preserves_existing_data_and_protects_audit_from_raw_sql()
    {
        await using var test = await SqlTestDatabase.CreateAsync("20261029172256_InitialReferenceData");
        await using var database = test.Context();
        database.Units.Add(new Unit("وحدة قبل الترقية"));
        database.AuditLogs.Add(new AuditLog(null, "TEST_CREATED", "Setup", null, "test"));
        await database.SaveChangesAsync();
        await database.Database.MigrateAsync();
        Assert.True(await database.Units.AnyAsync(x => x.NameAr == "وحدة قبل الترقية"));
        await Assert.ThrowsAsync<SqlException>(() => database.Database.ExecuteSqlRawAsync("DELETE FROM dbo.AuditLogs"));
        Assert.Single(await database.AuditLogs.AsNoTracking().ToListAsync());
    }

    [SqlServerFact]
    public async Task Sql_constraints_reject_duplicates_negative_rates_and_a_second_local_branch()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        await using var database = test.Context();
        await new ReferenceDataSeeder(database).SeedAsync();
        await Assert.ThrowsAsync<SqlException>(() => database.Database.ExecuteSqlRawAsync("UPDATE dbo.Currencies SET ExchangeRate = -1 WHERE Code = 'YER'"));
        await Assert.ThrowsAsync<SqlException>(() => database.Database.ExecuteSqlRawAsync("INSERT dbo.Branches (BranchId,NameAr,IsActive,IsDeleted) VALUES (2,N'فرع ثان',1,0)"));
        database.Currencies.Add(new Currency("YER", "مكرر", "ر"));
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        await using var verify = test.Context();
        Assert.Equal(3, await verify.Currencies.CountAsync());
    }

    [SqlServerFact]
    public async Task Soft_delete_filter_and_rowversion_protect_reference_changes()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        await using (var setup = test.Context()) await new ReferenceDataSeeder(setup).SeedAsync();
        await using var first = test.Context();
        await using var second = test.Context();
        var one = await first.Units.FirstAsync();
        var two = await second.Units.SingleAsync(x => x.Id == one.Id);
        one.Rename("تعديل أول");
        await first.SaveChangesAsync();
        two.Rename("تعديل متعارض");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        one.MarkDeleted();
        await first.SaveChangesAsync();
        await using var verify = test.Context();
        Assert.False(await verify.Units.AnyAsync(x => x.Id == one.Id));
        Assert.Equal("تعديل أول", (await verify.Units.IgnoreQueryFilters().SingleAsync(x => x.Id == one.Id)).NameAr);
    }

    [SqlServerFact]
    public async Task Initial_owner_is_created_once_and_passwords_never_enter_audit()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        await using (var database = test.Context()) await new ReferenceDataSeeder(database).SeedAsync();
        var password = "Test-" + Guid.NewGuid().ToString("N");
        async Task<bool> Attempt(string username)
        {
            await using var services = test.Services();
            await using var scope = services.CreateAsyncScope();
            try { await scope.ServiceProvider.GetRequiredService<IInitialOwnerService>().CreateAsync(username, password); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Attempt("first"), Attempt("second"));
        Assert.Single(results, x => x);
        await using var verify = test.Context();
        var owner = await verify.Users.SingleAsync();
        Assert.NotEqual(password, owner.PasswordHash);
        var audit = await verify.AuditLogs.SingleAsync(x => x.OperationType == "OWNER_INITIALIZED");
        Assert.Null(audit.Details);
        Assert.Equal(owner.Id, audit.UserId);
        Assert.Single(await verify.UserRoles.ToListAsync());
    }

    [SqlServerFact]
    public async Task Permission_checks_fail_closed_and_inactive_users_cannot_authenticate()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        await using (var database = test.Context()) await new ReferenceDataSeeder(database).SeedAsync();
        await using var services = test.Services();
        await using var scope = services.CreateAsyncScope();
        var passwords = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var databaseContext = scope.ServiceProvider.GetRequiredService<HisabTakDbContext>();
        var password = "Test-" + Guid.NewGuid().ToString("N");
        var user = new User("cashier", passwords.Hash(password));
        databaseContext.Users.Add(user);
        await databaseContext.SaveChangesAsync();
        var role = await databaseContext.Roles.SingleAsync(x => x.Code == "CASHIER");
        databaseContext.UserRoles.Add(new UserRole(user.Id, role.Id));
        await databaseContext.SaveChangesAsync();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authorization.DemandAsync(user.Id, PermissionCodes.SetupManage));
        var permission = await databaseContext.Permissions.SingleAsync(x => x.Code == PermissionCodes.SetupManage);
        databaseContext.UserPermissions.Add(new UserPermission(user.Id, permission.Id));
        await databaseContext.SaveChangesAsync();
        await authorization.DemandAsync(user.Id, PermissionCodes.SetupManage);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authorization.DemandAsync(user.Id, PermissionCodes.BackupRestore));
        var authentication = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
        Assert.NotNull(await authentication.AuthenticateAsync("cashier", password));
        Assert.Null(await authentication.AuthenticateAsync("cashier", "wrong-password"));
        user.SetActive(false);
        await databaseContext.SaveChangesAsync();
        Assert.Null(await authentication.AuthenticateAsync("cashier", password));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authorization.DemandAsync(user.Id, PermissionCodes.SetupManage));
    }

    [SqlServerFact]
    public async Task Company_initialization_rolls_back_a_partial_branch_and_enforces_one_company()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var owner = await InitializeOwnerAsync(test);
        await using (var services = test.Services())
        await using (var scope = services.CreateAsyncScope())
        {
            var setup = scope.ServiceProvider.GetRequiredService<ICompanySetupService>();
            await Assert.ThrowsAsync<ArgumentException>(() => setup.InitializeAsync(owner, "", "فرع تجريبي"));
        }
        await using (var verify = test.Context())
        {
            Assert.False(await verify.Branches.AnyAsync());
            Assert.False(await verify.Companies.AnyAsync());
            Assert.False(await verify.AuditLogs.AnyAsync(x => x.OperationType == "COMPANY_INITIALIZED"));
        }
        await using var provider = test.Services();
        await using var validScope = provider.CreateAsyncScope();
        var valid = validScope.ServiceProvider.GetRequiredService<ICompanySetupService>();
        await valid.InitializeAsync(owner, "تاجر مستقل", "الفرع المحلي");
        await Assert.ThrowsAsync<InvalidOperationException>(() => valid.InitializeAsync(owner, "تاجر آخر", "فرع آخر"));
    }

    [SqlServerFact]
    public async Task Separate_databases_never_share_company_or_user_state()
    {
        await using var first = await SqlTestDatabase.CreateAsync();
        await using var second = await SqlTestDatabase.CreateAsync();
        await using var one = first.Context();
        await using var two = second.Context();
        one.Units.Add(new Unit("خاص بالتاجر الأول"));
        await one.SaveChangesAsync();
        Assert.False(await two.Units.AnyAsync());
        Assert.NotEqual(first.Name, second.Name);
        Assert.NotEqual(one.Database.GetConnectionString(), two.Database.GetConnectionString());
    }

    [SqlServerFact(recovery: true)]
    public async Task Native_backup_restores_to_a_new_database_without_replacing_original_or_existing_files()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var owner = await InitializeOwnerAsync(test);
        await using (var data = test.Context())
        {
            data.Units.Add(new Unit("قبل النسخ"));
            await data.SaveChangesAsync();
        }
        var directory = Environment.GetEnvironmentVariable("HISABTAK_TEST_SQL_DATA_DIRECTORY")!;
        var separator = directory.Contains('\\') ? '\\' : '/';
        var path = directory.TrimEnd('/', '\\') + separator + test.Name + ".bak";
        var target = test.ReserveRestoreTarget();
        async Task<bool> CreateCompetingBackup()
        {
            await using var provider = test.Services();
            await using var child = provider.CreateAsyncScope();
            try
            {
                await child.ServiceProvider.GetRequiredService<IDatabaseBackupService>().CreateAsync(owner, path);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }
        Assert.Single(await Task.WhenAll(CreateCompetingBackup(), CreateCompetingBackup()), success => success);
        await using (var services = test.Services())
        await using (var scope = services.CreateAsyncScope())
        {
            var backup = scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => backup.CreateAsync(owner, path));
            await backup.RestoreToNewDatabaseAsync(owner, path, target, directory);
            await Assert.ThrowsAsync<InvalidOperationException>(() => backup.RestoreToNewDatabaseAsync(owner, path, target, directory));
            await Assert.ThrowsAsync<ArgumentException>(() => backup.RestoreToNewDatabaseAsync(owner, path, test.Name, directory));
        }
        await using var restored = test.Context(target);
        Assert.True(await restored.Units.AnyAsync(x => x.NameAr == "قبل النسخ"));
        Assert.Equal(3, await restored.Currencies.CountAsync());
        Assert.Single(await restored.Users.ToListAsync());
        await using var original = test.Context();
        Assert.NotNull(await original.CustomSettings.FindAsync("Backup.LastSuccessfulUtc"));
        Assert.True(await original.AuditLogs.AnyAsync(x => x.OperationType == "BACKUP_CREATED"));
    }

    private static async Task<int> InitializeOwnerAsync(SqlTestDatabase test)
    {
        await using (var setup = test.Context()) await new ReferenceDataSeeder(setup).SeedAsync();
        await using var services = test.Services();
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IInitialOwnerService>().CreateAsync("owner", "Test-" + Guid.NewGuid().ToString("N"));
    }
}
