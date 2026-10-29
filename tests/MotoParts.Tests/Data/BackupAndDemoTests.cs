using Microsoft.Data.Sqlite;
using MotoParts.Core.Common;
using MotoParts.Core.Seeding;
using MotoParts.Core.Services;
using MotoParts.Tests.Support;

namespace MotoParts.Tests.Data;

public sealed class BackupAndDemoTests : TestBase
{
    private string Folder => Path.Combine(Path.GetDirectoryName(Database.FilePath)!, "backups");

    [Fact]
    public void A_backup_is_a_valid_standalone_copy()
    {
        Stock("BP-1", 10, 18, 45);

        var path = App.Backups.Create(Folder, "manual");

        Assert.Null(BackupService.Validate(path));
        Assert.False(File.Exists(path + "-wal"));
    }

    [Fact]
    public void Pruning_keeps_the_newest_backups()
    {
        for (var i = 0; i < 5; i++)
        {
            App.Backups.Create(Folder, $"n{i}");
            Clock.Advance(TimeSpan.FromMinutes(1));
            File.SetLastWriteTime(App.Backups.List(Folder)[0].Path, Clock.Now);
        }

        Assert.Equal(3, App.Backups.Prune(Folder, 2));
        Assert.Equal(2, App.Backups.List(Folder).Count);
    }

    [Fact]
    public void A_file_that_is_not_one_of_ours_is_refused()
    {
        var bogus = Path.Combine(Folder, "motoparts-bogus.db");
        Directory.CreateDirectory(Folder);
        File.WriteAllText(bogus, "not a database");

        Assert.NotNull(BackupService.Validate(bogus));
        Assert.Throws<BusinessRuleException>(() => App.Backups.Restore(bogus, Folder));
    }

    [Fact]
    public void Restoring_brings_back_the_old_data_and_keeps_a_safety_copy()
    {
        Stock("BEFORE", 1, 1, 1);
        var backup = App.Backups.Create(Folder, "point");
        Stock("AFTER", 1, 1, 1);

        foreach (var scope in new[] { App })
        {
            scope.Db.Dispose();
        }

        var safety = App.Backups.Restore(backup, Folder);

        SqliteConnection.ClearAllPools();
        using var fresh = NewScope();
        Assert.Single(fresh.Catalog.Products(), p => p.Sku == "BEFORE");
        Assert.DoesNotContain(fresh.Catalog.Products(), p => p.Sku == "AFTER");
        Assert.Null(BackupService.Validate(safety));
    }

    [Fact]
    public void Demo_data_loads_and_every_integrity_check_passes()
    {
        new DemoDataSeeder(App).Seed(Admin);

        Assert.True(App.Catalog.Products().Count >= 18);
        Assert.NotNull(App.Users.Authenticate("accountant", DemoDataSeeder.DemoPassword));
        Assert.NotNull(App.Users.Authenticate("sales", DemoDataSeeder.DemoPassword));
        Assert.All(App.Reports.IntegrityChecks(), c => Assert.True(c.Passed, $"{c.Name}: {c.Actual} / {c.Expected}"));
    }
}
