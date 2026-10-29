using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HisabTak.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<HisabTakDbContext>
{
    public HisabTakDbContext CreateDbContext(string[] args)
    {
        // Migration generation does not connect. Applying migrations requires an explicit target.
        var connection = Environment.GetEnvironmentVariable("HISABTAK_Database__ConnectionString")
            ?? "Server=localhost;Database=HisabTak_DesignOnly;Integrated Security=True;Encrypt=True;TrustServerCertificate=False";
        return new HisabTakDbContext(new DbContextOptionsBuilder<HisabTakDbContext>().UseSqlServer(connection,
            sql => sql.UseCompatibilityLevel(160)).Options);
    }
}
