using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using HisabTak.Application.Security;
using HisabTak.Application.Setup;
using HisabTak.Domain.Security;
using HisabTak.Infrastructure.Persistence;
using HisabTak.WPF.Views;
using HisabTak.WPF.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static class AdministrationSmoke
{
    public static async Task Run(IServiceProvider services, int owner, int reader, int branch, string password)
    {
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        await using var metadata = scopes.CreateAsyncScope();
        var db = metadata.ServiceProvider.GetRequiredService<HisabTakDbContext>();
        var role = (await db.Roles.SingleAsync(x => x.Code == "ACCOUNTANT")).Id;
        var permission = (await db.Permissions.SingleAsync(x => x.Code == PermissionCodes.SetupManage)).Id;
        var admin = new AdministrationWindow(scopes, owner, branch);
        T Field<T>(string name) where T : FrameworkElement => (T)admin.FindName(name);
        var grid = Field<DataGrid>("Records");
        var search = Field<TextBox>("Search");
        var name = Field<TextBox>("EntryName");
        async Task Press(string id) => await Invoke(admin, id);
        admin.Show();
        try
        {
            await Ready(admin);
            Check(admin.FlowDirection == FlowDirection.RightToLeft, "Administration is not RTL.");
            name.Text = "P08 center";
            await Press("CreateButton");
            search.Text = "P08 center";
            Check(grid.Items.Count == 1, "Search did not find exactly one center.");
            grid.SelectedIndex = 0;
            var original = (DirectoryRow)grid.SelectedItem;
            name.Text = "P08 center renamed";
            await Press("RenameButton");
            grid.SelectedIndex = 0;
            await Press("HideButton");
            Check(grid.Items.Count == 0, "Hidden record remained visible.");
            var hidden = Field<CheckBox>("IncludeHidden");
            hidden.IsChecked = true;
            hidden.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            grid.SelectedIndex = 0;
            Check(((DirectoryRow)grid.SelectedItem).Hidden, "Hidden state missing.");
            await Press("RestoreButton");
            Check(grid.Items.OfType<DirectoryRow>().Single() is { Hidden: false } restored && restored.Id == original.Id,
                "Restore did not retain identity.");
            try
            {
                await metadata.ServiceProvider.GetRequiredService<IReferenceDirectory>()
                    .ApplyAsync(owner, branch, DirectoryKind.Center, DirectoryAction.Rename, original, "stale");
                throw new InvalidOperationException("Stale rename succeeded.");
            }
            catch (DirectoryConflictException) { }

            Field<ComboBox>("Kind").SelectedIndex = 1;
            await Ready(admin);
            search.Clear();
            name.Text = "P08 department";
            await Press("CreateButton");
            Check(grid.Items.OfType<DirectoryRow>().Any(x => x.Name == "P08 department"), "Department create failed.");
            foreach (var (key, modifiers) in new[] { (Key.F, ModifierKeys.Control), (Key.F5, ModifierKeys.None) })
            {
                var binding = admin.InputBindings.OfType<KeyBinding>().Single(x => x.Key == key && x.Modifiers == modifiers);
                ((RoutedCommand)binding.Command!).Execute(null, admin);
                await Ready(admin);
                if (key == Key.F) Check(search.IsKeyboardFocusWithin, "Find did not focus search.");
            }

            Field<TabItem>("RoleTab").IsSelected = true;
            await Ready(admin);
            var roleList = Field<ComboBox>("RoleList");
            roleList.SelectedItem = roleList.Items.OfType<SecuritySubject>().Single(x => x.Id == role);
            await Ready(admin);
            var roleGrid = Field<DataGrid>("RolePermissions");
            roleGrid.SelectedItem = roleGrid.Items.OfType<PermissionChoice>().Single(x => x.Id == permission);
            await Press("RoleGrantButton");
            Field<TabItem>("SecurityTab").IsSelected = true;
            await Ready(admin);
            var users = Field<ComboBox>("Users");
            users.SelectedItem = users.Items.OfType<SecuritySubject>().Single(x => x.Id == reader);
            await Ready(admin);
            var roles = Field<ComboBox>("Roles");
            roles.SelectedItem = roles.Items.OfType<RoleChoice>().Single(x => x.Id == role);
            await Press("AssignRoleButton");
            var permissions = Field<DataGrid>("Permissions");
            PermissionChoice Choice() => permissions.Items.OfType<PermissionChoice>().Single(x => x.Id == permission);
            Check(Choice() is { Granted: false, Inherited: true }, "Inherited permission missing.");
            permissions.SelectedItem = Choice();
            await Press("GrantButton");
            Check(Choice() is { Granted: true, Inherited: true }, "Direct grant missing.");
            permissions.SelectedItem = Choice();
            await Press("RevokeButton");
            Check(Choice() is { Granted: false, Inherited: true }, "Revoking direct grant removed role grant.");
            roles.SelectedItem = roles.Items.OfType<RoleChoice>().Single(x => x.Id == role);
            await Press("RemoveRoleButton");
            Check(Choice() is { Granted: false, Inherited: false }, "Removed membership retained privileges.");
        }
        finally { admin.Close(); }

        var limited = new AdministrationWindow(scopes, reader, branch);
        limited.Show();
        try
        {
            await Ready(limited);
            void Denied() => Check(((TextBlock)limited.FindName("Status")).Text.Contains("لا تملك صلاحية"), "Missing access denial.");
            Denied();
            ((TextBox)limited.FindName("EntryName")).Text = "denied change";
            await Invoke(limited, "CreateButton");
            Denied();
            ((TabItem)limited.FindName("SecurityTab")).IsSelected = true;
            await Ready(limited);
            Denied();
            Check(!await db.Centers.AnyAsync(x => x.NameAr == "denied change"), "Denied mutation wrote a record.");
            Check(((ComboBox)limited.FindName("Users")).Items.Count == 0, "User directory leaked.");
        }
        finally { limited.Close(); }

        var security = metadata.ServiceProvider.GetRequiredService<ISecurityDirectory>();
        var readPermission = (await db.Permissions.SingleAsync(x => x.Code == PermissionCodes.JournalRead)).Id;
        await security.SetPermissionAsync(owner, reader, readPermission, false);
        await security.SetPermissionAsync(owner, reader, permission, true);
        var shell = new MainWindow(new MainWindowViewModel(new HisabTak.Application.ApplicationIdentity("HisabTak", "P08")),
            scopes, services.GetRequiredService<ILogger<MainWindow>>());
        shell.Show();
        try
        {
            ((TextBox)shell.FindName("Username")).Text = "ui-reader";
            ((PasswordBox)shell.FindName("Password")).Password = password;
            ((Button)shell.FindName("LoginButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!((FrameworkElement)shell.FindName("LoginPanel")).IsEnabled)
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("Administration login timed out.");
                await Task.Delay(20);
            }
            var tabs = (TabControl)shell.FindName("WorkTabs");
            Check(tabs.IsVisible && tabs.SelectedItem == shell.FindName("AdministrationTab") &&
                tabs.Items.OfType<TabItem>().Count(x => x.Visibility == Visibility.Visible) == 1,
                "Setup-only login must open administration without exposing accounting.");
        }
        finally { shell.Close(); }
    }

    private static async Task Invoke(Window window, string name)
    {
        var button = (Button)window.FindName(name);
        Check(button.IsEnabled && button.IsVisible, name + " unavailable");
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Ready(window);
    }
    private static async Task Ready(Window window)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!((TabControl)window.FindName("Tabs")).IsEnabled)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Administration UI operation timed out.");
            await Task.Delay(20);
        }
        window.UpdateLayout();
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
