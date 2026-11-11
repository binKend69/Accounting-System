using System.Globalization;
using System.Windows;
using HisabTak.Application.Inventory;

namespace HisabTak.WPF.Views;

public partial class StockWindow
{
    private sealed record FeeRow(CustomsCharge Charge, string Label)
    {
        public override string ToString() => $"{Label} · {Charge.Amount:G29}";
    }

    private void AddFee(object sender, RoutedEventArgs e) => _ = Run(() =>
    {
        var amount = Number(FeeAmount);
        if (amount <= 0 || decimal.Round(amount, 2) != amount)
            throw new ArgumentException("Invalid fee amount.");
        var account = accounts.Single(x => x.Id == Id(FeeAccount));
        if (account.CurrencyId != Id(InvoiceCurrency)) throw new ArgumentException("Fee currency mismatch.");
        Fees.Items.Add(new FeeRow(new(account.Id, amount), account.Label));
        FeeAmount.Clear();
        return Task.CompletedTask;
    });

    private void RemoveFee(object sender, RoutedEventArgs e)
    {
        if (Fees.SelectedItem is FeeRow row) Fees.Items.Remove(row);
    }

    private CustomsDeclaration ReadDeclaration() => new(
        int.Parse(VehicleCount.Text, CultureInfo.InvariantCulture), VehicleNumber.Text.Trim(), OriginCountry.Text.Trim(),
        GoodsType.Text.Trim(), DeclarationType.SelectedIndex + 1, Fees.Items.Cast<FeeRow>().Select(x => x.Charge).ToArray());

    private static bool SameDeclaration(CustomsDeclaration? first, CustomsDeclaration? second) =>
        first == null || second == null ? first == second :
        (first with { Charges = second.Charges }) == second && first.Charges.SequenceEqual(second.Charges);

    private void ShowDeclaration(CustomsDeclaration? declaration)
    {
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
