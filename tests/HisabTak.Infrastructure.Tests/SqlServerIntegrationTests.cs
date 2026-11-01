using HisabTak.Application.Backup;
using HisabTak.Application.Accounting;
using HisabTak.Application.Security;
using HisabTak.Application.Setup;
using HisabTak.Domain.Security;
using HisabTak.Domain.Accounting;
using HisabTak.Domain.Setup;
using HisabTak.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace HisabTak.Infrastructure.Tests;

[Trait("Category", "SqlServer")]
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
        Assert.Equal(10, (await database.Database.GetAppliedMigrationsAsync()).Count());
    }

    [SqlServerFact]
    public async Task Migration_upgrade_preserves_existing_data_and_protects_audit_from_raw_sql()
    {
        await using var test = await SqlTestDatabase.CreateAsync("20261029172256_InitialReferenceData");
        await using var database = test.Context();
        database.Units.Add(new Unit("وحدة قبل الترقية"));
        database.FiscalPeriods.Add(new FiscalPeriod("فترة قبل الترقية", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        database.AuditLogs.Add(new AuditLog(null, "TEST_CREATED", "Setup", null, "test"));
        await database.SaveChangesAsync();
        await database.Database.ExecuteSqlRawAsync("INSERT dbo.Branches (BranchId,NameAr,IsActive,IsDeleted) VALUES (1,N'الفرع السابق',1,0)");
        await database.Database.ExecuteSqlRawAsync("INSERT dbo.CompanyInfo (CompanyInfoId,BranchId,NameAr,IsActive,IsDeleted,UseImageInsteadOfText) VALUES (1,1,N'التاجر السابق',1,0,0)");
        await database.Database.MigrateAsync();
        Assert.True(await database.Units.AnyAsync(x => x.NameAr == "وحدة قبل الترقية"));
        Assert.False((await database.FiscalPeriods.SingleAsync()).IsClosed);
        Assert.Equal("التاجر السابق", (await database.Companies.SingleAsync()).NameAr);
        var branch = new Branch("فرع بعد الترقية");
        database.Branches.Add(branch);
        await database.SaveChangesAsync();
        Assert.True(branch.Id > 1);
        Assert.Equal(1, (await database.Companies.SingleAsync()).BranchId);
        Assert.Equal("الفرع السابق", (await database.Branches.SingleAsync(x => x.Id == 1)).NameAr);
        await Assert.ThrowsAsync<SqlException>(() => database.Database.ExecuteSqlRawAsync("DELETE FROM dbo.AuditLogs"));
        Assert.Single(await database.AuditLogs.AsNoTracking().ToListAsync());
    }

    [SqlServerFact]
    public async Task Sql_constraints_reject_duplicates_negative_rates_and_orphan_branches()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        await using var database = test.Context();
        await new ReferenceDataSeeder(database).SeedAsync();
        await Assert.ThrowsAsync<SqlException>(() => database.Database.ExecuteSqlRawAsync("UPDATE dbo.Currencies SET ExchangeRate = -1 WHERE Code = 'YER'"));
        await Assert.ThrowsAsync<SqlException>(() => database.Database.ExecuteSqlRawAsync("INSERT dbo.Warehouses (BranchId,NameAr,IsActive,IsDeleted) VALUES (999999,N'مستودع بلا فرع',1,0)"));
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
        await using var result = test.Context();
        var branch = await result.Branches.SingleAsync();
        Assert.Equal(branch.Id, (await result.Companies.SingleAsync()).BranchId);
        Assert.Single(await result.AuditLogs.Where(x => x.OperationType == "COMPANY_INITIALIZED").ToListAsync());
    }

    [SqlServerFact]
    public async Task Authorized_branch_setup_preserves_one_merchant_and_each_branch_reference()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var owner = await InitializeOwnerAsync(test);
        await using var services = test.Services();
        await using var scope = services.CreateAsyncScope();
        var setup = scope.ServiceProvider.GetRequiredService<IBranchSetupService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.CreateAsync(owner, "قبل إعداد المنشأة"));
        await scope.ServiceProvider.GetRequiredService<ICompanySetupService>().InitializeAsync(owner, "تاجر واحد", "الفرع الأول");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => setup.CreateAsync(0, "غير مصرح"));
        await Assert.ThrowsAsync<ArgumentException>(() => setup.CreateAsync(owner, " "));
        var secondId = await setup.CreateAsync(owner, "الفرع الثاني");
        await using var verify = test.Context();
        var company = await verify.Companies.SingleAsync();
        Assert.NotEqual(company.BranchId, secondId);
        verify.Warehouses.AddRange(new Warehouse("مخزن أول", company.BranchId), new Warehouse("مخزن ثان", secondId));
        verify.Centers.AddRange(new Center("مركز أول", company.BranchId), new Center("مركز ثان", secondId));
        await verify.SaveChangesAsync();
        Assert.Equal(2, await verify.Branches.CountAsync());
        Assert.Equal(new[] { "مخزن ثان" }, await verify.Warehouses.Where(x => x.BranchId == secondId).Select(x => x.NameAr).ToArrayAsync());
        Assert.Equal(new[] { "مركز أول" }, await verify.Centers.Where(x => x.BranchId == company.BranchId).Select(x => x.NameAr).ToArrayAsync());
        var audit = await verify.AuditLogs.SingleAsync(x => x.OperationType == "BRANCH_CREATED");
        Assert.Equal(secondId, audit.RecordId);
        Assert.Equal(owner, audit.UserId);
        await Assert.ThrowsAsync<SqlException>(() => verify.Database.ExecuteSqlRawAsync(
            "INSERT dbo.CompanyInfo (CompanyInfoId,BranchId,NameAr,IsActive,IsDeleted,UseImageInsteadOfText) VALUES (2,{0},N'تاجر ثان',1,0,0)", secondId));
    }

    [SqlServerFact]
    public async Task Concurrent_branch_creation_allocates_distinct_keys_and_audits_each_branch()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var owner = await InitializeOwnerAsync(test);
        await using var services = test.Services();
        await using (var scope = services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<ICompanySetupService>().InitializeAsync(owner, "تاجر", "الفرع الأول");

        async Task<int> CreateBranch(string name)
        {
            await using var scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IBranchSetupService>().CreateAsync(owner, name);
        }
        var ids = await Task.WhenAll(CreateBranch("فرع ثان"), CreateBranch("فرع ثالث"));
        Assert.Equal(2, ids.Distinct().Count());
        await using var verify = test.Context();
        Assert.Equal(3, await verify.Branches.CountAsync());
        Assert.Single(await verify.Companies.ToListAsync());
        Assert.Equal(ids.Select(id => (long)id).Order(), (await verify.AuditLogs.Where(x => x.OperationType == "BRANCH_CREATED")
            .Select(x => x.RecordId!.Value).ToListAsync()).Order());
    }

    [SqlServerFact]
    public async Task Branch_creation_rolls_back_when_audit_persistence_fails()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var owner = await InitializeOwnerAsync(test);
        await using var services = test.Services();
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICompanySetupService>().InitializeAsync(owner, "تاجر", "الأول");
        await using (var setup = test.Context())
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER dbo.RejectTestBranchAudit ON dbo.AuditLogs AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted WHERE OperationType = 'BRANCH_CREATED')
                        THROW 51004, 'Test audit storage failure.', 1;
                END;
                """);
        await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<IBranchSetupService>().CreateAsync(owner, "يتراجع"));
        await using var verify = test.Context();
        Assert.Single(await verify.Branches.ToListAsync());
        Assert.False(await verify.AuditLogs.AnyAsync(x => x.OperationType == "BRANCH_CREATED"));
    }

    [SqlServerFact]
    public async Task Multi_branch_downgrade_preserves_data_and_refuses_to_discard_additional_branches()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var owner = await InitializeOwnerAsync(test);
        await using var services = test.Services();
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICompanySetupService>().InitializeAsync(owner, "تاجر", "الأول");
        await using var database = test.Context();
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("20261030060503_SupportMerchantBranches");
        // A single existing branch can return to the previous schema without data loss.
        await migrator.MigrateAsync("20261029172557_ProtectAuditLedger");
        Assert.Single(await database.Branches.AsNoTracking().ToListAsync());
        await database.Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<IBranchSetupService>().CreateAsync(owner, "الثاني");
        await migrator.MigrateAsync("20261030060503_SupportMerchantBranches");
        var error = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync("20261029172557_ProtectAuditLedger"));
        Assert.Equal(51002, error.Number);
        Assert.Equal(2, await database.Branches.CountAsync());
        Assert.Single(await database.Companies.ToListAsync());
        Assert.Equal(3, (await database.Database.GetAppliedMigrationsAsync()).Count());
    }

    [SqlServerFact]
    public async Task Separate_databases_never_share_company_or_user_state()
    {
        await using var first = await SqlTestDatabase.CreateAsync();
        await using var second = await SqlTestDatabase.CreateAsync();
        var firstOwner = await InitializeOwnerAsync(first);
        var secondOwner = await InitializeOwnerAsync(second);
        await using var firstServices = first.Services();
        await using var secondServices = second.Services();
        await using var firstScope = firstServices.CreateAsyncScope();
        await using var secondScope = secondServices.CreateAsyncScope();
        await firstScope.ServiceProvider.GetRequiredService<ICompanySetupService>().InitializeAsync(firstOwner, "التاجر الأول", "الأول");
        await firstScope.ServiceProvider.GetRequiredService<IBranchSetupService>().CreateAsync(firstOwner, "فرع إضافي للأول");
        await secondScope.ServiceProvider.GetRequiredService<ICompanySetupService>().InitializeAsync(secondOwner, "التاجر الثاني", "الأول");
        await using var one = first.Context();
        await using var two = second.Context();
        Assert.Equal("التاجر الأول", (await one.Companies.SingleAsync()).NameAr);
        Assert.Equal("التاجر الثاني", (await two.Companies.SingleAsync()).NameAr);
        Assert.Equal(2, await one.Branches.CountAsync());
        Assert.Single(await two.Branches.ToListAsync());
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

    [SqlServerFact]
    public async Task Period_close_and_reopen_require_distinct_permissions_and_persist_audit()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var owner = await InitializeOwnerAsync(test);
        int periodId, clerkId;
        await using (var setup = test.Context())
        {
            var period = new FiscalPeriod("سنة", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
            var clerk = new User("period-clerk", new HisabTak.Infrastructure.Security.BCryptPasswordHasher().Hash("Test-" + Guid.NewGuid().ToString("N")));
            setup.FiscalPeriods.Add(period);
            setup.Users.Add(clerk);
            await setup.SaveChangesAsync();
            periodId = period.Id;
            clerkId = clerk.Id;
            await Assert.ThrowsAsync<SqlException>(() => setup.Database.ExecuteSqlRawAsync("UPDATE dbo.FiscalPeriods SET IsClosed = 1 WHERE FiscalPeriodId = {0}", periodId));
        }
        await using var services = test.Services();
        async Task Change(int actor, bool close)
        {
            await using var scope = services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IFiscalPeriodService>();
            if (close) await service.CloseAsync(actor, periodId);
            else await service.ReopenAsync(actor, periodId);
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Change(clerkId, true));
        await using (var grant = test.Context())
        {
            var close = await grant.Permissions.SingleAsync(x => x.Code == PermissionCodes.PeriodClose);
            grant.UserPermissions.Add(new UserPermission(clerkId, close.Id));
            await grant.SaveChangesAsync();
        }
        await Change(clerkId, true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Change(clerkId, false));
        await using (var closed = test.Context())
        {
            var period = await closed.FiscalPeriods.SingleAsync();
            Assert.True(period.IsClosed);
            Assert.Equal(clerkId, period.ClosedByUserId);
            Assert.Throws<InvalidOperationException>(() => period.EnsurePostingAllowed(new DateOnly(2026, 5, 1)));
        }
        await Change(owner, false);
        await using var verify = test.Context();
        var reopened = await verify.FiscalPeriods.SingleAsync();
        reopened.EnsurePostingAllowed(new DateOnly(2026, 5, 1));
        Assert.Null(reopened.ClosedAt);
        var audit = await verify.AuditLogs.Where(x => x.DocumentTypeCode == "FiscalPeriods").OrderBy(x => x.AuditLogId).ToListAsync();
        Assert.Equal(new[] { "FISCAL_PERIOD_CLOSED", "FISCAL_PERIOD_REOPENED" }, audit.Select(x => x.OperationType));
        Assert.Equal(clerkId, audit[0].UserId);
        Assert.Equal(owner, audit[1].UserId);
        Assert.All(audit, item => Assert.Equal((long)periodId, item.RecordId));
    }

    [SqlServerFact]
    public async Task Concurrent_period_closures_commit_once_and_keep_the_audit_consistent()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var owner = await InitializeOwnerAsync(test);
        int periodId;
        await using (var setup = test.Context())
        {
            var period = new FiscalPeriod("سنة", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
            setup.FiscalPeriods.Add(period);
            await setup.SaveChangesAsync();
            periodId = period.Id;
        }
        await using var services = test.Services();
        async Task<bool> Attempt()
        {
            await using var scope = services.CreateAsyncScope();
            try { await scope.ServiceProvider.GetRequiredService<IFiscalPeriodService>().CloseAsync(owner, periodId); return true; }
            catch (InvalidOperationException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Attempt(), Attempt()), result => result);
        await using var verify = test.Context();
        Assert.True((await verify.FiscalPeriods.SingleAsync()).IsClosed);
        Assert.Single(await verify.AuditLogs.Where(x => x.OperationType == "FISCAL_PERIOD_CLOSED").ToListAsync());
    }

    [SqlServerFact]
    public async Task Period_reopen_rolls_back_when_its_audit_cannot_be_saved()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        var owner = await InitializeOwnerAsync(test);
        int periodId;
        await using (var setup = test.Context())
        {
            var period = new FiscalPeriod("سنة", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
            setup.FiscalPeriods.Add(period);
            await setup.SaveChangesAsync();
            periodId = period.Id;
        }
        await using var services = test.Services();
        await using (var scope = services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IFiscalPeriodService>().CloseAsync(owner, periodId);
        await using (var setup = test.Context())
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER dbo.RejectTestPeriodReopen ON dbo.AuditLogs AFTER INSERT AS
                BEGIN
                    SET NOCOUNT ON;
                    IF EXISTS (SELECT 1 FROM inserted WHERE OperationType = 'FISCAL_PERIOD_REOPENED')
                        THROW 51005, 'Test period audit storage failure.', 1;
                END;
                """);
        await using (var scope = services.CreateAsyncScope())
            await Assert.ThrowsAsync<DbUpdateException>(() => scope.ServiceProvider.GetRequiredService<IFiscalPeriodService>().ReopenAsync(owner, periodId));
        await using var verify = test.Context();
        var stillClosed = await verify.FiscalPeriods.SingleAsync();
        Assert.True(stillClosed.IsClosed);
        Assert.Equal(owner, stillClosed.ClosedByUserId);
        Assert.False(await verify.AuditLogs.AnyAsync(x => x.OperationType == "FISCAL_PERIOD_REOPENED"));
    }

    private static async Task<int> InitializeOwnerAsync(SqlTestDatabase test)
    {
        await using (var setup = test.Context()) await new ReferenceDataSeeder(setup).SeedAsync();
        await using var services = test.Services();
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IInitialOwnerService>().CreateAsync("owner", "Test-" + Guid.NewGuid().ToString("N"));
    }
}
