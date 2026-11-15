using System.Globalization;
using System.Windows;
using HisabTak.Application.Inventory;
using Microsoft.Extensions.DependencyInjection;

namespace HisabTak.WPF.Views;
public partial class StockWindow
{
    private string? appliedCoupon;
    private void InitializeCoupons()
    {
        Bind(CouponCurrency,currencies.Select(c=>new Choice(c.Id,c.Label)));
        CouponCurrency.SelectedValue=currency;
        Bind(CouponCustomer,new[]{new Choice(0,"كل العملاء")}.Concat(accounts.Where(a=>a.IsCustomer).Select(a=>new Choice(a.Id,a.Label))));
        CouponStart.SelectedDate=DateTime.Today;CouponEnd.SelectedDate=DateTime.Today.AddMonths(1);
    }
    private async Task<T> CouponService<T>(Func<ICouponService,Task<T>> action)
    {
        await using var scope=scopes.CreateAsyncScope();return await action(scope.ServiceProvider.GetRequiredService<ICouponService>());
    }
    private async Task LoadCoupons()=>CouponGrid.ItemsSource=await CouponService(s=>s.CouponsAsync(actor,branch));
    private async void RefreshCoupons(object sender,RoutedEventArgs e)=>await Run(LoadCoupons);
    private async void CreateSaleCoupon(object sender,RoutedEventArgs e)=>await Run(async()=>
    {
        await CouponService(s=>s.CreateCouponAsync(actor,branch,new(NewCouponCode.Text,Id(CouponCurrency),CouponPercentage.IsChecked==true,
            Number(CouponValue),Number(CouponMinimum),Day(CouponStart),Day(CouponEnd),int.Parse(CouponMaximum.Text,CultureInfo.InvariantCulture),Id(CouponCustomer)==0?null:Id(CouponCustomer))));
        await LoadCoupons();Status.Text="تم إنشاء الكوبون. لا يستهلك الاستخدام إلا عند ترحيل البيع.";
    });
    private async void DisableSaleCoupon(object sender,RoutedEventArgs e)=>await Run(async()=>
    {
        if(CouponGrid.SelectedItem is not CouponInfo coupon || !Confirm("تعطيل الكوبون مع حفظ تاريخه؟"))return;
        await CouponService(async s=>{await s.DisableCouponAsync(actor,branch,coupon.Id);return true;});await LoadCoupons();
    });
    private async void ApplySaleCoupon(object sender,RoutedEventArgs e)=>await Run(async()=>
    {
        if(current is {State:not 1} or {ConvertedInvoiceId:not null})return;
        var draft=await CouponService(s=>s.ApplyCouponAsync(actor,Build(),SaleCoupon.Text));
        appliedCoupon=draft.CouponCode;SaleCoupon.Text=appliedCoupon;rows.Clear();
        foreach(var line in draft.Lines)rows.Add(new(line,Product(line.ProductId),UnitName(line.UnitId)));
        Status.Text=$"تم تطبيق الكوبون {appliedCoupon} · الخصم {draft.Lines.Sum(l=>l.Discount):G29}. أزل الكوبون قبل تعديل السطور ثم أعد تطبيقه.";
    });
    private void RemoveSaleCoupon(object sender,RoutedEventArgs e)
    {
        if(busy || !CouponControls.IsEnabled || appliedCoupon==null)return;
        var lines=rows.Select(r=>r.Line with {Discount=0}).ToArray();rows.Clear();
        foreach(var line in lines)rows.Add(new(line,Product(line.ProductId),UnitName(line.UnitId)));
        appliedCoupon=null;SaleCoupon.Clear();Status.Text="أزيل خصم الكوبون من سطور الفاتورة.";
    }
}
