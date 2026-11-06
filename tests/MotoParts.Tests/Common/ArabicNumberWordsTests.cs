using MotoParts.Core.Common;

namespace MotoParts.Tests.Common;

public sealed class ArabicNumberWordsTests
{
    [Theory]
    [InlineData(0, "صفر")]
    [InlineData(1, "واحد")]
    [InlineData(10, "عشرة")]
    [InlineData(11, "أحد عشر")]
    [InlineData(12, "اثنا عشر")]
    [InlineData(20, "عشرون")]
    [InlineData(25, "خمسة وعشرون")]
    [InlineData(100, "مائة")]
    [InlineData(200, "مائتان")]
    [InlineData(305, "ثلاثمائة وخمسة")]
    [InlineData(999, "تسعمائة وتسعة وتسعون")]
    [InlineData(1000, "ألف")]
    [InlineData(2000, "ألفان")]
    [InlineData(3000, "ثلاثة آلاف")]
    [InlineData(11000, "أحد عشر ألف")]
    [InlineData(1250, "ألف ومائتان وخمسون")]
    [InlineData(1000000, "مليون")]
    [InlineData(2500000, "مليونان وخمسمائة ألف")]
    public void Writes_integers(long number, string expected)
    {
        Assert.Equal(expected, ArabicNumberWords.ToWords(number));
    }

    [Fact]
    public void Writes_an_amount_with_currency_and_halalas()
    {
        Assert.Equal("فقط مائة وخمسة وعشرون ريالاً و50 هللة لا غير".Replace("50", "خمسون"),
            ArabicNumberWords.Amount(Money.Of(125.50m)));
    }

    [Fact]
    public void Writes_whole_amounts_and_fractions_alone()
    {
        Assert.Equal("فقط مائة ريال لا غير", ArabicNumberWords.Amount(Money.Of(100m)));
        Assert.Equal("فقط خمسة وسبعون هللة لا غير", ArabicNumberWords.Amount(Money.Of(0.75m)));
        Assert.Equal("فقط صفر ريال لا غير", ArabicNumberWords.Amount(Money.Zero));
    }
}
