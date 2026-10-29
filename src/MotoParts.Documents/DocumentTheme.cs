using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using MotoParts.Core.Common;
using MotoParts.Core.Services;

namespace MotoParts.Documents;

/// <summary>
/// Shared look for every printed document: Arabic, right to left, plain and
/// dense. Documents are filed and read on paper, so nothing costs ink without
/// earning it.
/// </summary>
public static class DocumentTheme
{
    /// <summary>
    /// Fonts with full Arabic coverage that ship with every supported Windows
    /// version, tried in order. No font is bundled or downloaded.
    /// </summary>
    public static readonly string[] Fonts = ["Segoe UI", "Tahoma", "Arial"];

    public const string Ink = "#111111";
    public const string Muted = "#555555";
    public const string Rule = "#BBBBBB";
    public const string Shade = "#F1F1F1";
    public const string Danger = "#B00020";

    private static int _configured;

    /// <summary>
    /// QuestPDF's Community licence covers businesses with annual revenue under
    /// USD 1M - a single motorcycle parts shop. Above that, a commercial licence
    /// is required (see README).
    /// </summary>
    public static void Configure()
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1)
        {
            return;
        }

        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.UseSystemFonts = true;
    }

    public static void ApplyPage(PageDescriptor page, PageSize size, float marginMm = 12)
    {
        page.Size(size);
        page.Margin(marginMm, Unit.Millimetre);
        page.ContentFromRightToLeft();
        page.PageColor(Colors.White);
        page.DefaultTextStyle(t => t.FontFamily(Fonts).FontSize(9.5f).FontColor(Ink));
    }

    /// <summary>Shop name and details on one side, the document title on the other.</summary>
    public static void ShopHeader(IContainer container, ShopProfile shop, string title, params (string Label, string? Value)[] facts)
    {
        container.PaddingBottom(6).BorderBottom(1).BorderColor(Ink).Row(row =>
        {
            row.RelativeItem(3).Column(col =>
            {
                col.Item().Text(shop.Name).FontSize(15).Bold();

                if (!string.IsNullOrWhiteSpace(shop.Address))
                {
                    col.Item().Text(shop.Address).FontColor(Muted);
                }

                var contact = new List<string>();

                if (!string.IsNullOrWhiteSpace(shop.Phone))
                {
                    contact.Add($"هاتف: {Ltr(shop.Phone)}");
                }

                if (!string.IsNullOrWhiteSpace(shop.TaxNumber))
                {
                    contact.Add($"الرقم الضريبي: {Ltr(shop.TaxNumber)}");
                }

                if (!string.IsNullOrWhiteSpace(shop.CommercialRegister))
                {
                    contact.Add($"س.ت: {Ltr(shop.CommercialRegister)}");
                }

                if (contact.Count > 0)
                {
                    col.Item().Text(string.Join("   |   ", contact)).FontColor(Muted).FontSize(8.5f);
                }
            });

            row.RelativeItem(2).AlignLeft().Column(col =>
            {
                col.Item().AlignLeft().Text(title).FontSize(14).Bold();

                foreach (var (label, value) in facts.Where(f => !string.IsNullOrWhiteSpace(f.Value)))
                {
                    col.Item().AlignLeft().Text(text =>
                    {
                        if (!string.IsNullOrEmpty(label))
                        {
                            text.Span($"{label}: ").FontColor(Muted);
                        }

                        text.Span(Bidi(value)).SemiBold();
                    });
                }
            });
        });
    }

    public static void Footer(IContainer container, string? note = null)
    {
        container.PaddingTop(6).BorderTop(0.5f).BorderColor(Rule).Row(row =>
        {
            row.RelativeItem().Text(note ?? "").FontSize(8).FontColor(Muted);
            row.AutoItem().Text(text =>
            {
                text.DefaultTextStyle(t => t.FontSize(8).FontColor(Muted));
                text.Span("صفحة ");
                text.CurrentPageNumber();
                text.Span(" من ");
                text.TotalPages();
                text.Span($"   |   طُبع في {DateTime(System.DateTime.Now)}");
            });
        });
    }

    /// <summary>A shaded table header cell.</summary>
    public static IContainer HeaderCell(IContainer cell) =>
        cell.Background(Shade).BorderBottom(1).BorderColor(Ink).PaddingVertical(4).PaddingHorizontal(3);

    /// <summary>A body cell with a hairline under it.</summary>
    public static IContainer BodyCell(IContainer cell) =>
        cell.BorderBottom(0.5f).BorderColor(Rule).PaddingVertical(3).PaddingHorizontal(3);

    public static IContainer TotalCell(IContainer cell) =>
        cell.BorderTop(1).BorderColor(Ink).PaddingVertical(4).PaddingHorizontal(3);

    // --- Bidirectional text -------------------------------------------------------
    //
    // Numbers, dates and document codes are left-to-right islands inside Arabic
    // text. Left to the bidi algorithm, "2026-06-15" renders as "15-06-2026",
    // "-3.50" as "3.50-", and "RCV-1: +150" scrambles. Wrapping each in a
    // left-to-right isolate (U+2066 ... U+2069) keeps it intact and in place.

    public static string Ltr(string text) => $"⁦{text}⁩";

    /// <summary>Isolates text that contains no Arabic, so a whole cell of digits or Latin stays in order.</summary>
    public static string Bidi(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        return text.Any(c => c is >= '؀' and <= 'ۿ') ? text : Ltr(text);
    }

    public static string Amount(Money money) => Ltr(money.ToDisplay());

    public static string Amount(Money? money) => money is { } m ? Amount(m) : "-";

    public static string Qty(Quantity quantity) => Ltr(quantity.ToDisplay());

    /// <summary>"2 طقم": the number isolated, then the Arabic unit.</summary>
    public static string Qty(Quantity quantity, string unit) => $"{Qty(quantity)} {unit}";

    public static string Date(DateOnly date) => Ltr(date.ToString("yyyy-MM-dd"));

    public static string Date(DateTime dateTime) => Ltr(dateTime.ToString("yyyy-MM-dd"));

    public static string DateTime(DateTime dateTime) => Ltr(dateTime.ToString("yyyy-MM-dd HH:mm"));

    public static string Time(DateTime dateTime) => Ltr(dateTime.ToString("HH:mm"));

    /// <summary>A label/value pair in a totals box.</summary>
    public static void TotalLine(ColumnDescriptor col, string label, string value, bool emphasise = false)
    {
        col.Item().PaddingVertical(1.5f).Row(row =>
        {
            var labelText = row.RelativeItem().Text(label);
            var valueText = row.AutoItem().Text(Bidi(value));

            if (emphasise)
            {
                labelText.Bold().FontSize(11);
                valueText.Bold().FontSize(11);
            }
        });
    }

    /// <summary>A plain bordered note box, e.g. the amount in words.</summary>
    public static void NoteBox(IContainer container, string text, bool strong = false)
    {
        var t = container.Border(0.75f).BorderColor(Rule).Background(Shade).Padding(5).Text(text);

        if (strong)
        {
            t.SemiBold();
        }
    }

    public static void SignatureLines(IContainer container, params string[] roles)
    {
        container.PaddingTop(28).Row(row =>
        {
            foreach (var role in roles)
            {
                row.RelativeItem().PaddingHorizontal(10).Column(col =>
                {
                    col.Item().BorderBottom(0.75f).BorderColor(Ink).Height(18);
                    col.Item().PaddingTop(2).AlignCenter().Text(role).FontColor(Muted);
                });
            }
        });
    }
}
