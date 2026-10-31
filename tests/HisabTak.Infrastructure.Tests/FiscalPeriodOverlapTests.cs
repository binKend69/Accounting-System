using HisabTak.Domain.Accounting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

namespace HisabTak.Infrastructure.Tests;

[Trait("Category", "SqlServer")]
public sealed class FiscalPeriodOverlapTests
{
    [SqlServerFact]
    public async Task Overlap_is_rejected_but_adjacent_periods_are_allowed()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        await using var db = test.Context();
        db.FiscalPeriods.Add(new FiscalPeriod("يناير", new(2026, 1, 1), new(2026, 1, 31)));
        await db.SaveChangesAsync();
        db.FiscalPeriods.Add(new FiscalPeriod("فبراير", new(2026, 2, 1), new(2026, 2, 28)));
        await db.SaveChangesAsync();
        db.FiscalPeriods.Add(new FiscalPeriod("متداخل", new(2026, 1, 31), new(2026, 2, 1)));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        await using var verify = test.Context();
        Assert.Equal(2, await verify.FiscalPeriods.CountAsync());
    }

    [SqlServerFact]
    public async Task Raw_update_and_reactivation_cannot_bypass_overlap_protection()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        await using var db = test.Context();
        db.FiscalPeriods.Add(new FiscalPeriod("أولى", new(2026, 1, 1), new(2026, 1, 31)));
        var inactive = new FiscalPeriod("موقوفة", new(2026, 1, 1), new(2026, 12, 31));
        inactive.SetActive(false);
        db.FiscalPeriods.Add(inactive);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE dbo.FiscalPeriods SET IsActive=1 WHERE FiscalPeriodId={0}", inactive.Id));
        db.FiscalPeriods.Add(new FiscalPeriod("ثانية", new(2026, 2, 1), new(2026, 2, 28)));
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE dbo.FiscalPeriods SET StartDate='20260101' WHERE StartDate='20260201'"));
    }

    [SqlServerFact]
    public async Task Concurrent_overlapping_creates_cannot_both_commit()
    {
        await using var test = await SqlTestDatabase.CreateAsync();
        async Task<bool> Attempt(string name)
        {
            await using var db = test.Context();
            db.FiscalPeriods.Add(new FiscalPeriod(name, new(2026, 1, 1), new(2026, 12, 31)));
            try { await db.SaveChangesAsync(); return true; }
            catch (DbUpdateException) { return false; }
        }
        var results = await Task.WhenAll(Attempt("أولى"), Attempt("ثانية"));
        Assert.Single(results, x => x);
        await using var verify = test.Context();
        Assert.Single(await verify.FiscalPeriods.ToListAsync());
    }
}
