using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using HisabTak.Application.Accounting;
using HisabTak.Application.Security;
using HisabTak.Domain.Accounting;
using HisabTak.WPF.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HisabTak.WPF.Views;

public partial class MainWindow : Window
{
    private readonly IServiceScopeFactory scopes;
    private readonly ILogger<MainWindow> logger;
    private int? actorId;
    private int? baseCurrencyId;
    private bool busy;
    private int page;
    private Guid sourceKey = Guid.NewGuid();
    private JournalDocumentDetails? current;
    public ObservableCollection<AccountLookup> Accounts { get; } = [];
    private readonly ObservableCollection<EditorLine> lines = [];
    public sealed class EditorLine
    {
        public int AccountId { get; set; }
        public string Side { get; set; } = "1";
        public string Amount { get; set; } = "0";
        public string Rate { get; set; } = "1";
    }
    private sealed record DocumentItem(JournalDocument Document)
    {
        public string Caption => $"{TypeName(Document.TypeCode)} — {Document.Number?.ToString() ?? "مسودة"}";
        public string Subtitle => $"{Document.Date:yyyy-MM-dd} • {StateName(Document.State)} • إصدار {Document.Version}";
    }
    private sealed class InputException(string message) : Exception(message);

    public MainWindow(MainWindowViewModel viewModel, IServiceScopeFactory scopes, ILogger<MainWindow> logger)
    {
        InitializeComponent(); DataContext = viewModel; this.scopes = scopes; this.logger = logger;
        LinesGrid.ItemsSource = lines; EntryDate.SelectedDate = DateTime.Today; ReversalDate.SelectedDate = DateTime.Today;
        UpdateButtons();
    }
    private int Actor => actorId ?? throw new InputException("سجل الدخول أولاً.");
    private int Branch => BranchBox.SelectedValue is int id ? id : throw new InputException("اختر الفرع أولاً.");
    private static int Selected(ComboBox box) => box.SelectedValue is int id ? id : throw new InputException("أكمل الاختيارات المطلوبة.");
    private static string TypeName(string type) => type switch { "JOURNAL_OPENING" => "قيد افتتاحي", "RECEIPT_VOUCHER" => "سند قبض", "PAYMENT_VOUCHER" => "سند صرف", _ => "قيد يومية" };
    private static string StateName(JournalDocumentState state) => state switch { JournalDocumentState.Draft => "مسودة", JournalDocumentState.Posted => "مرحل", _ => "ملغى بعكس" };
    private string SelectedType => ((ComboBoxItem)TypeBox.SelectedItem).Tag.ToString()!;
    private async Task<T> Use<TService, T>(Func<TService, Task<T>> action) where TService : class
    {
        await using var scope = scopes.CreateAsyncScope();
        var service = scope.ServiceProvider.GetService<TService>() ?? throw new InputException("لم يضبط اتصال قاعدة البيانات. راجع دليل تشغيل الواجهة ثم أعد تشغيل البرنامج.");
        return await action(service);
    }
    private Task Change<TService>(Func<TService, Task> action) where TService : class =>
        Use<TService, bool>(async service => { await action(service); return true; });
    private async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true; LoginPanel.IsEnabled = false; WorkspacePanel.IsEnabled = false; LogoutButton.IsEnabled = false;
        StatusText.Text = "جارٍ تنفيذ العملية…";
        try { await action(); }
        catch (InputException e) { StatusText.Text = e.Message; }
        catch (DocumentConflictException) { StatusText.Text = "تغير المستند بواسطة جلسة أخرى. افتح النسخة الحالية قبل الحفظ أو الترحيل؛ لم نحفظ فوق التعديل الآخر."; }
        catch (UnauthorizedAccessException) { StatusText.Text = "لا تملك صلاحية هذه العملية أو الوصول إلى الفرع المحدد."; }
        catch (ArgumentException) { StatusText.Text = "راجع المبالغ والتواريخ والحسابات. يجب أن تكون القيم صحيحة والقيد متوازنًا."; }
        catch (Exception e)
        {
            logger.LogWarning("Accounting operation failed ({ExceptionType}).", e.GetType().Name);
            StatusText.Text = e is InvalidOperationException
                ? "تعذرت العملية: تحقق من توازن القيد، والفترة المفتوحة، وعملة الأساس وحساب التقريب، وصلاحية الحسابات. لسند القبض أو الصرف اختر حساب صندوق أو بنك في جانبه الصحيح."
                : "تعذر إكمال العملية. تحقق من اتصال قاعدة البيانات وترقيتها. أعد فتح المستند للتحقق من حالته قبل المحاولة مجددًا.";
        }
        finally { busy = false; LoginPanel.IsEnabled = true; WorkspacePanel.IsEnabled = true; LogoutButton.IsEnabled = true; UpdateButtons(); }
    }
    private async void Login_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var password = Password.Password;
        Password.Clear();
        var user = await Use<IAuthenticationService, AuthenticatedUser?>(s => s.AuthenticateAsync(Username.Text.Trim(), password));
        password = string.Empty;
        if (user is null) throw new InputException("اسم المستخدم أو كلمة المرور غير صحيحة.");
        var workspace = await Use<IAccountingWorkspaceService, AccountingWorkspace>(s => s.LoadAsync(user.UserId, null));
        actorId = user.UserId; BranchBox.ItemsSource = workspace.Branches; BranchBox.SelectedIndex = workspace.Branches.Count > 0 ? 0 : -1;
        LoginPanel.Visibility = Visibility.Collapsed; WorkspacePanel.Visibility = Visibility.Visible; LogoutButton.Visibility = Visibility.Visible;
        UserLabel.Text = user.UserName;
        if (BranchBox.SelectedValue is int) await LoadBranch();
        StatusText.Text = "تم الدخول. اختر مستندًا لفتحه أو أنشئ مستندًا جديدًا.";
    });
    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !ConfirmDiscard()) return;
        actorId = null; current = null; Accounts.Clear(); lines.Clear(); DocumentList.ItemsSource = null; BranchBox.ItemsSource = null;
        baseCurrencyId = null; sourceKey = Guid.NewGuid(); UserLabel.Text = ""; Password.Clear();
        LoginPanel.Visibility = Visibility.Visible; WorkspacePanel.Visibility = Visibility.Collapsed; LogoutButton.Visibility = Visibility.Collapsed;
        StatusText.Text = "تم تسجيل الخروج.";
    }
    private async Task LoadBranch()
    {
        var workspace = await Use<IAccountingWorkspaceService, AccountingWorkspace>(s => s.LoadAsync(Actor, Branch));
        Accounts.Clear(); foreach (var item in workspace.Accounts) Accounts.Add(item);
        baseCurrencyId = workspace.BaseCurrencyId;
        GroupBox.ItemsSource = workspace.Groups; CurrencyBox.ItemsSource = workspace.Currencies;
        GeneralGroupBox.ItemsSource = workspace.GeneralGroups; BaseCurrencyBox.ItemsSource = workspace.Currencies;
        BaseCurrencyBox.SelectedValue = baseCurrencyId; RoundingAccountBox.ItemsSource = Accounts;
        await RefreshList();
    }
    private async void Branch_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (busy || actorId is null || BranchBox.SelectedValue is not int) return;
        await Run(async () => { ResetEditor(); page = 0; await LoadBranch(); StatusText.Text = "تم تحميل مستندات الفرع."; });
    }
    private async Task RefreshList() => DocumentList.ItemsSource = (await Use<IJournalDocumentService, IReadOnlyList<JournalDocument>>(s => s.ListAsync(Actor, Branch, page))).Select(d => new DocumentItem(d)).ToArray();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Run(async () => { await RefreshList(); StatusText.Text = "تم تحديث القائمة. انقر مرتين على المستند لفتحه."; });
    private async void Previous_Click(object sender, RoutedEventArgs e) => await Run(async () => { page = Math.Max(0, page - 1); await RefreshList(); StatusText.Text = $"صفحة {page + 1}"; });
    private async void Next_Click(object sender, RoutedEventArgs e) => await Run(async () => { page++; await RefreshList(); StatusText.Text = $"صفحة {page + 1}"; });
    private bool ConfirmDiscard() => lines.Count == 0 || current?.Document.State != JournalDocumentState.Draft && current is not null ||
        MessageBox.Show("قد توجد تعديلات غير محفوظة في المحرر. هل تريد تركها؟", "تأكيد", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void ResetEditor()
    {
        current = null; sourceKey = Guid.NewGuid(); lines.Clear(); EntryDate.SelectedDate = DateTime.Today;
        DocumentTitle.Text = "مستند جديد"; TypeBox.SelectedIndex = 0; UpdateButtons();
    }
    private void New_Click(object sender, RoutedEventArgs e) { if (!ConfirmDiscard()) return; ResetEditor(); StatusText.Text = "أضف السطور ثم احفظ المسودة. الحفظ وحده لا ينشئ حركة مالية."; }
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (DocumentList.SelectedItem is not DocumentItem selected || !ConfirmDiscard()) return;
        await Run(async () => { await OpenDocument(selected.Document.Id); StatusText.Text = "تم فتح النسخة المحفوظة."; });
    }
    private async Task OpenDocument(long id)
    {
        current = await Use<IJournalDocumentService, JournalDocumentDetails>(s => s.DetailsAsync(Actor, id));
        sourceKey = current.Document.SourceKey; EntryDate.SelectedDate = current.Document.Date.ToDateTime(TimeOnly.MinValue);
        TypeBox.SelectedItem = TypeBox.Items.Cast<ComboBoxItem>().Single(x => x.Tag.ToString() == current.Document.TypeCode);
        lines.Clear();
        foreach (var line in current.Draft.Lines)
        {
            if (!Accounts.Any(a => a.Id == line.AccountId)) Accounts.Add(new(line.AccountId, $"حساب محفوظ #{line.AccountId} (غير متاح للاختيار الجديد)", line.Valuation.Original.CurrencyId, null));
            lines.Add(new() { AccountId = line.AccountId, Side = ((int)line.Side).ToString(CultureInfo.InvariantCulture), Amount = line.Valuation.Original.Amount.ToString(CultureInfo.InvariantCulture), Rate = line.Valuation.RateToBase.ToString(CultureInfo.InvariantCulture) });
        }
        DocumentTitle.Text = $"{TypeName(current.Document.TypeCode)} — {current.Document.Number?.ToString() ?? "مسودة"} • {StateName(current.Document.State)} • إصدار {current.Document.Version}";
        UpdateButtons();
    }
    private void AddLine_Click(object sender, RoutedEventArgs e) { lines.Add(new()); UpdateButtons(); }
    private void RemoveLine_Click(object sender, RoutedEventArgs e) { if (LinesGrid.SelectedItem is EditorLine line) lines.Remove(line); UpdateButtons(); }
    private JournalDraft BuildDraft()
    {
        LinesGrid.CommitEdit(DataGridEditingUnit.Cell, true); LinesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (EntryDate.SelectedDate is not DateTime dateTime || baseCurrencyId is not int baseId) throw new InputException("حدد التاريخ وعملة الأساس أولاً.");
        var date = DateOnly.FromDateTime(dateTime); var period = new FiscalPeriod("proposal", date, date);
        var definitions = lines.Select(l =>
        {
            var account = Accounts.SingleOrDefault(a => a.Id == l.AccountId) ?? throw new InputException("اختر حسابًا لكل سطر.");
            if (!decimal.TryParse(l.Amount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
                || !decimal.TryParse(l.Rate, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate)) throw new InputException("اكتب المبلغ والسعر بصيغة مثل 100.25 دون فواصل آلاف.");
            return new JournalLineDefinition(l.AccountId, l.Side == "1" ? JournalSide.Debit : JournalSide.Credit, new(new(amount, account.CurrencyId), baseId, rate, date));
        }).ToArray();
        return SelectedType == "JOURNAL_OPENING" ? JournalDraft.CreateOpening(Branch, period, date, sourceKey, definitions) : JournalDraft.Create(Branch, period, date, sourceKey, definitions);
    }
    private async void Save_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var draft = BuildDraft();
        var result = await Use<IJournalDocumentService, JournalDocument>(s => current is not null
            ? s.UpdateDraftAsync(Actor, current.Document.Id, current.Document.Version, draft)
            : SelectedType is "RECEIPT_VOUCHER" or "PAYMENT_VOUCHER" ? s.SaveVoucherAsync(Actor, draft, SelectedType) : s.SaveDraftAsync(Actor, draft));
        await OpenDocument(result.Id); await RefreshList(); StatusText.Text = "حُفظت المسودة دون أثر مالي.";
    });
    private async void Post_Click(object sender, RoutedEventArgs e)
    {
        if (current is null || MessageBox.Show("سيُرحّل المستند المالي المحفوظ. لا يعدل بعد الترحيل؛ التصحيح بالعكس. هل تتابع؟", "ترحيل", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Run(async () =>
        {
            var draft = BuildDraft();
            if (draft.EntryDate != current.Draft.EntryDate || !draft.Lines.SequenceEqual(current.Draft.Lines)) throw new InputException("توجد تعديلات في المحرر. احفظها أولاً قبل الترحيل.");
            var result = await Use<IJournalDocumentService, JournalDocument>(s => s.PostVersionAsync(Actor, current.Document.Id, current.Document.Version));
            await OpenDocument(result.Id); await RefreshList(); StatusText.Text = "تم الترحيل وحفظ الرقم والقيد وسجل التدقيق.";
        });
    }
    private async void Reverse_Click(object sender, RoutedEventArgs e)
    {
        if (current is null || MessageBox.Show("سيُنشأ قيد عكسي في التاريخ المحدد مع بقاء المستند الأصلي. هل تتابع؟", "إلغاء بعكس", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var reversalKey = Guid.NewGuid();
        await Run(async () =>
        {
            if (ReversalDate.SelectedDate is not DateTime date) throw new InputException("حدد تاريخ العكس.");
            var result = await Use<IJournalDocumentService, JournalDocument>(s => s.ReverseAsync(Actor, current.Document.Id, DateOnly.FromDateTime(date), reversalKey));
            await OpenDocument(result.Id); await RefreshList(); StatusText.Text = "تم الإلغاء بقيد عكسي مرتبط؛ رقم المستند محفوظ.";
        });
    }
    private async void CreateAccount_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var code = AccountCode.Text; var name = AccountName.Text; var group = Selected(GroupBox); var currency = Selected(CurrencyBox);
        var nature = (byte)(NatureBox.SelectedIndex + 1); var classification = GeneralGroupBox.SelectedValue as int?;
        // Create and classify atomically using a single scoped context/transaction is handled by the service below.
        await Change<IChartOfAccountsService>(s => s.CreateClassifiedAccountAsync(Actor, Branch, code, name, group, currency, nature, classification));
        await LoadBranch(); AccountCode.Clear(); AccountName.Clear(); StatusText.Text = "تم إنشاء الحساب وتصنيفه.";
    });
    private async void BaseCurrency_Click(object sender, RoutedEventArgs e) => await Run(async () => { await Change<IJournalService>(s => s.SetBaseCurrencyAsync(Actor, Selected(BaseCurrencyBox))); await LoadBranch(); StatusText.Text = "حُفظت عملة الأساس."; });
    private async void Rounding_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (!int.TryParse(PrecisionBox.Text, out var precision)) throw new InputException("أدخل دقة صحيحة.");
        await Change<IJournalService>(s => s.ConfigureRoundingAsync(Actor, Branch, Selected(RoundingAccountBox), precision)); StatusText.Text = "حُفظ حساب فرق التقريب ودقة العملة.";
    });
    private async Task Period(bool close)
    {
        if (!int.TryParse(PeriodIdBox.Text, out var id) || id <= 0) throw new InputException("أدخل رقم فترة صحيحًا.");
        await Change<IFiscalPeriodService>(s => close ? s.CloseAsync(Actor, id) : s.ReopenAsync(Actor, id)); StatusText.Text = close ? "أُقفلت الفترة." : "أُعيد فتح الفترة.";
    }
    private async void ClosePeriod_Click(object sender, RoutedEventArgs e) => await Run(() => Period(true));
    private async void ReopenPeriod_Click(object sender, RoutedEventArgs e) => await Run(() => Period(false));
    private void UpdateButtons()
    {
        var editable = current is null || current.Document.State == JournalDocumentState.Draft;
        LinesGrid.IsEnabled = editable; EntryDate.IsEnabled = editable; TypeBox.IsEnabled = current is null;
        SaveButton.IsEnabled = editable; AddLineButton.IsEnabled = editable; RemoveLineButton.IsEnabled = editable;
        PostButton.IsEnabled = current?.Document.State == JournalDocumentState.Draft; ReverseButton.IsEnabled = current?.Document.State == JournalDocumentState.Posted;
        PrintButton.IsEnabled = current is not null; BranchBox.IsEnabled = lines.Count == 0 && current is null;
    }
    private async void Print_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        if (current is null) return;
        var details = await Use<IJournalDocumentService, JournalDocumentDetails>(s => s.DetailsAsync(Actor, current.Document.Id));
        var document = new FlowDocument { FlowDirection = FlowDirection.RightToLeft, FontFamily = FontFamily, FontSize = 14, PagePadding = new Thickness(35), ColumnWidth = double.PositiveInfinity };
        document.Blocks.Add(new Paragraph(new Run($"{TypeName(details.Document.TypeCode)} — {StateName(details.Document.State)}")) { FontSize = 24, FontWeight = FontWeights.Bold });
        document.Blocks.Add(new Paragraph(new Run($"رقم: {details.Document.Number?.ToString() ?? "مسودة غير مرحلة"} | التاريخ: {details.Document.Date:yyyy-MM-dd} | الفرع: {details.Document.BranchId}")));
        if (details.Document.JournalId is long journalId)
        {
            var posted = await Use<IJournalService, PostedJournal>(s => s.GetAsync(Actor, journalId));
            foreach (var line in posted.Lines) document.Blocks.Add(new Paragraph(new Run($"حساب {line.AccountId} | {(line.Side == JournalSide.Debit ? "مدين" : "دائن")} {line.Amount} | عملة {line.CurrencyId} | أساس دفتري {line.BookBaseAmount ?? line.BaseAmount}{(line.IsRounding ? " | فرق تقريب" : "")}")));
        }
        else foreach (var line in details.Draft.Lines) document.Blocks.Add(new Paragraph(new Run($"حساب {line.AccountId} | {(line.Side == JournalSide.Debit ? "مدين" : "دائن")} {line.Valuation.Original.Amount} | عملة {line.Valuation.Original.CurrencyId}")));
        var viewer = new FlowDocumentReader { Document = document, ViewingMode = FlowDocumentReaderViewingMode.Scroll };
        new Window { Owner = this, Title = "معاينة النسخة المحفوظة — الطباعة لا تعيد الترحيل", Content = viewer, Width = 850, Height = 700, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
        StatusText.Text = "انتهت المعاينة. فتحها أو الطباعة منها لا ينشئ رقمًا أو قيدًا جديدًا.";
    });
}
