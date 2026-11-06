using System.Globalization;
using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;

namespace MotoParts.Core.Services;

/// <summary>
/// Shop configuration, stored in the database so it travels with every backup.
/// </summary>
public sealed class SettingsService
{
    public const string ShopName = "shop.name";
    public const string ShopAddress = "shop.address";
    public const string ShopPhone = "shop.phone";
    public const string ShopTaxNumber = "shop.tax_number";
    public const string ShopCommercialRegister = "shop.commercial_register";
    public const string CurrencySymbol = "shop.currency_symbol";
    public const string TaxRate = "tax.rate";
    public const string PricesIncludeTax = "tax.prices_include";
    public const string InvoiceFooter = "invoice.footer";
    public const string BackupFolder = "backup.folder";
    public const string BackupKeepCount = "backup.keep_count";
    public const string SetupCompleted = "setup.completed";

    private readonly DbSession _db;
    private readonly AuditService _audit;

    public SettingsService(DbSession db, AuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public string? Get(string key) =>
        _db.QueryFirstOrDefault<string?>("SELECT value FROM settings WHERE key = @key", new { key });

    public string Get(string key, string fallback) => Get(key) ?? fallback;

    /// <summary>Writes one setting. Pass <paramref name="actor"/> to record the change.</summary>
    public void Set(string key, string? value, Actor? actor = null)
    {
        var previous = Get(key);

        if (previous == value)
        {
            return;
        }

        _db.InTransaction(() =>
        {
            _db.Execute(
                """
                INSERT INTO settings (key, value, updated_at) VALUES (@key, @value, datetime('now', 'localtime'))
                ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
                """,
                new { key, value });

            if (actor is not null)
            {
                _audit.Record("Setting", null, AuditAction.Updated, actor,
                    new { key, value = previous }, new { key, value });
            }
        });
    }

    /// <summary>The sales tax rate as a fraction: 0.15 for 15%.</summary>
    public decimal GetTaxRate()
    {
        var raw = Get(TaxRate);
        return raw is not null && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate)
            ? rate
            : 0m;
    }

    public void SetTaxRate(decimal rate, Actor actor)
    {
        actor.Require(Permission.SettingsManage);
        ValidateTaxRate(rate);
        Set(TaxRate, rate.ToString("0.####", CultureInfo.InvariantCulture), actor);
    }

    public bool GetPricesIncludeTax() => Get(PricesIncludeTax) == "1";

    public void SetPricesIncludeTax(bool include, Actor actor)
    {
        actor.Require(Permission.SettingsManage);
        Set(PricesIncludeTax, include ? "1" : "0", actor);
    }

    public ShopProfile GetShopProfile() => new(
        Get(ShopName, "محل قطع غيار الدراجات النارية"),
        Get(ShopAddress),
        Get(ShopPhone),
        Get(ShopTaxNumber),
        Get(ShopCommercialRegister),
        Get(CurrencySymbol, "ر.س"),
        Get(InvoiceFooter));

    public void SaveShopProfile(ShopProfile profile, Actor actor)
    {
        actor.Require(Permission.SettingsManage);

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            throw new BusinessRuleException("اسم المحل مطلوب.");
        }

        _db.InTransaction(() =>
        {
            Set(ShopName, profile.Name.Trim(), actor);
            Set(ShopAddress, Clean(profile.Address), actor);
            Set(ShopPhone, Clean(profile.Phone), actor);
            Set(ShopTaxNumber, Clean(profile.TaxNumber), actor);
            Set(ShopCommercialRegister, Clean(profile.CommercialRegister), actor);
            Set(CurrencySymbol, Clean(profile.CurrencySymbol) ?? "ر.س", actor);
            Set(InvoiceFooter, Clean(profile.InvoiceFooter), actor);
        });
    }

    public static void ValidateTaxRate(decimal rate)
    {
        if (rate < 0m)
        {
            throw new BusinessRuleException("نسبة الضريبة لا يمكن أن تكون سالبة.");
        }

        // 15 typed instead of 0.15 is the usual mistake; a rate above 100% is
        // never meant.
        if (rate > 1m)
        {
            throw new BusinessRuleException("نسبة الضريبة تُكتب ككسر عشري، مثل 0.15 لنسبة 15٪.");
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record ShopProfile(
    string Name,
    string? Address,
    string? Phone,
    string? TaxNumber,
    string? CommercialRegister,
    string CurrencySymbol,
    string? InvoiceFooter);
