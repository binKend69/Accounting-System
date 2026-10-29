using HisabTak.Domain.Common;

namespace HisabTak.Domain.Setup;

public sealed class Branch : ReferenceEntity
{
    private Branch() { }
    public Branch(string name) : base(name) { }
}

public sealed class Center : ReferenceEntity
{
    private Center() { }
    public Center(string name, int? branchId = null) : base(name) => BranchId = branchId;
    public int? BranchId { get; private set; }
}

public sealed class Warehouse : ReferenceEntity
{
    private Warehouse() { }
    public Warehouse(string name, int? branchId = null) : base(name) => BranchId = branchId;
    public int? BranchId { get; private set; }
}

public sealed class Unit : ReferenceEntity
{
    private Unit() { }
    public Unit(string name) : base(name) { }
}

public sealed class Department : ReferenceEntity
{
    private Department() { }
    public Department(string name) : base(name) { }
    public string? ColorHex { get; private set; }
    public int DisplayOrder { get; private set; }
}

public sealed class StockLocation : ReferenceEntity
{
    private StockLocation() { }
    public StockLocation(string name, int? warehouseId = null) : base(name) => WarehouseId = warehouseId;
    public int? WarehouseId { get; private set; }
}

public sealed class CompanyInfo : ReferenceEntity
{
    private CompanyInfo() { }
    public CompanyInfo(string name, int branchId) : base(name) => BranchId = branchId;
    public int BranchId { get; private set; }
    public string? NameEn { get; private set; }
    public string? AddressAr { get; private set; }
    public string? AddressEn { get; private set; }
    public string? Phone { get; private set; }
    public string? Mobile { get; private set; }
    public string? LogoPath { get; private set; }
    public string? StampPath { get; private set; }
    public bool UseImageInsteadOfText { get; private set; }
    public string? InvoiceFooterNote { get; private set; }
    public int? BaseCurrencyId { get; private set; }
    public int? DefaultPurchaseCurrencyId { get; private set; }
    public int? DefaultSalesCurrencyId { get; private set; }
    public int? DefaultVoucherCurrencyId { get; private set; }
    public byte? CostingMethod { get; private set; }
}

public sealed class CustomSetting
{
    private CustomSetting() { }
    public CustomSetting(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (key.Length > 100 || value.Length > 2000) throw new ArgumentOutOfRangeException(nameof(key));
        Key = key;
        Value = value;
    }
    public string Key { get; private set; } = string.Empty;
    public string Value { get; private set; } = string.Empty;
    public byte[] RowVersion { get; private set; } = [];
    public void ChangeValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 2000) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }
}
