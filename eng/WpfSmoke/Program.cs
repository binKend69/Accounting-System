using System.Windows;
using System.Collections.ObjectModel;
using System.IO;
using HisabTak.Application.Accounting;
using HisabTak.Application.Inventory;
using HisabTak.Application.Security;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ApplicationIdentity = HisabTak.Application.ApplicationIdentity;
using HisabTak.WPF.ViewModels;
using HisabTak.WPF.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var window = new MainWindow(new MainWindowViewModel(new ApplicationIdentity("HisabTak", "smoke")),
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<ILogger<MainWindow>>());
        window.Show(); window.UpdateLayout();
        if (window.FlowDirection != FlowDirection.RightToLeft) throw new InvalidOperationException("RTL layout missing.");
        if (((FrameworkElement)window.FindName("WorkspacePanel")).Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Workspace must require authentication.");
        Directory.CreateDirectory("artifacts/wpf-smoke");
        foreach (var width in new[] { 1220, 980 })
        {
            window.Width = width; window.UpdateLayout();
            var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create($"artifacts/wpf-smoke/login-{width}.png"); encoder.Save(stream);
        }
        var login = (FrameworkElement)window.FindName("LoginPanel");
        var button = FindButton(login);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (!((TextBlock)window.FindName("StatusText")).Text.Contains("لم يضبط اتصال"))
            throw new InvalidOperationException("Missing-connection login must fail visibly and safely.");
        window.Close();
        var documents = new SmokeDocuments();
        var inventory = new SmokeInventory();
        using var authenticated = new ServiceCollection().AddLogging()
            .AddSingleton<IAuthenticationService, SmokeAuthentication>().AddSingleton<IAccountingWorkspaceService, SmokeWorkspace>()
            .AddSingleton<IInventorySetupService>(inventory)
            .AddSingleton<IJournalDocumentService>(documents).BuildServiceProvider();
        MainWindow OpenAuthenticated()
        {
            var w = new MainWindow(new MainWindowViewModel(new ApplicationIdentity("HisabTak","smoke")),
                authenticated.GetRequiredService<IServiceScopeFactory>(), authenticated.GetRequiredService<ILogger<MainWindow>>());
            w.Show(); ((TextBox)w.FindName("Username")).Text="smoke"; ((PasswordBox)w.FindName("Password")).Password="smoke-only";
            Click(w,"LoginButton");
            if (((FrameworkElement)w.FindName("WorkspacePanel")).Visibility!=Visibility.Visible) throw new InvalidOperationException("Login did not open the workspace.");
            return w;
        }
        var editor = OpenAuthenticated();
        var rows = (ObservableCollection<MainWindow.EditorLine>)((DataGrid)editor.FindName("LinesGrid")).ItemsSource;
        rows.Add(new() { AccountId=1,Side="1",Amount="100.25",Rate="1" });
        rows.Add(new() { AccountId=2,Side="2",Amount="100.25",Rate="1" });
        editor.UpdateLayout(); Click(editor,"SaveButton");
        if (documents.Stored?.Draft.Lines.Count!=2 || documents.Stored.Draft.Lines[0].Valuation.Original.Amount!=100.25m)
            throw new InvalidOperationException("Editor did not save the reviewed line values.");
        if (!((Button)editor.FindName("PostButton")).IsEnabled || !((Button)editor.FindName("PrintButton")).IsEnabled)
            throw new InvalidOperationException("Saved draft actions are unavailable.");
        Capture(editor,"workspace-draft"); editor.Close();
        documents.MarkPosted();
        var history = OpenAuthenticated(); ((ListBox)history.FindName("DocumentList")).SelectedIndex=0;
        Click(history,"OpenDocumentButton");
        if (((DataGrid)history.FindName("LinesGrid")).IsEnabled || ((Button)history.FindName("SaveButton")).IsEnabled
            || ((Button)history.FindName("PostButton")).IsEnabled || !((Button)history.FindName("ReverseButton")).IsEnabled)
            throw new InvalidOperationException("Posted document editing was not disabled.");
        if (documents.PostCalls!=0) throw new InvalidOperationException("Reading or saving a draft must not post.");
        Capture(history,"workspace-posted");
        ((TabItem)history.FindName("InventoryTab")).IsSelected = true;
        Click(history,"LoadInventoryButton");
        if (((ItemsControl)history.FindName("UnitList")).Items.Count != 1 || ((ItemsControl)history.FindName("LocationList")).Items.Count != 1)
            throw new InvalidOperationException("Inventory references did not load for selected warehouse.");
        ((TextBox)history.FindName("InventoryNameBox")).Text = "Shelf B";
        Click(history,"CreateLocationButton");
        if (inventory.Created != InventoryReferenceKind.Location || inventory.Parent != 7 || inventory.Name != "Shelf B")
            throw new InvalidOperationException("Inventory setup did not send the selected warehouse and name.");
        Capture(history,"inventory-setup");
        Click(history,"LogoutButton");
        if (((ItemsControl)history.FindName("UnitList")).Items.Count != 0 || ((ComboBox)history.FindName("WarehouseBox")).Items.Count != 0)
            throw new InvalidOperationException("Logout retained inventory reference data.");
        history.Close(); app.Shutdown();
        Console.WriteLine("WPF smoke passed: XAML construction, RTL, authenticated workspace gate, two window sizes, safe missing-connection login, authenticated editor save and immutable posted view (stub services).");
    }
    private static void Click(Window window,string name)
    {
        ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }
    private static void Capture(Window window,string name)
    {
        window.UpdateLayout();
        var image=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); image.Render(window);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream=File.Create($"artifacts/wpf-smoke/{name}.png"); encoder.Save(stream);
    }
    private static Button FindButton(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button) return button;
            try { return FindButton(child); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("Login button not found.");
    }
}

internal sealed class SmokeInventory : IInventorySetupService
{
    public InventoryReferenceKind? Created { get; private set; }
    public int? Parent { get; private set; }
    public string? Name { get; private set; }
    public Task<InventorySetup> LoadAsync(int actorId, int branchId, CancellationToken ct = default) =>
        Task.FromResult(new InventorySetup([new(1, "حبة", null)], [new(7, "المستودع الرئيسي", branchId)], [new(8, "الرف الأول", 7)]));
    public Task<int> CreateAsync(int actorId, int branchId, InventoryReferenceKind kind, string name, int? warehouseId = null, CancellationToken ct = default)
    {
        Created = kind; Parent = warehouseId; Name = name; return Task.FromResult(9);
    }
}
