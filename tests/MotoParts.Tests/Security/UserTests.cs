using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;
using MotoParts.Core.Services;
using MotoParts.Tests.Support;
using Xunit;

namespace MotoParts.Tests.Security;

/// <summary>Accounts, sign-in and the rules that keep the shop administrable.</summary>
public sealed class UserTests : TestBase
{
    private static UserInput Input(string username, params string[] roles) =>
        new(username, $"موظف {username}", null, null, null, Money.Of(4000), roles.Length == 0 ? [RoleName.Salesperson] : roles);

    [Fact]
    public void First_owner_can_only_be_created_once()
    {
        App.Users.CreateFirstOwner("boss", "المالك", "owner123");

        Assert.Throws<BusinessRuleException>(() => App.Users.CreateFirstOwner("intruder", "دخيل", "owner123"));
    }

    [Fact]
    public void Sign_in_succeeds_with_the_right_password_and_fails_quietly_otherwise()
    {
        var user = App.Users.Create(Input("khaled"), "pass1234", Admin);

        Assert.NotNull(App.Users.Authenticate("khaled", "pass1234"));
        Assert.NotNull(App.Users.Authenticate("  khaled ", "pass1234"));
        Assert.Null(App.Users.Authenticate("khaled", "wrong-pass"));
        Assert.Null(App.Users.Authenticate("nobody", "pass1234"));
        Assert.Null(App.Users.Authenticate("khaled", ""));

        var actions = App.Audit.Search(null, null, null).Where(a => a.AuditableType == nameof(User)).Select(a => a.Action).ToList();
        // A blank password is turned away before any lookup, so only the two real attempts are logged.
        Assert.Equal(2, actions.Count(a => a == AuditAction.LoginFailed));
        Assert.Equal(2, actions.Count(a => a == AuditAction.LoggedIn));
        Assert.Equal(Clock.Now, App.Users.Get(user.Id).LastLoginAt);
    }

    [Fact]
    public void Accounts_created_by_the_owner_must_change_their_password_at_first_sign_in()
    {
        var user = App.Users.Create(Input("sara"), "temp1234", Admin);
        Assert.True(App.Users.Get(user.Id).MustChangePassword);

        var actor = App.Users.Authenticate("sara", "temp1234")!;
        App.Users.ChangeOwnPassword(actor, "temp1234", "mine5678");

        Assert.False(App.Users.Get(user.Id).MustChangePassword);
        Assert.Null(App.Users.Authenticate("sara", "temp1234"));
        Assert.NotNull(App.Users.Authenticate("sara", "mine5678"));
    }

    [Fact]
    public void Changing_your_own_password_requires_the_current_one()
    {
        App.Users.Create(Input("ali"), "first123", Admin);
        var actor = App.Users.Authenticate("ali", "first123")!;

        Assert.Throws<BusinessRuleException>(() => App.Users.ChangeOwnPassword(actor, "guess123", "second12"));
        Assert.Throws<BusinessRuleException>(() => App.Users.ChangeOwnPassword(actor, "first123", "123"));
        Assert.NotNull(App.Users.Authenticate("ali", "first123"));
    }

    [Fact]
    public void A_password_reset_forces_a_change_at_next_sign_in()
    {
        var user = App.Users.Create(Input("omar"), "first123", Admin);
        var actor = App.Users.Authenticate("omar", "first123")!;
        App.Users.ChangeOwnPassword(actor, "first123", "own12345");

        App.Users.ResetPassword(user.Id, "reset123", Admin);

        Assert.True(App.Users.Get(user.Id).MustChangePassword);
        Assert.NotNull(App.Users.Authenticate("omar", "reset123"));
    }

    [Fact]
    public void Only_the_owner_manages_accounts()
    {
        var accountant = Accountant();
        var target = App.Users.Create(Input("fahad"), "pass1234", Admin);

        Assert.Throws<AccessDeniedException>(() => App.Users.Create(Input("x1"), "pass1234", accountant));
        Assert.Throws<AccessDeniedException>(() => App.Users.Update(target.Id, Input("fahad", RoleName.Owner), accountant));
        Assert.Throws<AccessDeniedException>(() => App.Users.ResetPassword(target.Id, "pass9999", accountant));
        Assert.Throws<AccessDeniedException>(() => App.Users.SetActive(target.Id, false, accountant));
    }

    [Fact]
    public void A_deactivated_account_cannot_sign_in_and_keeps_its_records()
    {
        var user = App.Users.Create(Input("nasser"), "pass1234", Admin);

        App.Users.SetActive(user.Id, false, Admin);

        Assert.Null(App.Users.Authenticate("nasser", "pass1234"));
        Assert.False(App.Users.Get(user.Id).IsActive);
        Assert.Contains(App.Users.All(), u => u.Id == user.Id);
        Assert.DoesNotContain(App.Users.All(activeOnly: true), u => u.Id == user.Id);

        App.Users.SetActive(user.Id, true, Admin);
        Assert.NotNull(App.Users.Authenticate("nasser", "pass1234"));
    }

    [Fact]
    public void An_employee_with_an_open_till_cannot_be_deactivated()
    {
        var seller = Salesperson();
        OpenTill(seller);

        var ex = Assert.Throws<BusinessRuleException>(() => App.Users.SetActive(seller.Id, false, Admin));
        Assert.Contains("وردية مفتوحة", ex.Message);
    }

    [Fact]
    public void The_shop_always_keeps_an_active_owner()
    {
        var owner = Admin;
        var second = App.Users.Create(Input("partner", RoleName.Owner), "pass1234", owner);

        // Nobody can switch themselves off or drop their own owner role...
        Assert.Throws<BusinessRuleException>(() => App.Users.SetActive(owner.Id, false, owner));
        Assert.Throws<BusinessRuleException>(() => App.Users.Update(owner.Id, Input(owner.Username, RoleName.Accountant), owner));

        // ...and with two owners, one may retire the other, but not the last one.
        App.Users.SetActive(second.Id, false, owner);
        var secondActor = App.Users.LoadActor(second.Id);
        Assert.Throws<BusinessRuleException>(() => App.Users.SetActive(owner.Id, false, secondActor));
    }

    [Fact]
    public void Usernames_are_unique_and_validated()
    {
        App.Users.Create(Input("hamad"), "pass1234", Admin);

        Assert.Throws<BusinessRuleException>(() => App.Users.Create(Input("hamad"), "pass1234", Admin));
        Assert.Throws<BusinessRuleException>(() => App.Users.Create(Input("two words"), "pass1234", Admin));
        Assert.Throws<BusinessRuleException>(() => App.Users.Create(Input("short"), "123", Admin));
        Assert.Throws<BusinessRuleException>(() => App.Users.Create(Input("noroles") with { Roles = [] }, "pass1234", Admin));
        Assert.Throws<BusinessRuleException>(() => App.Users.Create(Input("bad", "superuser"), "pass1234", Admin));
    }

    [Fact]
    public void Roles_decide_permissions()
    {
        var seller = App.Users.LoadActor(App.Users.Create(Input("s1", RoleName.Salesperson), "pass1234", Admin).Id);
        var accountant = App.Users.LoadActor(App.Users.Create(Input("a1", RoleName.Accountant), "pass1234", Admin).Id);

        Assert.True(seller.Can(Permission.SaleCreate));
        Assert.False(seller.Can(Permission.AccountingView));
        Assert.False(seller.Can(Permission.UserManage));
        Assert.True(accountant.Can(Permission.AccountingView));
        Assert.False(accountant.Can(Permission.UserManage));
        Assert.True(Admin.Can(Permission.UserManage));
    }

    [Fact]
    public void Editing_a_user_updates_details_and_roles_with_an_audit_record()
    {
        var user = App.Users.Create(Input("majed"), "pass1234", Admin);

        App.Users.Update(user.Id, new UserInput("majed", "ماجد السبيعي", "E-010", "0550000000", new DateOnly(2026, 1, 1), Money.Of(5200),
            [RoleName.Salesperson, RoleName.Accountant]), Admin);

        var after = App.Users.Get(user.Id);
        Assert.Equal("ماجد السبيعي", after.FullName);
        Assert.Equal(Money.Of(5200), after.BaseSalary);
        Assert.Equal(new DateOnly(2026, 1, 1), after.HireDate);
        Assert.Equal([RoleName.Accountant, RoleName.Salesperson], App.Users.RolesOf(user.Id).Order());
        Assert.Contains(App.Audit.For(nameof(User), user.Id), a => a.Action == AuditAction.Updated);
    }
}
