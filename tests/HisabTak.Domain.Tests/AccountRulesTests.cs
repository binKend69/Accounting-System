using HisabTak.Domain.Accounting;

namespace HisabTak.Domain.Tests;

public sealed class AccountRulesTests
{
    [Theory]
    [InlineData(" 00121 ", "00121", 5)]
    [InlineData("\t11\r\n", "11", 2)]
    [InlineData("1", "1", 1)]
    public void Group_level_uses_the_stored_code_and_preserves_leading_zeroes(string input, string expected, int level)
    {
        var group = new AccountMainGroup(input, "مجموعة");
        Assert.Equal(expected, group.Code);
        Assert.Equal(level, group.Level);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Group_rejects_nonpositive_parent_references(int parentId) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new AccountMainGroup("11", "مجموعة", parentId));

    [Fact]
    public void Root_and_positive_parent_references_remain_supported()
    {
        Assert.Null(new AccountMainGroup("1", "رئيسية").ParentMainGroupId);
        Assert.Equal(7, new AccountMainGroup("11", "فرعية", 7).ParentMainGroupId);
    }

    [Fact]
    public void Active_account_accepts_its_original_transaction_currency()
    {
        var account = new Account("00121", "حساب", 1, 2, 1);
        account.EnsurePostingAllowed(2);
        Assert.Equal("00121", account.AccountCode);
    }

    [Fact]
    public void Account_rejects_another_currency_even_when_amounts_could_balance() =>
        Assert.Throws<InvalidOperationException>(() => new Account("121", "حساب", 1, 2, 1).EnsurePostingAllowed(1));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Account_rejects_invalid_transaction_currency(int currencyId) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Account("121", "حساب", 1, 2, 1).EnsurePostingAllowed(currencyId));

    [Fact]
    public void Inactive_account_rejects_posting()
    {
        var account = new Account("121", "حساب", 1, 1, 1);
        account.SetActive(false);
        Assert.Throws<InvalidOperationException>(() => account.EnsurePostingAllowed(1));
    }

    [Fact]
    public void Deleted_account_rejects_posting_even_when_active()
    {
        var account = new Account("121", "حساب", 1, 1, 1);
        account.MarkDeleted();
        Assert.True(account.IsActive);
        Assert.Throws<InvalidOperationException>(() => account.EnsurePostingAllowed(1));
    }

    [Fact]
    public void Materialized_suspended_account_rejects_posting()
    {
        var account = new Account("121", "حساب", 1, 1, 1);
        // EF materializes this persisted flag through its private setter.
        typeof(Account).GetProperty(nameof(Account.IsSuspended))!.SetValue(account, true);
        Assert.Throws<InvalidOperationException>(() => account.EnsurePostingAllowed(1));
    }

    [Fact]
    public void Restoring_a_deleted_account_does_not_override_its_inactive_state()
    {
        var account = new Account("121", "حساب", 1, 1, 1);
        account.SetActive(false);
        account.MarkDeleted();
        account.Restore();
        Assert.Throws<InvalidOperationException>(() => account.EnsurePostingAllowed(1));
        account.SetActive(true);
        account.EnsurePostingAllowed(1);
    }
}
