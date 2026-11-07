using HisabTak.Domain.Common;
using HisabTak.Domain.Setup;
using HisabTak.Infrastructure.Persistence;
using HisabTak.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace HisabTak.Infrastructure.Tests;

public sealed class DatabaseModelTests
{
    [Fact]
    public void All_reference_entities_have_soft_delete_filters_and_concurrency_tokens()
    {
        using var database = ModelContext();
        var entities = database.Model.GetEntityTypes().Where(x => typeof(ReferenceEntity).IsAssignableFrom(x.ClrType)).ToList();
        Assert.NotEmpty(entities);
        foreach (var entity in entities)
        {
            Assert.NotEmpty(entity.GetDeclaredQueryFilters());
            var version = entity.FindProperty(nameof(ReferenceEntity.RowVersion));
            Assert.NotNull(version);
            Assert.True(version.IsConcurrencyToken);
        }
    }

    [Fact]
    public void Current_model_matches_the_migration_snapshot()
    {
        using var database = ModelContext();
        Assert.False(database.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Merchant_is_singleton_but_branch_keys_are_generated_without_a_single_branch_constraint()
    {
        using var database = ModelContext();
        var model = database.GetService<IDesignTimeModel>().Model;
        var branch = model.FindEntityType(typeof(Branch))!;
        Assert.Equal(ValueGenerated.OnAdd, branch.FindProperty(nameof(Branch.Id))!.ValueGenerated);
        Assert.Equal("NEXT VALUE FOR [dbo].[BranchNumbers]", branch.FindProperty(nameof(Branch.Id))!.GetDefaultValueSql());
        Assert.DoesNotContain(branch.GetCheckConstraints(), constraint => constraint.Name == "CK_Branches_LocalBranch");
        var company = model.FindEntityType(typeof(CompanyInfo))!;
        Assert.Equal("[CompanyInfoId] = 1", company.GetCheckConstraints().Single(x => x.Name == "CK_CompanyInfo_Singleton").Sql);
        Assert.Contains(company.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(Branch));
    }

    [Theory]
    [InlineData("Server=localhost;Database=App;Integrated Security=True;Encrypt=False")]
    [InlineData("Server=localhost;Database=App;Integrated Security=True;TrustServerCertificate=True")]
    [InlineData("Server=localhost;Database=master;Integrated Security=True;Encrypt=True")]
    public void Registration_rejects_insecure_connections_and_system_databases(string connection) =>
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddSqlServerFoundation(connection));

    [Fact]
    public void Registration_accepts_strict_certificate_pinning_without_disabling_verification()
    {
        var certificate = Path.Combine(Path.GetTempPath(), "hisabtak-model-only.crt");
        using var services = new ServiceCollection().AddSqlServerFoundation(
            $"Server=localhost;Database=HisabTak_PinModel;Integrated Security=True;Encrypt=Strict;TrustServerCertificate=False;Server Certificate={certificate}")
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = services.CreateScope();
        var connection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(
            scope.ServiceProvider.GetRequiredService<HisabTakDbContext>().Database.GetConnectionString());
        Assert.Equal(Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Strict, connection.Encrypt);
        Assert.False(connection.TrustServerCertificate);
        Assert.Equal(certificate, connection.ServerCertificate);
    }

    [Fact]
    public async Task Physical_delete_is_rejected_before_contacting_sql()
    {
        await using var database = ModelContext();
        var unit = new Unit("حبة");
        database.Attach(unit);
        database.Entry(unit).Property(x => x.Id).CurrentValue = 99;
        database.Entry(unit).State = EntityState.Deleted;
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.SaveChangesAsync());
    }

    [Fact]
    public void Password_hashing_salts_each_password_and_rejects_incorrect_password()
    {
        var hasher = new BCryptPasswordHasher();
        var password = "Test-" + Guid.NewGuid().ToString("N");
        var first = hasher.Hash(password);
        var second = hasher.Hash(password);
        Assert.NotEqual(first, second);
        Assert.True(hasher.Verify(password, first));
        Assert.False(hasher.Verify("wrong-password", first));
        Assert.False(hasher.Verify(password, "not-a-hash"));
        Assert.Throws<ArgumentException>(() => hasher.Hash("short"));
        Assert.Throws<ArgumentException>(() => hasher.Hash(new string('ع', 37)));
    }

    private static HisabTakDbContext ModelContext() => new(new DbContextOptionsBuilder<HisabTakDbContext>()
        .UseSqlServer("Server=localhost;Database=HisabTak_ModelOnly;Integrated Security=True;Encrypt=True",
            sql => sql.UseCompatibilityLevel(160)).Options);
}
