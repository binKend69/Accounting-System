using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;
using MotoParts.Core.Services;

namespace MotoParts.Core.Seeding;

/// <summary>
/// Sample data for trying the system: a catalogue of motorcycle parts, staff,
/// customers, suppliers, opening stock and a day of trading.
///
/// Everything goes through the real services, so the demo obeys the same rules
/// as live data and every integrity check passes from the first sign-in.
/// </summary>
public sealed class DemoDataSeeder
{
    /// <summary>Password given to the demo staff accounts. Shown on the setup screen.</summary>
    public const string DemoPassword = "123456";

    private readonly ServiceScope _app;

    public DemoDataSeeder(ServiceScope app) => _app = app;

    public void Seed(Actor owner)
    {
        _app.Db.InTransaction(() =>
        {
            var accountant = _app.Users.Create(
                new UserInput("accountant", "سارة العتيبي", "E-002", "0551112222", new DateOnly(2024, 3, 1), Money.Of(6500), [RoleName.Accountant]),
                DemoPassword, owner);
            var seller = _app.Users.Create(
                new UserInput("sales", "خالد الحربي", "E-003", "0553334444", new DateOnly(2025, 1, 15), Money.Of(4500), [RoleName.Salesperson]),
                DemoPassword, owner);

            // Demo accounts sign in with the published password straight away.
            _app.Db.Execute("UPDATE users SET must_change_password = 0 WHERE id IN (@a, @s)", new { a = accountant.Id, s = seller.Id });

            var categories = new Dictionary<string, long>();

            foreach (var name in new[] { "الفرامل", "الزيوت والفلاتر", "الكهرباء", "السلاسل والتروس", "الإطارات", "الإكسسوارات" })
            {
                categories[name] = _app.Catalog.SaveCategory(null, name, null, true, owner).Id;
            }

            var catalogue = new (string Sku, string? Barcode, string Name, string Category, string Brand, string Fits, string Unit, decimal Price, decimal Cost, decimal Qty, decimal Reorder)[]
            {
                ("BP-1001", "6281000000011", "فحمات فرامل أمامية", "الفرامل", "Honda", "CG125 / CB150", "طقم", 115, 55, 24, 6),
                ("BP-1002", "6281000000028", "فحمات فرامل خلفية", "الفرامل", "Honda", "CG125 / CB150", "طقم", 95, 42, 18, 6),
                ("BD-2001", null, "قرص فرامل أمامي", "الفرامل", "Yamaha", "YBR125", "قطعة", 260, 140, 6, 2),
                ("BC-2101", null, "كيبل فرامل خلفي", "الفرامل", "Suzuki", "GN125", "قطعة", 35, 14, 30, 10),
                ("OIL-4001", "6281000000035", "زيت محرك 10W-40 شبه صناعي", "الزيوت والفلاتر", "Motul", "عام", "لتر", 34.5m, 18.25m, 60, 20),
                ("OIL-4002", null, "زيت محرك 20W-50 معدني", "الزيوت والفلاتر", "Castrol", "عام", "لتر", 22, 11.5m, 48, 20),
                ("OF-4101", "6281000000042", "فلتر زيت", "الزيوت والفلاتر", "Honda", "CG125", "قطعة", 28, 12, 40, 10),
                ("AF-4201", null, "فلتر هواء", "الزيوت والفلاتر", "Yamaha", "YBR125", "قطعة", 45, 21, 3, 5),
                ("SP-5001", "6281000000059", "بوجي (شمعة إشعال)", "الكهرباء", "NGK", "عام", "قطعة", 25, 9.5m, 80, 20),
                ("BT-5101", null, "بطارية 12V 7Ah", "الكهرباء", "Yuasa", "عام", "قطعة", 210, 128, 8, 3),
                ("HL-5201", null, "لمبة إضاءة أمامية LED", "الكهرباء", "Philips", "عام", "قطعة", 75, 33, 15, 5),
                ("CH-6001", "6281000000066", "جنزير 428H", "السلاسل والتروس", "DID", "125-150cc", "قطعة", 120, 66, 12, 4),
                ("SK-6101", null, "طقم تروس وجنزير", "السلاسل والتروس", "JT", "CG125", "طقم", 340, 195, 5, 2),
                ("TR-7001", null, "إطار أمامي 2.75-18", "الإطارات", "IRC", "عام", "قطعة", 230, 150, 10, 4),
                ("TR-7002", null, "إطار خلفي 3.00-18", "الإطارات", "IRC", "عام", "قطعة", 265, 172, 2, 4),
                ("IT-7101", null, "لستك داخلي 18 إنش", "الإطارات", "Kenda", "عام", "قطعة", 38, 17, 25, 8),
                ("MR-8001", null, "مرآة جانبية (زوج)", "الإكسسوارات", "عام", "عام", "زوج", 60, 24, 14, 4),
                ("HG-8101", null, "قبضات مقود", "الإكسسوارات", "عام", "عام", "زوج", 30, 11, 20, 6),
            };

            var products = new Dictionary<string, Product>();

            foreach (var p in catalogue)
            {
                products[p.Sku] = _app.Catalog.CreateProduct(new ProductInput(p.Sku, p.Barcode, p.Name, null, categories[p.Category],
                    p.Brand, p.Fits, p.Unit, Money.Of(p.Price), Money.Of(p.Cost), Quantity.Of(p.Reorder), true), owner);
            }

            var supplier = _app.Parties.SaveSupplier(null, new SupplierInput("شركة القطع الأصلية المحدودة", "أبو فهد", "0112223333", null, "الرياض - الصناعية الثانية", "300555666700003", true, null), owner);
            var supplier2 = _app.Parties.SaveSupplier(null, new SupplierInput("مؤسسة الزيوت المتحدة", "م. ياسر", "0126667777", null, "جدة", null, true, null), owner);

            // Most stock arrives through a received purchase (on credit), so the
            // supplier has a balance to pay; the rest is opening stock.
            var purchased = catalogue.Where((_, i) => i % 3 != 2).ToList();
            var opening = catalogue.Where((_, i) => i % 3 == 2).ToList();

            _app.Purchases.DraftAndReceive(owner, new PurchaseInput(supplier.Id, _app.Clock.Today,
                purchased.Where(p => !p.Sku.StartsWith("OIL", StringComparison.Ordinal))
                    .Select(p => new PurchaseLineInput(products[p.Sku].Id, Quantity.Of(p.Qty), Money.Of(p.Cost))).ToList(),
                PaymentMethod.Credit, _app.Settings.GetTaxRate(), "SUP-2026-0145"));

            _app.Purchases.DraftAndReceive(owner, new PurchaseInput(supplier2.Id, _app.Clock.Today,
                purchased.Where(p => p.Sku.StartsWith("OIL", StringComparison.Ordinal))
                    .Select(p => new PurchaseLineInput(products[p.Sku].Id, Quantity.Of(p.Qty), Money.Of(p.Cost))).ToList(),
                PaymentMethod.Transfer, _app.Settings.GetTaxRate(), "UO-88213"));

            foreach (var p in opening)
            {
                _app.Inventory.RecordOpeningStock(owner, products[p.Sku].Id, Quantity.Of(p.Qty), Money.Of(p.Cost));
            }

            var garage = _app.Parties.SaveCustomer(null, new CustomerInput("ورشة النجم للصيانة", "0554445555", null, "الرياض - حي السلي", "310444555600003", Money.Of(5000), true, null), owner);
            var courier = _app.Parties.SaveCustomer(null, new CustomerInput("شركة التوصيل السريع", "0118889999", null, "الرياض", null, Money.Of(10000), true, "أسطول 40 دراجة"), owner);
            _app.Parties.SaveCustomer(null, new CustomerInput("أحمد الدوسري", "0557778888", null, null, null, Money.Zero, true, null), owner);
            _app.Parties.SetCustomerOpeningBalance(courier.Id, Money.Of(1850), owner);

            // A day of trading on the accountant's till.
            var cashierActor = _app.Users.LoadActor(accountant.Id);
            var shift = _app.Shifts.Shifts(activeOnly: true).First();
            var session = _app.Shifts.Open(cashierActor, shift.Id, Money.Of(500));
            _app.Shifts.CheckIn(cashierActor);

            CartLine Line(string sku, decimal qty, decimal discount = 0) =>
                new(products[sku].Id, Quantity.Of(qty), null, Money.Of(discount));

            _app.Sales.Checkout(cashierActor, [Line("BP-1001", 1), Line("OIL-4001", 1)], PaymentMethod.Cash, Money.Of(200));
            _app.Sales.Checkout(cashierActor, [Line("SP-5001", 2), Line("OF-4101", 1)], PaymentMethod.Card);
            _app.Sales.Checkout(cashierActor, [Line("CH-6001", 1), Line("SK-6101", 1, 20)], PaymentMethod.Credit, customerId: garage.Id);
            var toRefund = _app.Sales.Checkout(cashierActor, [Line("HL-5201", 2)], PaymentMethod.Cash);
            _app.Sales.Checkout(cashierActor, [Line("OIL-4002", 3), Line("IT-7101", 2)], PaymentMethod.Credit, customerId: courier.Id);

            var refundItem = _app.Sales.GetItems(toRefund.Id).Single();
            _app.Sales.Refund(cashierActor, toRefund.Id, [new RefundLineRequest(refundItem.Id, Quantity.One)], "اللمبة لا تناسب الموديل");

            _app.Vouchers.ReceiveFromCustomer(cashierActor, courier.Id, Money.Of(850), PaymentMethod.Cash, throughDrawer: true, "دفعة على الحساب");
            _app.Vouchers.RecordExpense(cashierActor, _app.Vouchers.ExpenseAccounts().First(a => a.Code == "6500").Id,
                Money.Of(40), PaymentMethod.Cash, throughDrawer: true, "توصيل طلبية لورشة النجم");

            var expected = _app.Shifts.DrawerTotals(session.Id).Expected;
            _app.Shifts.Close(cashierActor, session.Id, expected - Money.Of(5));

            _app.Vouchers.RecordExpense(owner, _app.Vouchers.ExpenseAccounts().First(a => a.Code == "6300").Id,
                Money.Of(4500), PaymentMethod.Transfer, throughDrawer: false, "إيجار المحل لهذا الشهر", "مكتب الدار العقاري");
            _app.Vouchers.PaySupplier(owner, supplier.Id, Money.Of(2000), PaymentMethod.Transfer, false, "حوالة بنكية");

            // The salesperson's till is left open, ready to sell.
            _app.Shifts.Open(_app.Users.LoadActor(seller.Id), shift.Id, Money.Of(300));
        });
    }
}
