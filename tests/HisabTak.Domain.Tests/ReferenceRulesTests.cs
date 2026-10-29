using HisabTak.Domain.Accounting;
using HisabTak.Domain.Security;
using HisabTak.Domain.Setup;

namespace HisabTak.Domain.Tests;

public sealed class ReferenceRulesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("123")]
    [InlineData("USD$")]
    public void Currency_requires_a_three_letter_code(string code) =>
        Assert.ThrowsAny<ArgumentException>(() => new Currency(code, "دولار", "$"));

    [Fact]
    public void New_currency_has_no_fabricated_exchange_rate_or_base_selection()
    {
        var currency = new Currency("usd", "دولار", "$");
        Assert.Equal("USD", currency.Code);
        Assert.Null(currency.ExchangeRate);
        Assert.False(currency.IsBaseCurrency);
    }

    [Fact]
    public void User_cannot_accept_plaintext_as_password_hash() =>
        Assert.Throws<ArgumentException>(() => new User("owner", "plaintext-is-not-a-hash"));

    [Fact]
    public void Fiscal_period_cannot_end_before_it_starts() =>
        Assert.Throws<ArgumentException>(() => new FiscalPeriod("اختبار", new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1)));

    [Fact]
    public void Group_codes_preserve_textual_hierarchy()
    {
        var group = new AccountMainGroup("1211", "مجموعة");
        Assert.Equal("1211", group.Code);
        Assert.Throws<ArgumentException>(() => new AccountMainGroup("12A", "مجموعة"));
    }
}
