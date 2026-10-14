using System.Text;

namespace MotoParts.Core.Common;

/// <summary>
/// Writes an amount in Arabic words (التفقيط), as printed on invoices and
/// vouchers: "فقط مائة وخمسة وعشرون ريالاً و50 هللة لا غير".
///
/// Follows the conventions used on Saudi commercial documents: compound numbers
/// read units-before-tens ("خمسة وعشرون"), thousands and millions take their
/// dual and plural forms (ألفان، ثلاثة آلاف), and parts are joined with "و".
/// </summary>
public static class ArabicNumberWords
{
    private static readonly string[] Ones =
        ["", "واحد", "اثنان", "ثلاثة", "أربعة", "خمسة", "ستة", "سبعة", "ثمانية", "تسعة"];

    private static readonly string[] Teens =
        ["عشرة", "أحد عشر", "اثنا عشر", "ثلاثة عشر", "أربعة عشر", "خمسة عشر", "ستة عشر", "سبعة عشر", "ثمانية عشر", "تسعة عشر"];

    private static readonly string[] Tens =
        ["", "", "عشرون", "ثلاثون", "أربعون", "خمسون", "ستون", "سبعون", "ثمانون", "تسعون"];

    private static readonly string[] Hundreds =
        ["", "مائة", "مائتان", "ثلاثمائة", "أربعمائة", "خمسمائة", "ستمائة", "سبعمائة", "ثمانمائة", "تسعمائة"];

    /// <summary>An integer in words. Zero is "صفر".</summary>
    public static string ToWords(long number)
    {
        if (number == 0)
        {
            return "صفر";
        }

        if (number < 0)
        {
            return "سالب " + ToWords(-number);
        }

        var parts = new List<string>();

        var billions = number / 1_000_000_000;
        var millions = number / 1_000_000 % 1000;
        var thousands = number / 1000 % 1000;
        var rest = number % 1000;

        if (billions > 0)
        {
            parts.Add(Scale(billions, "مليار", "ملياران", "مليارات"));
        }

        if (millions > 0)
        {
            parts.Add(Scale(millions, "مليون", "مليونان", "ملايين"));
        }

        if (thousands > 0)
        {
            parts.Add(Scale(thousands, "ألف", "ألفان", "آلاف"));
        }

        if (rest > 0)
        {
            parts.Add(UpToThousand(rest));
        }

        return string.Join(" و", parts);
    }

    /// <summary>
    /// "فقط ألف ومائتان وخمسون ريالاً و75 هللة لا غير".
    /// </summary>
    public static string Amount(Money amount, string currency = "ريال", string fraction = "هللة")
    {
        var absolute = amount.Abs();
        var whole = decimal.ToInt64(decimal.Truncate(absolute.Amount));
        var minor = decimal.ToInt32((absolute.Amount - whole) * 100m);

        var text = new StringBuilder("فقط ");

        if (whole > 0 || minor == 0)
        {
            text.Append(ToWords(whole)).Append(' ').Append(CurrencyForm(whole, currency));
        }

        if (minor > 0)
        {
            if (whole > 0)
            {
                text.Append(" و");
            }

            text.Append(ToWords(minor)).Append(' ').Append(fraction);
        }

        text.Append(" لا غير");
        return text.ToString();
    }

    private static string Scale(long count, string single, string dual, string plural) => count switch
    {
        1 => single,
        2 => dual,
        >= 3 and <= 10 => $"{UpToThousand(count)} {plural}",
        _ => $"{UpToThousand(count)} {single}",
    };

    private static string UpToThousand(long number)
    {
        var parts = new List<string>();
        var hundreds = number / 100;
        var rest = number % 100;

        if (hundreds > 0)
        {
            parts.Add(Hundreds[hundreds]);
        }

        if (rest > 0)
        {
            parts.Add(UpToHundred(rest));
        }

        return string.Join(" و", parts);
    }

    private static string UpToHundred(long number)
    {
        if (number < 10)
        {
            return Ones[number];
        }

        if (number < 20)
        {
            return Teens[number - 10];
        }

        var ones = number % 10;
        var tens = Tens[number / 10];

        return ones == 0 ? tens : $"{Ones[ones]} و{tens}";
    }

    /// <summary>The accusative "ريالاً" after 11-99, as written on vouchers.</summary>
    private static string CurrencyForm(long whole, string currency)
    {
        if (currency != "ريال")
        {
            return currency;
        }

        var lastTwo = whole % 100;
        return lastTwo is >= 11 and <= 99 ? "ريالاً" : "ريال";
    }
}
