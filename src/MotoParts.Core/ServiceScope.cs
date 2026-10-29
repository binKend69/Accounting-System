using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Seeding;
using MotoParts.Core.Services;

namespace MotoParts.Core;

/// <summary>
/// One unit of work: a database session and the services that share it.
///
/// Create one per user action (a checkout, a shift close, a report), use it,
/// dispose it. Every service obtained from the same scope writes through the
/// same connection, so they all join one transaction when one is open - which
/// is what makes a multi-service operation such as checkout atomic.
///
/// Services are built on first use, so a report screen never pays for the
/// checkout machinery.
/// </summary>
public sealed class ServiceScope : IDisposable
{
    private readonly Database _database;

    private AuditService? _audit;
    private SequenceService? _sequences;
    private SettingsService? _settings;
    private AccountingService? _accounting;
    private FiscalPeriodService? _periods;
    private UserService? _users;
    private DatabaseSeeder? _seeder;
    private InventoryService? _inventory;
    private CatalogService? _catalog;
    private ShiftService? _shifts;
    private PartyService? _parties;
    private SaleService? _sales;
    private PurchaseService? _purchases;
    private VoucherService? _vouchers;
    private PayrollService? _payroll;
    private ReportService? _reports;
    private BackupService? _backups;
    private ChartOfAccountsService? _chart;

    public ServiceScope(Database database, IClock clock)
    {
        _database = database;
        Clock = clock;
        Db = new DbSession(database);
    }

    public IClock Clock { get; }

    public DbSession Db { get; }

    public Database Database => _database;

    public AuditService Audit => _audit ??= new AuditService(Db, Clock);

    public SequenceService Sequences => _sequences ??= new SequenceService(Db);

    public SettingsService Settings => _settings ??= new SettingsService(Db, Audit);

    public AccountingService Accounting => _accounting ??= new AccountingService(Db, Clock, Sequences, Audit);

    public FiscalPeriodService FiscalPeriods => _periods ??= new FiscalPeriodService(Db, Clock, Audit);

    public UserService Users => _users ??= new UserService(Db, Clock, Audit);

    public DatabaseSeeder Seeder => _seeder ??= new DatabaseSeeder(Db, Clock, FiscalPeriods);

    public InventoryService Inventory => _inventory ??= new InventoryService(Db, Clock, Accounting, Audit);

    public CatalogService Catalog => _catalog ??= new CatalogService(Db, Clock, Audit);

    public ShiftService Shifts => _shifts ??= new ShiftService(Db, Clock, Accounting, Audit);

    public PartyService Parties => _parties ??= new PartyService(Db, Clock, Accounting, Audit);

    public SaleService Sales => _sales ??= new SaleService(Db, Clock, Accounting, Inventory, Shifts, Sequences, Settings, Parties);

    public PurchaseService Purchases => _purchases ??= new PurchaseService(Db, Clock, Accounting, Inventory, Sequences, Audit);

    public VoucherService Vouchers => _vouchers ??= new VoucherService(Db, Clock, Accounting, Shifts, Sequences, Parties, Audit);

    public PayrollService Payroll => _payroll ??= new PayrollService(Db, Clock, Accounting, Sequences, Audit);

    public ReportService Reports => _reports ??= new ReportService(Db, Clock, Inventory, Shifts);

    public BackupService Backups => _backups ??= new BackupService(Database, Clock);

    public ChartOfAccountsService Chart => _chart ??= new ChartOfAccountsService(Db, Clock, Audit);

    public void Dispose() => Db.Dispose();
}
