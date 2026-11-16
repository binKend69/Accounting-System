using System.Windows;
using HisabTak.Application.Accounting;
namespace HisabTak.WPF.Views;
public partial class MainWindow
{
    private void ClearTrialBalance(object sender,RoutedEventArgs e)
    {
        if(TrialGrid!=null)TrialGrid.ItemsSource=null;
        if(TrialTotals!=null)TrialTotals.Text="";
    }
    private async void RefreshTrialBalance(object sender,RoutedEventArgs e)=>await Run(async()=>
    {
        ClearTrialBalance(sender,e);
        if(TrialFrom.SelectedDate is not DateTime from || TrialTo.SelectedDate is not DateTime to || TrialCurrency.SelectedValue is not int currency)
            throw new InputException("اختر بداية الفترة ونهايتها وعملة التقرير.");
        if(from>to)throw new InputException("تاريخ بداية التقرير يجب ألا يتجاوز تاريخ النهاية.");
        var report=await Use<IFinancialReports,IReadOnlyList<TrialBalanceRow>>(s=>s.TrialBalanceAsync(Actor,Branch,currency==0?null:currency,
            DateOnly.FromDateTime(from),DateOnly.FromDateTime(to)));
        TrialGrid.ItemsSource=report;
        TrialTotals.Text=$"الافتتاحي: مدين {report.Sum(x=>x.OpeningDebit):G29} · دائن {report.Sum(x=>x.OpeningCredit):G29} | الحركة: مدين {report.Sum(x=>x.PeriodDebit):G29} · دائن {report.Sum(x=>x.PeriodCredit):G29} | الختامي: مدين {report.Sum(x=>x.ClosingDebit):G29} · دائن {report.Sum(x=>x.ClosingCredit):G29}";
        StatusText.Text=currency==0?"ميزان المراجعة بالقيم الدفترية التاريخية بعملة الأساس.":"أرصدة وحركة العملة المختارة فقط؛ قد تكون طرفًا من قيد متعدد العملات. راجع التوازن العام بعملة الأساس.";
    });
}
