using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HisabTak.Application.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HisabTak.WPF.Views;

public partial class AdministrationWindow : Window
{
    private readonly IServiceScopeFactory scopes;
    private readonly int actor;
    private readonly int branch;
    private IReadOnlyList<DirectoryRow> rows = [];
    private IReadOnlyList<PermissionChoice> grants = [];
    private IReadOnlyList<PermissionChoice> roleGrants = [];
    private bool busy;
    private DirectoryKind CurrentKind => Kind.SelectedIndex == 0 ? DirectoryKind.Center : DirectoryKind.Department;

    public AdministrationWindow(IServiceScopeFactory scopes, int actor, int branch)
    {
        this.scopes = scopes;
        this.actor = actor;
        this.branch = branch;
        InitializeComponent();
        Loaded += async (_, _) => await Refresh();
        Closing += (_, e) => e.Cancel = busy;
    }

    private async Task Run(Func<IServiceProvider, Task> operation)
    {
        if (busy) return;
        busy = true;
        Tabs.IsEnabled = false;
        Status.Text = "جارٍ التحميل…";
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await operation(scope.ServiceProvider);
            Status.Text = "تم تحديث البيانات.";
        }
        catch (UnauthorizedAccessException) { Status.Text = "لا تملك صلاحية هذه العملية أو الوصول إلى السجل."; }
        catch (ArgumentException) { Status.Text = "أدخل اسمًا صالحًا واختر السجل المطلوب."; }
        catch (DirectoryConflictException ex) { Status.Text = ex.Message; }
        catch (Exception ex)
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetService<ILogger<AdministrationWindow>>()?.LogWarning("Administration operation failed ({Type}).", ex.GetType().Name);
            Status.Text = "تعذرت العملية. حدّث القائمة وتحقق من الاتصال ثم أعد المحاولة.";
        }
        finally { busy = false; Tabs.IsEnabled = true; }
    }

    private void Filter()
    {
        if (Records is null || Permissions is null || RolePermissions is null) return;
        var query = Search.Text.Trim();
        bool Match(string value) => value.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        Records.ItemsSource = rows.Where(x => (!x.Hidden || IncludeHidden.IsChecked == true) && Match(x.Name)).ToArray();
        RolePermissions.ItemsSource = roleGrants.Where(x => Match(x.Name) || Match(x.Module)).ToArray();
        Permissions.ItemsSource = grants.Where(x => Match(x.Name) || Match(x.Module)).ToArray();
    }

    private async Task LoadRows(IServiceProvider services)
    {
        rows = [];
        Filter();
        rows = await services.GetRequiredService<IReferenceDirectory>().ListAsync(actor, branch, CurrentKind);
        Filter();
    }
    private async Task LoadAccess(IServiceProvider services)
    {
        grants = [];
        Roles.ItemsSource = null;
        Filter();
        if (Users.SelectedItem is not SecuritySubject user) return;
        var access = await services.GetRequiredService<ISecurityDirectory>().AccessAsync(actor, user.Id);
        Roles.ItemsSource = access.Roles;
        grants = access.Permissions;
        Filter();
    }
    private Task Refresh() => Run(async services =>
    {
        if (RoleTab.IsSelected)
        {
            var id = (RoleList.SelectedItem as SecuritySubject)?.Id;
            RoleList.ItemsSource = null;
            await LoadRole(services);
            var roles = await services.GetRequiredService<ISecurityDirectory>().RolesAsync(actor);
            RoleList.ItemsSource = roles;
            RoleList.SelectedItem = roles.FirstOrDefault(x => x.Id == id) ?? roles.FirstOrDefault();
            await LoadRole(services);
            return;
        }
        if (!SecurityTab.IsSelected) { await LoadRows(services); return; }
        var selected = (Users.SelectedItem as SecuritySubject)?.Id;
        Users.ItemsSource = null;
        await LoadAccess(services);
        var users = await services.GetRequiredService<ISecurityDirectory>().UsersAsync(actor);
        Users.ItemsSource = users;
        Users.SelectedItem = users.FirstOrDefault(x => x.Id == selected) ?? users.FirstOrDefault();
        await LoadAccess(services);
    });

    private async Task LoadRole(IServiceProvider services)
    {
        roleGrants = [];
        Filter();
        if (RoleList.SelectedItem is not SecuritySubject role) return;
        roleGrants = await services.GetRequiredService<ISecurityDirectory>().RoleAccessAsync(actor, role.Id);
        Filter();
    }
    private async void Role_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !busy) await Run(LoadRole);
    }
    private Task ChangeRolePermission(bool granted) => Run(async services =>
    {
        if (RoleList.SelectedItem is not SecuritySubject role || RolePermissions.SelectedItem is not PermissionChoice permission)
            throw new ArgumentException();
        await services.GetRequiredService<ISecurityDirectory>().SetRolePermissionAsync(actor, role.Id, permission.Id, granted);
        await LoadRole(services);
    });
    private async void RoleGrant_Click(object sender, RoutedEventArgs e) => await ChangeRolePermission(true);
    private async void RoleRevoke_Click(object sender, RoutedEventArgs e) => await ChangeRolePermission(false);

    private async void Tab_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !busy && e.Source == Tabs) { Search.Clear(); await Refresh(); }
    }
    private async void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await Refresh();
    }
    private async void User_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !busy) await Run(LoadAccess);
    }
    private async void Refresh_Executed(object sender, ExecutedRoutedEventArgs e) => await Refresh();
    private void Find_Executed(object sender, ExecutedRoutedEventArgs e) { Search.Focus(); Search.SelectAll(); }
    private void Filter_Changed(object sender, RoutedEventArgs e) => Filter();
    private void Record_Changed(object sender, SelectionChangedEventArgs e)
    {
        var row = Records.SelectedItem as DirectoryRow;
        EntryName.Text = row?.Name ?? "";
        RenameButton.IsEnabled = HideButton.IsEnabled = row is { Hidden: false };
        RestoreButton.IsEnabled = row is { Hidden: true };
    }

    private Task Edit(DirectoryAction action) => Run(async services =>
    {
        var row = Records.SelectedItem as DirectoryRow;
        if (action != DirectoryAction.Create && row is null) throw new ArgumentException();
        await services.GetRequiredService<IReferenceDirectory>().ApplyAsync(actor, branch, CurrentKind, action, row, EntryName.Text);
        EntryName.Clear();
        await LoadRows(services);
    });
    private async void Create_Click(object sender, RoutedEventArgs e) => await Edit(DirectoryAction.Create);
    private async void Rename_Click(object sender, RoutedEventArgs e) => await Edit(DirectoryAction.Rename);
    private async void Hide_Click(object sender, RoutedEventArgs e) => await Edit(DirectoryAction.Hide);
    private async void Restore_Click(object sender, RoutedEventArgs e) => await Edit(DirectoryAction.Restore);

    private Task SetGrant(bool role, bool add) => Run(async services =>
    {
        if (Users.SelectedItem is not SecuritySubject user) throw new ArgumentException();
        var service = services.GetRequiredService<ISecurityDirectory>();
        if (role && Roles.SelectedItem is RoleChoice selectedRole)
            await service.SetRoleAsync(actor, user.Id, selectedRole.Id, add);
        else if (!role && Permissions.SelectedItem is PermissionChoice permission)
            await service.SetPermissionAsync(actor, user.Id, permission.Id, add);
        else throw new ArgumentException();
        await LoadAccess(services);
    });
    private async void Assign_Click(object sender, RoutedEventArgs e) => await SetGrant(true, true);
    private async void Remove_Click(object sender, RoutedEventArgs e) => await SetGrant(true, false);
    private async void Grant_Click(object sender, RoutedEventArgs e) => await SetGrant(false, true);
    private async void Revoke_Click(object sender, RoutedEventArgs e) => await SetGrant(false, false);
}
