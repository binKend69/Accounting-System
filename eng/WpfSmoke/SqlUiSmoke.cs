using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HisabTak.Application.Accounting;
using HisabTak.Application.Inventory;
using HisabTak.Application.Security;
using HisabTak.Application.Setup;
using HisabTak.Domain.Accounting;
using HisabTak.Domain.Inventory;
using HisabTak.Domain.Security;
using HisabTak.Infrastructure.Persistence;
using HisabTak.WPF.Views;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static class SqlUiSmoke
{
    public static async Task Run()
    {
        var raw=Environment.GetEnvironmentVariable("HISABTAK_UI_SQL_CONNECTION")??throw new InvalidOperationException("Real UI SQL connection required.");
        var connection=new SqlConnectionStringBuilder(raw);
        if(connection.TrustServerCertificate || connection.Encrypt==SqlConnectionEncryptOption.Optional)throw new InvalidOperationException("Verified TLS required.");
        var name="HisabTak_UiTest_"+Guid.NewGuid().ToString("N");connection.InitialCatalog=name;
        using var services=new ServiceCollection().AddLogging().AddSqlServerFoundation(connection.ConnectionString).BuildServiceProvider();
        async Task<T> Use<S,T>(Func<S,Task<T>> action) where S:notnull
        {await using var scope=services.CreateAsyncScope();return await action(scope.ServiceProvider.GetRequiredService<S>());}
        async Task Change<S>(Func<S,Task> action) where S:notnull=>await Use<S,bool>(async s=>{await action(s);return true;});
        var windows=new List<Window>();
        try
        {
            await Change<HisabTakDbContext>(async db=>{await db.Database.MigrateAsync();await new ReferenceDataSeeder(db).SeedAsync();});
            var password="Ui-"+Guid.NewGuid().ToString("N");
            var actor=await Use<IInitialOwnerService,int>(x=>x.CreateAsync("ui-owner",password));
            var login=await Use<IAuthenticationService,AuthenticatedUser?>(x=>x.AuthenticateAsync("ui-owner",password));
            Check(login?.UserId==actor,"Real authentication failed.");
            await Change<ICompanySetupService>(x=>x.InitializeAsync(actor,"UI test merchant","UI branch"));
            var setup=await Use<HisabTakDbContext,(int Branch,int Currency,int Unit,int CashGroup)>(async db=>
            {
                var date=DateOnly.FromDateTime(DateTime.Today);
                db.FiscalPeriods.Add(new FiscalPeriod("UI period",date.AddDays(-20),date.AddDays(20)));await db.SaveChangesAsync();
                return ((await db.Branches.SingleAsync()).Id,(await db.Currencies.SingleAsync(x=>x.Code=="YER")).Id,
                    (await db.Units.FirstAsync()).Id,(await db.AccountGeneralGroups.SingleAsync(x=>x.SystemCode=="CASH")).Id);
            });
            var group=await Use<IChartOfAccountsService,int>(x=>x.CreateGroupAsync(actor,"009","UI root",null));
            async Task<int> Account(int code,int? classification=null)=>await Use<IChartOfAccountsService,int>(x=>x.CreateClassifiedAccountAsync(actor,setup.Branch,"009"+code,"UI account "+code,group,setup.Currency,1,classification));
            var cash=await Account(1,setup.CashGroup);var stock=await Account(2);var sales=await Account(3);var cost=await Account(4);
            var adjustment=await Account(5);var input=await Account(6);var output=await Account(7);var returns=await Account(8);var rounding=await Account(9);
            await Change<IJournalService>(x=>x.SetBaseCurrencyAsync(actor,setup.Currency));
            await Change<IJournalService>(x=>x.ConfigureRoundingAsync(actor,setup.Branch,rounding,2));
            await Change<IStockDocumentService>(x=>x.ConfigureAsync(actor,setup.Branch,new(stock,sales,cost,adjustment,input,output,SalesReturnsAccountId:returns)));
            await Use<IInventorySetupService,int>(x=>x.CreateAsync(actor,setup.Branch,InventoryReferenceKind.Warehouse,"UI warehouse"));
            await Use<IStockDocumentService,int>(x=>x.CreateProductAsync(actor,new("UI-1","UI product",setup.Unit,CostMethod.WeightedAverage,false)));
            var workspace=await Use<IAccountingWorkspaceService,AccountingWorkspace>(x=>x.LoadAsync(actor,setup.Branch));
            var window=new StockWindow(services.GetRequiredService<IServiceScopeFactory>(),actor,setup.Branch,setup.Currency,workspace.Accounts.ToArray(),workspace.Currencies.ToArray()) {Confirmation=_=>true};
            windows.Add(window);await window.LoadAsync();window.Show();window.UpdateLayout();
            void Text(string field,string value)=>((TextBox)window.FindName(field)).Text=value;
            void Select(string field,int value)=>((ComboBox)window.FindName(field)).SelectedValue=value;
            void Date(string field,int offset)=>((DatePicker)window.FindName(field)).SelectedDate=DateTime.Today.AddDays(offset);
            async Task<StockDocument> Enter(StockDocumentKind kind,int offset,string qty,string price,long? original=null)
            {
                await Click(window,"NewStockDocumentButton");Select("Kind",(int)kind);Select("Party",cash);Date("EntryDate",offset);
                Text("Quantity",qty);Text("Price",price);Text("Original",original?.ToString()??"");
                await Click(window,"AddStockLineButton");await Click(window,"SaveButton");
                Check(((TextBlock)window.FindName("Status")).Text=="حفظت المسودة.","UI save failed.");
                await Click(window,"PostButton");
                var doc=(await Use<IStockDocumentService,IReadOnlyList<StockDocument>>(x=>x.ListAsync(actor,setup.Branch)))[0];
                Check(doc.State==2,"UI posting failed.");Check(!((Button)window.FindName("SaveButton")).IsEnabled,"Posted editor writable.");return doc;
            }
            var first=await Enter(StockDocumentKind.Purchase,-3,"10","20");
            var second=await Enter(StockDocumentKind.Purchase,-2,"10","30");
            var sale=await Enter(StockDocumentKind.Sale,-1,"3","50");
            var ret=await Enter(StockDocumentKind.SalesReturn,0,"1","0",sale.Movements[0].Id);
            Date("HistoryFrom",-3);Date("HistoryTo",0);((TabItem)window.FindName("HistoryTab")).IsSelected=true;
            await Click(window,"HistorySearchButton");
            var grid=(DataGrid)window.FindName("HistoryGrid");Check(grid.Items.Count==4,"UI history omitted rows.");
            Directory.CreateDirectory("artifacts/wpf-smoke");Capture(window,"sql-trade-history");
            grid.SelectedIndex=3;await Click(window,"HistoryOpenButton");
            var line=((StockWindow.DisplayLine)((DataGrid)window.FindName("Lines")).Items[0]).Line;
            Check(line.UnitPrice==20,"Reopened first purchase price changed.");
            grid.SelectedIndex=1;await Click(window,"HistoryOpenButton");
            var original=await Use<IStockDocumentService,StockDocument>(x=>x.GetAsync(actor,sale.Id));
            Check(original.State==2,"History read changed document state.");
            // Reject reversal while a return depends on the sale, with no partial effect.
            Date("ReverseDate",0);await Click(window,"ReverseButton");
            Check((await Use<IStockDocumentService,StockDocument>(x=>x.GetAsync(actor,sale.Id))).State==2,"Dependent reversal was allowed.");
            grid.SelectedIndex=0;await Click(window,"HistoryOpenButton");Date("ReverseDate",0);await Click(window,"ReverseButton");
            Check((await Use<IStockDocumentService,StockDocument>(x=>x.GetAsync(actor,ret.Id))).State==3,"UI reverse failed.");
            var balances=await Use<IStockDocumentService,IReadOnlyList<StockBalance>>(x=>x.BalancesAsync(actor,setup.Branch,DateOnly.FromDateTime(DateTime.Today)));
            Check(balances.Sum(x=>x.Quantity)==17 && balances.Sum(x=>x.BookValue)==425,"SQL balance differs after UI reversal.");
            Capture(window,"sql-reversed-return");window.Close();
            var reader=await Use<HisabTakDbContext,int>(async db=>
            {
                var u=new User("ui-reader",services.GetRequiredService<IPasswordHasher>().Hash(password));db.Users.Add(u);await db.SaveChangesAsync();
                foreach(var p in await db.Permissions.Where(x=>x.Code==PermissionCodes.JournalRead || x.Code==PermissionCodes.AccountingAllBranches).ToArrayAsync())db.UserPermissions.Add(new UserPermission(u.Id,p.Id));
                await db.SaveChangesAsync();return u.Id;
            });
            var limited=new StockWindow(services.GetRequiredService<IServiceScopeFactory>(),reader,setup.Branch,setup.Currency,workspace.Accounts.ToArray(),workspace.Currencies.ToArray()) {Confirmation=_=>true};
            windows.Add(limited);await limited.LoadAsync();limited.Show();
            ((TextBox)limited.FindName("Price")).Text="20";((ComboBox)limited.FindName("Party")).SelectedValue=cash;
            await Click(limited,"AddStockLineButton");await Click(limited,"SaveButton");
            Check(((TextBlock)limited.FindName("Status")).Text.Contains("لا تملك صلاحية"),"Read-only UI did not show permission rejection.");
            Check((await Use<IStockDocumentService,IReadOnlyList<StockDocument>>(x=>x.ListAsync(actor,setup.Branch))).Count==4,"Denied UI save wrote a document.");
            limited.Close();
            await AdministrationSmoke.Run(services, actor, reader, setup.Branch);
            Console.WriteLine("Real SQL WPF acceptance passed: authentication, purchases 20/30, sale, original return, history filters/open, dependent reversal rejection, reversal, balances, read-only permission rejection. Confirmation dialogs use a test adapter; services and SQL are real.");
        }
        finally
        {
            foreach(var w in windows)w.Close();
            SqlConnection.ClearAllPools();connection.InitialCatalog="master";
            await using var db=new SqlConnection(connection.ConnectionString);await db.OpenAsync();
            // Generated isolated database only; never accept a database name from input.
            await using var command=new SqlCommand($"IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END",db);
            command.Parameters.AddWithValue("@name",name);await command.ExecuteNonQueryAsync();
        }
    }
    private static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    private static async Task Click(Window window,string name)
    {
        ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var until=DateTime.UtcNow.AddSeconds(45);
        while(!((TabControl)window.FindName("Tabs")).IsEnabled)
        {if(DateTime.UtcNow>until)throw new TimeoutException("UI operation did not finish.");await Task.Delay(20);}
        window.UpdateLayout();
    }
    private static void Capture(Window window,string name)
    {
        window.UpdateLayout();var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var output=File.Create($"artifacts/wpf-smoke/{name}.png");encoder.Save(output);
    }
}
