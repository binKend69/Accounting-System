using HisabTak.Application.Backup;
using HisabTak.Application.Accounting;
using HisabTak.Application.Security;
using HisabTak.Application.Setup;
using HisabTak.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

Console.InputEncoding = System.Text.Encoding.UTF8;
Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length == 0 || args[0] is "--help" or "help")
{
    Console.WriteLine("HisabTak database administration (explicit database connection required).");
    Console.WriteLine("probe | migrate [new-server-backup-path] | seed | init-owner | init-company | add-branch | backup <server-path> | restore-new <server-backup> <new-database> <server-data-directory>");
    Console.WriteLine("Set HISABTAK_Database__ConnectionString securely; passwords are prompted, never command-line arguments.");
    Console.WriteLine("period-close <period-id> | period-reopen <period-id> (separate accounting permissions required)");
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var connection = Environment.GetEnvironmentVariable("HISABTAK_Database__ConnectionString");
    if (string.IsNullOrWhiteSpace(connection))
        throw new InvalidOperationException("An explicit database connection is required.");
    if (args[0] == "probe" && args.Length == 1)
    {
        var settings = new SqlConnectionStringBuilder(connection);
        if (settings.TrustServerCertificate || settings.Encrypt == SqlConnectionEncryptOption.Optional)
            throw new ArgumentException("SQL certificate verification is required.");
        await using var sql = new SqlConnection(connection);
        await sql.OpenAsync(cancellation.Token);
        await using var command = new SqlCommand("SELECT 1", sql);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellation.Token), System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("SQL functional probe failed.");
        Console.WriteLine("SQL functional probe passed.");
        return 0;
    }
    var services = new ServiceCollection().AddSqlServerFoundation(connection);
    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    await using var scope = provider.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<HisabTakDbContext>();
    var token = cancellation.Token;
    switch (args[0])
    {
        case "migrate":
            if (await database.Database.CanConnectAsync(token)
                && (await database.Database.GetPendingMigrationsAsync(token)).Any())
            {
                if (args.Length != 2)
                    throw new InvalidOperationException("Existing databases require a new server backup path before migration.");
                var actor = await AuthenticateAsync(scope.ServiceProvider, token);
                await scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>().CreateAsync(actor.UserId, args[1], token);
            }
            await database.Database.MigrateAsync(token);
            Console.WriteLine("Migrations applied.");
            break;
        case "seed":
            await scope.ServiceProvider.GetRequiredService<ReferenceDataSeeder>().SeedAsync(token);
            Console.WriteLine("Reference data prepared; no default user or exchange rates were created.");
            break;
        case "init-owner":
            var username = ReadText("اسم المالك للدخول: ");
            var password = ReadPassword("كلمة المرور الجديدة: ");
            if (password != ReadPassword("أعد كلمة المرور: ")) throw new InvalidOperationException("Passwords do not match.");
            await scope.ServiceProvider.GetRequiredService<IInitialOwnerService>().CreateAsync(username, password, token);
            Console.WriteLine("تم إنشاء المالك.");
            break;
        case "init-company":
            var owner = await AuthenticateAsync(scope.ServiceProvider, token);
            var companyName = ReadText("اسم التاجر أو المنشأة: ");
            var branchName = ReadText("اسم الفرع الأول للمنشأة: ");
            await scope.ServiceProvider.GetRequiredService<ICompanySetupService>().InitializeAsync(owner.UserId, companyName, branchName, token);
            Console.WriteLine("تم إعداد المنشأة والفرع لهذه القاعدة.");
            break;
        case "add-branch" when args.Length == 1:
            var branchActor = await AuthenticateAsync(scope.ServiceProvider, token);
            var newBranchName = ReadText("اسم الفرع الجديد للمنشأة: ");
            var newBranchId = await scope.ServiceProvider.GetRequiredService<IBranchSetupService>()
                .CreateAsync(branchActor.UserId, newBranchName, token);
            Console.WriteLine($"تم إنشاء الفرع برقم {newBranchId}.");
            break;
        case "period-close" or "period-reopen" when args.Length == 2:
            if (!int.TryParse(args[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var periodId) || periodId <= 0)
                throw new ArgumentException("A positive fiscal period ID is required.");
            var periodActor = await AuthenticateAsync(scope.ServiceProvider, token);
            var periods = scope.ServiceProvider.GetRequiredService<IFiscalPeriodService>();
            if (args[0] == "period-close") await periods.CloseAsync(periodActor.UserId, periodId, token);
            else await periods.ReopenAsync(periodActor.UserId, periodId, token);
            Console.WriteLine("تم حفظ حالة الفترة وتدقيق العملية.");
            break;
        case "backup" when args.Length == 2:
            var backupActor = await AuthenticateAsync(scope.ServiceProvider, token);
            await scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>().CreateAsync(backupActor.UserId, args[1], token);
            Console.WriteLine("تم إنشاء النسخة والتحقق منها.");
            break;
        case "restore-new" when args.Length == 4:
            var restoreActor = await AuthenticateAsync(scope.ServiceProvider, token);
            await scope.ServiceProvider.GetRequiredService<IDatabaseBackupService>()
                .RestoreToNewDatabaseAsync(restoreActor.UserId, args[1], args[2], args[3], token);
            Console.WriteLine("تمت الاستعادة إلى قاعدة جديدة؛ القاعدة الأصلية محفوظة.");
            break;
        default:
            Console.Error.WriteLine("Unknown command or argument count. Use --help.");
            return 2;
    }
    return 0;
}
catch (Exception exception)
{
    // Connection strings, passwords, SQL parameters and exception messages may contain secrets.
    Console.Error.WriteLine($"تعذر إكمال العملية ({exception.GetType().Name}). تحقق من الاتصال والصلاحيات والإعدادات؛ لم تُعرض بيانات سرية.");
    return 1;
}

static async Task<AuthenticatedUser> AuthenticateAsync(IServiceProvider services, CancellationToken cancellationToken)
{
    var user = ReadText("اسم المستخدم: ");
    var password = ReadPassword("كلمة المرور: ");
    return await services.GetRequiredService<IAuthenticationService>().AuthenticateAsync(user, password, cancellationToken)
        ?? throw new UnauthorizedAccessException("Authentication failed.");
}

static string ReadText(string prompt)
{
    Console.Write(prompt);
    var value = Console.ReadLine();
    ArgumentException.ThrowIfNullOrWhiteSpace(value);
    return value.Trim();
}

static string ReadPassword(string prompt)
{
    if (Console.IsInputRedirected) throw new InvalidOperationException("Use an interactive terminal for password entry.");
    Console.Write(prompt);
    var text = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; }
        else if (!char.IsControl(key.KeyChar)) text.Append(key.KeyChar);
    }
    Console.WriteLine();
    return text.ToString();
}
