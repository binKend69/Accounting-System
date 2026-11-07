using HisabTak.Domain.Accounting;
using HisabTak.Domain.Common;
using HisabTak.Domain.Security;
using HisabTak.Domain.Setup;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HisabTak.Infrastructure.Persistence;

public sealed class HisabTakDbContext(DbContextOptions<HisabTakDbContext> options) : DbContext(options)
{
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<Branch> Branches => Set<Branch>();
    public DbSet<Center> Centers => Set<Center>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<StockLocation> StockLocations => Set<StockLocation>();
    public DbSet<CompanyInfo> Companies => Set<CompanyInfo>();
    public DbSet<CustomSetting> CustomSettings => Set<CustomSetting>();
    public DbSet<AccountGeneralGroup> AccountGeneralGroups => Set<AccountGeneralGroup>();
    public DbSet<AccountMainGroup> AccountMainGroups => Set<AccountMainGroup>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<FiscalPeriod> FiscalPeriods => Set<FiscalPeriod>();
    public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.UseCollation("Arabic_100_CI_AS");
        var currency = Reference<Currency>(model, "Currencies", "CurrencyId", 50);
        currency.Property(x => x.Code).HasMaxLength(3).IsRequired();
        currency.Property(x => x.Symbol).HasMaxLength(10).IsRequired();
        currency.Property(x => x.FractionName).HasMaxLength(30);
        currency.Property(x => x.ExchangeRate).HasPrecision(18, 6);
        currency.HasIndex(x => x.Code).IsUnique();
        currency.HasIndex(x => x.IsBaseCurrency).IsUnique().HasFilter("[IsBaseCurrency] = 1 AND [IsDeleted] = 0");
        currency.ToTable("Currencies", t =>
        {
            t.HasCheckConstraint("CK_Currencies_Rate", "[ExchangeRate] IS NULL OR [ExchangeRate] > 0");
            t.HasCheckConstraint("CK_Currencies_Conversion", "[ConversionType] IN (1,2)");
        });

        var branch = Reference<Branch>(model, "Branches", "BranchId", 100);
        // A sequence upgrades the existing non-IDENTITY key without rebuilding the table.
        model.HasSequence<int>("BranchNumbers", "dbo");
        branch.Property(x => x.Id).ValueGeneratedOnAdd().HasDefaultValueSql("NEXT VALUE FOR [dbo].[BranchNumbers]");
        Reference<Center>(model, "Centers", "CenterId", 100);
        Reference<Warehouse>(model, "Warehouses", "WarehouseId", 100);
        Reference<Unit>(model, "Units", "UnitId", 50);
        var department = Reference<Department>(model, "Departments", "DepartmentId", 100);
        department.Property(x => x.ColorHex).HasMaxLength(9);
        Reference<StockLocation>(model, "StockLocations", "LocationId", 100);
        Foreign<Center, Branch>(model, nameof(Center.BranchId));
        Foreign<Warehouse, Branch>(model, nameof(Warehouse.BranchId));
        Foreign<StockLocation, Warehouse>(model, nameof(StockLocation.WarehouseId));

        var company = Reference<CompanyInfo>(model, "CompanyInfo", "CompanyInfoId", 200);
        company.Property(x => x.Id).ValueGeneratedNever();
        company.HasIndex(x => x.BranchId).IsUnique();
        company.ToTable("CompanyInfo", t =>
        {
            t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_CompanyInfo_Singleton", "[CompanyInfoId] = 1");
            t.HasCheckConstraint("CK_CompanyInfo_Costing", "[CostingMethod] IS NULL OR [CostingMethod] IN (1,2)");
        });
        company.Property(x => x.NameEn).HasMaxLength(200);
        company.Property(x => x.AddressAr).HasMaxLength(300);
        company.Property(x => x.AddressEn).HasMaxLength(300);
        company.Property(x => x.Phone).HasMaxLength(30);
        company.Property(x => x.Mobile).HasMaxLength(30);
        company.Property(x => x.LogoPath).HasMaxLength(300);
        company.Property(x => x.StampPath).HasMaxLength(300);
        company.Property(x => x.InvoiceFooterNote).HasMaxLength(500);
        Foreign<CompanyInfo, Branch>(model, nameof(CompanyInfo.BranchId));
        foreach (var key in new[] { nameof(CompanyInfo.BaseCurrencyId), nameof(CompanyInfo.DefaultPurchaseCurrencyId),
                     nameof(CompanyInfo.DefaultSalesCurrencyId), nameof(CompanyInfo.DefaultVoucherCurrencyId) })
            Foreign<CompanyInfo, Currency>(model, key);

        var setting = model.Entity<CustomSetting>();
        setting.ToTable("CustomSettings");
        setting.HasKey(x => x.Key);
        setting.Property(x => x.Key).HasMaxLength(100);
        setting.Property(x => x.Value).HasMaxLength(2000).IsRequired();
        setting.Property(x => x.RowVersion).IsRowVersion();

        var general = Reference<AccountGeneralGroup>(model, "AccountGeneralGroups", "GeneralGroupId", 100);
        general.Property(x => x.SystemCode).HasMaxLength(30);
        general.HasIndex(x => x.SystemCode).IsUnique().HasFilter("[SystemCode] IS NOT NULL");
        var group = Reference<AccountMainGroup>(model, "AccountMainGroups", "MainGroupId", 150);
        group.Property(x => x.Code).HasMaxLength(10).IsRequired();
        group.HasIndex(x => x.Code).IsUnique();
        group.ToTable("AccountMainGroups", t =>
        {
            t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_AccountMainGroups_Parent", "[ParentMainGroupId] IS NULL OR [ParentMainGroupId] <> [MainGroupId]");
            t.HasCheckConstraint("CK_AccountMainGroups_Level", "[Level] > 0");
            t.HasCheckConstraint("CK_AccountMainGroups_Report", "[ClosingReportType] IS NULL OR [ClosingReportType] IN (1,2,3)");
        });
        Foreign<AccountMainGroup, AccountMainGroup>(model, nameof(AccountMainGroup.ParentMainGroupId));

        var account = Reference<Account>(model, "Accounts", "AccountId", 200);
        account.Property(x => x.AccountCode).HasMaxLength(20).IsRequired();
        account.Property(x => x.Phone).HasMaxLength(30);
        account.Property(x => x.Mobile).HasMaxLength(30);
        account.Property(x => x.Address).HasMaxLength(300);
        account.Property(x => x.Notes).HasMaxLength(500);
        account.Property(x => x.CreditLimit).HasPrecision(18, 2);
        account.Property(x => x.OpeningBalance).HasPrecision(18, 2);
        account.HasIndex(x => x.AccountCode).IsUnique();
        account.ToTable("Accounts", t =>
        {
            t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_Accounts_Nature", "[Nature] IN (1,2)");
            t.HasCheckConstraint("CK_Accounts_OpeningNature", "[OpeningBalanceNature] IS NULL OR [OpeningBalanceNature] IN (1,2)");
            t.HasCheckConstraint("CK_Accounts_OpeningBalance", "[OpeningBalance] >= 0");
            t.HasCheckConstraint("CK_Accounts_CreditLimit", "[CreditLimit] IS NULL OR [CreditLimit] >= 0");
        });
        Foreign<Account, AccountMainGroup>(model, nameof(Account.MainGroupId));
        Foreign<Account, AccountGeneralGroup>(model, nameof(Account.GeneralGroupId));
        Foreign<Account, Currency>(model, nameof(Account.CurrencyId));
        Foreign<Account, Branch>(model, nameof(Account.BranchId));

        var period = Reference<FiscalPeriod>(model, "FiscalPeriods", "FiscalPeriodId", 50);
        period.ToTable("FiscalPeriods", t =>
        {
            t.UseSqlOutputClause(false);
            t.HasCheckConstraint("CK_FiscalPeriods_Dates", "[StartDate] <= [EndDate]");
            t.HasCheckConstraint("CK_FiscalPeriods_Closure", "([IsClosed] = 0 AND [ClosedAt] IS NULL AND [ClosedByUserId] IS NULL) OR ([IsClosed] = 1 AND [ClosedAt] IS NOT NULL AND [ClosedByUserId] IS NOT NULL)");
        });
        Foreign<FiscalPeriod, User>(model, nameof(FiscalPeriod.ClosedByUserId));
        var document = Reference<DocumentType>(model, "DocumentTypes", "DocumentTypeId", 100);
        document.Property(x => x.Code).HasMaxLength(30).IsRequired();
        document.HasIndex(x => x.Code).IsUnique();
        document.ToTable("DocumentTypes", t => t.HasCheckConstraint("CK_DocumentTypes_Category", "[Category] IN (1,2,3)"));

        var user = Reference<User>(model, "Users", "UserId", 200);
        user.Property(x => x.UserName).HasMaxLength(50).IsRequired();
        user.Property(x => x.PasswordHash).HasMaxLength(300).IsRequired();
        user.HasIndex(x => x.UserName).IsUnique();
        user.ToTable("Users", t => t.HasCheckConstraint("CK_Users_PasswordHash",
            "LEN([PasswordHash]) = 60 AND LEFT([PasswordHash],4) IN ('$2a$','$2b$','$2y$')"));
        Foreign<User, Branch>(model, nameof(User.BranchId));
        Foreign<User, Center>(model, nameof(User.CenterId));
        Foreign<User, Account>(model, nameof(User.LinkedAccountId));
        var role = Reference<Role>(model, "Roles", "RoleId", 100);
        role.Property(x => x.Code).HasMaxLength(30).IsRequired();
        role.HasIndex(x => x.Code).IsUnique();
        var permission = Reference<Permission>(model, "Permissions", "PermissionId", 150);
        permission.Property(x => x.Code).HasMaxLength(100).IsRequired();
        permission.Property(x => x.Module).HasMaxLength(50).IsRequired();
        permission.HasIndex(x => x.Code).IsUnique();
        Foreign<Permission, Permission>(model, nameof(Permission.ParentPermissionId));

        model.Entity<UserRole>().ToTable("UserRoles").HasKey(x => new { x.UserId, x.RoleId });
        Foreign<UserRole, User>(model, nameof(UserRole.UserId));
        Foreign<UserRole, Role>(model, nameof(UserRole.RoleId));
        model.Entity<RolePermission>().ToTable("RolePermissions").HasKey(x => new { x.RoleId, x.PermissionId });
        Foreign<RolePermission, Role>(model, nameof(RolePermission.RoleId));
        Foreign<RolePermission, Permission>(model, nameof(RolePermission.PermissionId));
        model.Entity<UserPermission>().ToTable("UserPermissions").HasKey(x => new { x.UserId, x.PermissionId });
        Foreign<UserPermission, User>(model, nameof(UserPermission.UserId));
        Foreign<UserPermission, User>(model, nameof(UserPermission.GrantedByUserId));
        Foreign<UserPermission, Permission>(model, nameof(UserPermission.PermissionId));

        var audit = model.Entity<AuditLog>();
        audit.ToTable("AuditLogs", t => t.UseSqlOutputClause(false));
        audit.HasKey(x => x.AuditLogId);
        audit.Property(x => x.OperationType).HasMaxLength(30).IsRequired();
        audit.Property(x => x.DocumentTypeCode).HasMaxLength(50);
        audit.Property(x => x.WindowName).HasMaxLength(100);
        audit.Property(x => x.ComputerName).HasMaxLength(100);
        audit.Property(x => x.Details).HasMaxLength(1000);
        audit.HasIndex(x => new { x.UserId, x.ActionDate });
        audit.HasIndex(x => x.ActionDate);
        Foreign<AuditLog, User>(model, nameof(AuditLog.UserId));
    }

    private static EntityTypeBuilder<T> Reference<T>(ModelBuilder model, string table, string id, int nameLength)
        where T : ReferenceEntity
    {
        var entity = model.Entity<T>();
        entity.HasBaseType((Type?)null);
        entity.ToTable(table);
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).HasColumnName(id);
        entity.Property(x => x.NameAr).HasMaxLength(nameLength).IsRequired();
        entity.Property(x => x.RowVersion).IsRowVersion();
        entity.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()");
        entity.HasQueryFilter(x => !x.IsDeleted);
        return entity;
    }

    private static void Foreign<TDependent, TPrincipal>(ModelBuilder model, string property)
        where TDependent : class where TPrincipal : class =>
        model.Entity<TDependent>().HasOne<TPrincipal>().WithMany().HasForeignKey(property).OnDelete(DeleteBehavior.Restrict);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void PrepareWrites()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<ReferenceEntity>())
        {
            if (entry.State == EntityState.Deleted) throw new InvalidOperationException("Physical deletion is not allowed.");
            if (entry.State == EntityState.Added)
            {
                entry.Property(nameof(ReferenceEntity.CreatedAt)).CurrentValue = now;
                if (entry.Entity is CompanyInfo) entry.Property(nameof(ReferenceEntity.Id)).CurrentValue = 1;
            }
            if (entry.State == EntityState.Modified)
            {
                if (entry.Entity.IsDeleted && (entry.Entity is Role { IsSystemRole: true } or Account { IsSystemAccount: true }))
                    throw new InvalidOperationException("System records cannot be deleted.");
                entry.Property(nameof(ReferenceEntity.ModifiedAt)).CurrentValue = now;
            }
        }
        foreach (var entry in ChangeTracker.Entries<UserPermission>().Where(x => x.State == EntityState.Added))
            entry.Property(nameof(UserPermission.GrantedAt)).CurrentValue = now;
        foreach (var entry in ChangeTracker.Entries<AuditLog>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted) throw new InvalidOperationException("Audit records are append-only.");
            if (entry.State != EntityState.Added) continue;
            entry.Property(nameof(AuditLog.CreatedAt)).CurrentValue = now;
            entry.Property(nameof(AuditLog.ActionDate)).CurrentValue = DateOnly.FromDateTime(now);
            entry.Property(nameof(AuditLog.ActionTime)).CurrentValue = TimeOnly.FromDateTime(now);
        }
    }
}
