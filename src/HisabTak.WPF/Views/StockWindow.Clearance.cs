using System.Globalization;
using System.Windows;
using HisabTak.Application.Inventory;

namespace HisabTak.WPF.Views;

public partial class StockWindow
{
    private sealed record FeeRow(CustomsCharge Charge, string Label)
    {
        public override string ToString() => $"{Label} · {FeeLabel(Charge.Treatment)} · {Charge.Amount:G29}";
    }

    private static string FeeLabel(FeeTreatment? treatment) => treatment switch
    {
        FeeTreatment.InventoryByValue => "تكلفة حسب القيمة",
        FeeTreatment.InventoryByWeight => "تكلفة حسب الوزن",
        FeeTreatment.Expense => "مصروف",
        FeeTreatment.RecoverableTax => "ضريبة مستردة",
        _ => "يتطلب تصنيف الحساب"
    };

    private async void ConfigureFee(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var account = Id(FeeAccount);
        await Change(s => s.ConfigureFeeAsync(actor,branch,account,(FeeTreatment)(FeePolicy.SelectedIndex+1)));
        lookups = await Use(s=>s.LookupsAsync(actor,branch));
        Status.Text = "حُفظت المعالجة الافتراضية لحساب الرسم.";
    });

    private async void LoadFeePurchase(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var id = long.Parse(FeePurchase.Text.Trim(), CultureInfo.InvariantCulture);
        var invoice = await Use(s=>s.GetAsync(actor,id));
        if (invoice.State != 2 || invoice.Draft.Kind != StockDocumentKind.Purchase || invoice.Draft.BranchId != branch)
            throw new ArgumentException("Invalid purchase.");
        if (rows.Count != 0 && !Confirm("استبدال سطور التخليص بأصناف الفاتورة المحددة؟")) return;
        Warehouse.SelectedValue = invoice.Draft.WarehouseId;
        rows.Clear();
        foreach (var line in invoice.Draft.Lines) rows.Add(new(line,Product(line.ProductId),UnitName(line.UnitId)));
        Status.Text = "تم ربط أصناف الشراء. اختر صندوق أو بنك سداد الرسوم.";
    });

    private void ApplyFeeWeight(object sender, RoutedEventArgs e) => _ = Run(() =>
    {
        if (Lines.SelectedItem is not DisplayLine selected || Number(FeeWeight) <= 0) throw new ArgumentException("Select a line and enter its weight.");
        rows[rows.IndexOf(selected)] = selected with { Line = selected.Line with { ShippingWeight = Number(FeeWeight) } };
        Status.Text = "حُفظ وزن السطر في المحرر.";
        return Task.CompletedTask;
    });

    private void AddFee(object sender, RoutedEventArgs e) => _ = Run(() =>
    {
        var amount = Number(FeeAmount);
        if (amount <= 0 || decimal.Round(amount, 2) != amount)
            throw new ArgumentException("Invalid fee amount.");
        var account = accounts.Single(x => x.Id == Id(FeeAccount));
        var rule = lookups.FeeRules.SingleOrDefault(x=>x.AccountId == account.Id);
        if (rule == null) { Status.Text = "احفظ قاعدة حساب الرسم أولًا."; return Task.CompletedTask; }
        Fees.Items.Add(new FeeRow(new(account.Id, amount, rule.Treatment), account.Label));
        FeeAmount.Clear();
        return Task.CompletedTask;
    });

    private void RemoveFee(object sender, RoutedEventArgs e)
    {
        if (Fees.SelectedItem is FeeRow row) Fees.Items.Remove(row);
    }

    private CustomsDeclaration ReadDeclaration() => new(
        int.Parse(VehicleCount.Text, CultureInfo.InvariantCulture), VehicleNumber.Text.Trim(), OriginCountry.Text.Trim(),
        GoodsType.Text.Trim(), DeclarationType.SelectedIndex + 1, Fees.Items.Cast<FeeRow>().Select(x => x.Charge).ToArray(),
        string.IsNullOrWhiteSpace(FeePurchase.Text) ? null : long.Parse(FeePurchase.Text.Trim(),CultureInfo.InvariantCulture));

    private static bool SameDeclaration(CustomsDeclaration? first, CustomsDeclaration? second) =>
        first == null || second == null ? first == second :
        (first with { Charges = second.Charges }) == second && first.Charges.SequenceEqual(second.Charges);

    private void ShowDeclaration(CustomsDeclaration? declaration)
    {
        FeePurchase.Text = declaration?.PurchaseId?.ToString(CultureInfo.InvariantCulture) ?? "";
        FeeWeight.Clear();
        VehicleCount.Text = (declaration?.VehicleCount ?? 1).ToString(CultureInfo.InvariantCulture);
        VehicleNumber.Text = declaration?.VehicleNumber ?? "";
        OriginCountry.Text = declaration?.Country ?? "";
        GoodsType.Text = declaration?.Goods ?? "";
        DeclarationType.SelectedIndex = (declaration?.DeclarationType ?? 1) - 1;
        FeeAmount.Clear();
        Fees.Items.Clear();
        if (declaration != null)
            foreach (var charge in declaration.Charges)
                Fees.Items.Add(new FeeRow(charge, accounts.FirstOrDefault(x => x.Id == charge.AccountId)?.Label ?? charge.AccountId.ToString()));
    }
}
