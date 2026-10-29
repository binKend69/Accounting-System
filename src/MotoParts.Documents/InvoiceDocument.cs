using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Services;

namespace MotoParts.Documents;

public enum InvoiceFormat
{
    /// <summary>Full page, for files and business customers.</summary>
    A4,

    /// <summary>80 mm thermal roll, for the counter.</summary>
    Receipt80mm,
}

public sealed record InvoiceData(
    ShopProfile Shop,
    Sale Sale,
    IReadOnlyList<SaleItem> Items,
    IReadOnlyList<SaleRefund> Refunds,
    string SellerName,
    Customer? Customer);

/// <summary>
/// The sales invoice. A customer with a VAT number gets a full tax invoice;
/// everyone else a simplified tax invoice, which is what a counter sale is.
/// </summary>
public sealed class InvoiceDocument : IDocument
{
    private readonly InvoiceData _data;
    private readonly InvoiceFormat _format;

    public InvoiceDocument(InvoiceData data, InvoiceFormat format)
    {
        _data = data;
        _format = format;
    }

    private Sale Sale => _data.Sale;

    private string Title => string.IsNullOrWhiteSpace(_data.Customer?.TaxNumber) ? "فاتورة ضريبية مبسطة" : "فاتورة ضريبية";

    public DocumentMetadata GetMetadata() => new() { Title = $"{Title} {Sale.InvoiceNumber}", Author = _data.Shop.Name };

    public void Compose(IDocumentContainer container)
    {
        if (_format == InvoiceFormat.Receipt80mm)
        {
            ComposeReceipt(container);
            return;
        }

        container.Page(page =>
        {
            DocumentTheme.ApplyPage(page, PageSizes.A4);

            page.Header().Element(c => DocumentTheme.ShopHeader(c, _data.Shop, Title,
                ("رقم الفاتورة", Sale.InvoiceNumber),
                ("التاريخ", Sale.SoldAt.ToString("yyyy-MM-dd HH:mm")),
                ("البائع", _data.SellerName)));

            page.Content().PaddingTop(10).Column(col =>
            {
                col.Spacing(8);

                col.Item().Row(row =>
                {
                    row.RelativeItem().Element(c => Party(c));
                    row.ConstantItem(12);
                    row.RelativeItem().Element(c => Payment(c));
                });

                col.Item().Element(LinesTable);
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(words =>
                    {
                        words.Item().Element(c => DocumentTheme.NoteBox(c, ArabicNumberWords.Amount(Sale.Total), strong: true));

                        if (Sale.PricesIncludeTax && Sale.TaxTotal.IsPositive)
                        {
                            words.Item().PaddingTop(4).Text("الأسعار شاملة ضريبة القيمة المضافة.").FontColor(DocumentTheme.Muted);
                        }
                    });
                    row.ConstantItem(12);
                    row.ConstantItem(220).Element(Totals);
                });

                if (_data.Refunds.Count > 0)
                {
                    col.Item().Element(RefundsSection);
                }

                if (!string.IsNullOrWhiteSpace(Sale.Notes))
                {
                    col.Item().Text($"ملاحظات: {Sale.Notes}");
                }
            });

            page.Footer().Element(c => DocumentTheme.Footer(c, _data.Shop.InvoiceFooter));
        });
    }

    private void Party(IContainer container)
    {
        container.Border(0.75f).BorderColor(DocumentTheme.Rule).Padding(6).Column(col =>
        {
            col.Item().Text("العميل").FontColor(DocumentTheme.Muted);

            if (_data.Customer is null)
            {
                col.Item().Text("عميل نقدي").SemiBold();
                return;
            }

            col.Item().Text(_data.Customer.Name).SemiBold();

            if (!string.IsNullOrWhiteSpace(_data.Customer.TaxNumber))
            {
                col.Item().Text($"الرقم الضريبي: {DocumentTheme.Ltr(_data.Customer.TaxNumber!)}");
            }

            if (!string.IsNullOrWhiteSpace(_data.Customer.Phone))
            {
                col.Item().Text($"هاتف: {DocumentTheme.Ltr(_data.Customer.Phone!)}");
            }

            if (!string.IsNullOrWhiteSpace(_data.Customer.Address))
            {
                col.Item().Text(_data.Customer.Address);
            }
        });
    }

    private void Payment(IContainer container)
    {
        container.Border(0.75f).BorderColor(DocumentTheme.Rule).Padding(6).Column(col =>
        {
            col.Item().Text("الدفع").FontColor(DocumentTheme.Muted);
            col.Item().Text(Sale.PaymentMethod.ToArabic()).SemiBold();

            if (Sale.PaymentMethod == PaymentMethod.Cash)
            {
                col.Item().Text($"المدفوع: {DocumentTheme.Amount(Sale.AmountPaid)}   المتبقي للعميل: {DocumentTheme.Amount(Sale.ChangeDue)}");
            }

            if (Sale.PaymentMethod == PaymentMethod.Credit)
            {
                col.Item().Text("تُقيَّد على حساب العميل").FontColor(DocumentTheme.Muted);
            }

            if (Sale.Status != SaleStatus.Completed)
            {
                col.Item().Text(Sale.Status.ToArabic()).FontColor(DocumentTheme.Danger).SemiBold();
            }
        });
    }

    private void LinesTable(IContainer container)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(22);
                c.ConstantColumn(70);
                c.RelativeColumn(4);
                c.ConstantColumn(50);
                c.ConstantColumn(65);
                c.ConstantColumn(55);
                c.ConstantColumn(75);
            });

            table.Header(h =>
            {
                foreach (var title in new[] { "#", "الرمز", "الصنف", "الكمية", "سعر الوحدة", "الخصم", "الإجمالي" })
                {
                    h.Cell().Element(DocumentTheme.HeaderCell).Text(title).SemiBold();
                }
            });

            var index = 0;

            foreach (var item in _data.Items)
            {
                index++;
                table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Ltr(index.ToString()));
                table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Ltr(item.Sku)).FontSize(8.5f);
                table.Cell().Element(DocumentTheme.BodyCell).Column(c =>
                {
                    c.Item().Text(item.Name);

                    if (item.RefundedQuantity.IsPositive)
                    {
                        c.Item().Text($"مرتجع: {DocumentTheme.Qty(item.RefundedQuantity)}").FontSize(8).FontColor(DocumentTheme.Danger);
                    }
                });
                table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Qty(item.Quantity, item.Unit));
                table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Amount(item.UnitPrice));
                table.Cell().Element(DocumentTheme.BodyCell).Text(item.Discount.IsZero ? "-" : DocumentTheme.Amount(item.Discount));
                table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Amount(item.LineTotal)).SemiBold();
            }
        });
    }

    private void Totals(IContainer container)
    {
        container.Border(0.75f).BorderColor(DocumentTheme.Ink).Padding(6).Column(col =>
        {
            var currency = _data.Shop.CurrencySymbol;
            var ratePercent = $"{Sale.TaxRate * 100m:0.##}%";

            DocumentTheme.TotalLine(col, "المجموع", DocumentTheme.Amount(Sale.Subtotal));

            if (Sale.DiscountTotal.IsPositive)
            {
                DocumentTheme.TotalLine(col, "الخصم", "- " + DocumentTheme.Amount(Sale.DiscountTotal));
            }

            if (Sale.PricesIncludeTax)
            {
                DocumentTheme.TotalLine(col, "الإجمالي قبل الضريبة", DocumentTheme.Amount(Sale.Revenue));
                DocumentTheme.TotalLine(col, $"ضريبة القيمة المضافة {ratePercent} (مشمولة)", DocumentTheme.Amount(Sale.TaxTotal));
            }
            else
            {
                DocumentTheme.TotalLine(col, "الإجمالي قبل الضريبة", DocumentTheme.Amount(Sale.NetOfDiscount));
                DocumentTheme.TotalLine(col, $"ضريبة القيمة المضافة {ratePercent}", DocumentTheme.Amount(Sale.TaxTotal));
            }

            col.Item().PaddingTop(3).BorderTop(1).BorderColor(DocumentTheme.Ink);
            DocumentTheme.TotalLine(col, $"الإجمالي المستحق ({currency})", DocumentTheme.Amount(Sale.Total), emphasise: true);
        });
    }

    private void RefundsSection(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().Text("المرتجعات على هذه الفاتورة").Bold();

            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(110);
                    c.ConstantColumn(80);
                    c.RelativeColumn();
                    c.ConstantColumn(80);
                });

                foreach (var refund in _data.Refunds)
                {
                    table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Ltr(refund.RefundNumber));
                    table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Date(refund.RefundDate));
                    table.Cell().Element(DocumentTheme.BodyCell).Text(refund.Reason);
                    table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Amount(refund.Total));
                }
            });

            col.Item().PaddingTop(3).Text(
                $"إجمالي المرتجع {DocumentTheme.Amount(Sale.RefundedTotal)}   |   صافي الفاتورة {DocumentTheme.Amount(Sale.Total - Sale.RefundedTotal)}")
                .SemiBold();
        });
    }

    // --- 80 mm receipt ----------------------------------------------------------

    private void ComposeReceipt(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.ContinuousSize(80, Unit.Millimetre);
            page.Margin(3, Unit.Millimetre);
            page.ContentFromRightToLeft();
            page.DefaultTextStyle(t => t.FontFamily(DocumentTheme.Fonts).FontSize(8.5f));

            page.Content().Column(col =>
            {
                col.Spacing(2);

                col.Item().AlignCenter().Text(_data.Shop.Name).Bold().FontSize(11);

                if (!string.IsNullOrWhiteSpace(_data.Shop.Address))
                {
                    col.Item().AlignCenter().Text(_data.Shop.Address);
                }

                if (!string.IsNullOrWhiteSpace(_data.Shop.Phone))
                {
                    col.Item().AlignCenter().Text($"هاتف: {DocumentTheme.Ltr(_data.Shop.Phone!)}");
                }

                if (!string.IsNullOrWhiteSpace(_data.Shop.TaxNumber))
                {
                    col.Item().AlignCenter().Text($"الرقم الضريبي: {DocumentTheme.Ltr(_data.Shop.TaxNumber!)}");
                }

                col.Item().PaddingVertical(2).AlignCenter().Text(Title).SemiBold();
                col.Item().Text($"رقم: {DocumentTheme.Ltr(Sale.InvoiceNumber)}");
                col.Item().Text($"التاريخ: {DocumentTheme.DateTime(Sale.SoldAt)}");
                col.Item().Text($"البائع: {_data.SellerName}");

                if (_data.Customer is not null)
                {
                    col.Item().Text($"العميل: {_data.Customer.Name}");
                }

                col.Item().PaddingVertical(2).LineHorizontal(0.75f);

                foreach (var item in _data.Items)
                {
                    col.Item().Text(item.Name).SemiBold();
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Text($"{DocumentTheme.Qty(item.Quantity)} × {DocumentTheme.Amount(item.UnitPrice)}");

                        if (item.Discount.IsPositive)
                        {
                            row.RelativeItem().Text($"خصم {DocumentTheme.Amount(item.Discount)}");
                        }

                        row.AutoItem().Text(DocumentTheme.Amount(item.LineTotal));
                    });
                }

                col.Item().PaddingVertical(2).LineHorizontal(0.75f);

                void Line(string label, Money value, bool bold = false)
                {
                    col.Item().Row(row =>
                    {
                        var l = row.RelativeItem().Text(label);
                        var v = row.AutoItem().Text(DocumentTheme.Amount(value));

                        if (bold)
                        {
                            l.Bold().FontSize(10);
                            v.Bold().FontSize(10);
                        }
                    });
                }

                if (Sale.DiscountTotal.IsPositive)
                {
                    Line("الخصم", Sale.DiscountTotal);
                }

                Line(Sale.PricesIncludeTax ? "الضريبة (مشمولة)" : "الضريبة", Sale.TaxTotal);
                Line($"الإجمالي ({_data.Shop.CurrencySymbol})", Sale.Total, bold: true);
                col.Item().Text($"طريقة الدفع: {Sale.PaymentMethod.ToArabic()}");

                if (Sale.PaymentMethod == PaymentMethod.Cash)
                {
                    Line("المدفوع", Sale.AmountPaid);
                    Line("المتبقي", Sale.ChangeDue);
                }

                if (!string.IsNullOrWhiteSpace(_data.Shop.InvoiceFooter))
                {
                    col.Item().PaddingTop(4).AlignCenter().Text(_data.Shop.InvoiceFooter).FontSize(7.5f);
                }
            });
        });
    }
}
