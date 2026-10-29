using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using MotoParts.Core.Common;
using MotoParts.Core.Domain;
using MotoParts.Core.Services;

namespace MotoParts.Documents;

// --- Refund note ---------------------------------------------------------------

public sealed record RefundData(
    ShopProfile Shop,
    SaleRefund Refund,
    Sale Sale,
    IReadOnlyList<(SaleRefundItem Line, SaleItem Original)> Lines,
    string UserName,
    Customer? Customer);

/// <summary>إشعار دائن: the refund note, referencing the original invoice.</summary>
public sealed class RefundDocument : IDocument
{
    private readonly RefundData _data;

    public RefundDocument(RefundData data) => _data = data;

    public DocumentMetadata GetMetadata() => new() { Title = $"إشعار مرتجع {_data.Refund.RefundNumber}" };

    public void Compose(IDocumentContainer container)
    {
        var refund = _data.Refund;

        container.Page(page =>
        {
            DocumentTheme.ApplyPage(page, PageSizes.A5.Landscape());

            page.Header().Element(c => DocumentTheme.ShopHeader(c, _data.Shop, "إشعار مرتجع مبيعات",
                ("رقم الإشعار", refund.RefundNumber),
                ("الفاتورة الأصلية", _data.Sale.InvoiceNumber),
                ("التاريخ", refund.RefundedAt.ToString("yyyy-MM-dd HH:mm"))));

            page.Content().PaddingTop(8).Column(col =>
            {
                col.Spacing(6);

                col.Item().Text(text =>
                {
                    text.Span("العميل: ").FontColor(DocumentTheme.Muted);
                    text.Span(_data.Customer?.Name ?? "عميل نقدي").SemiBold();
                    text.Span("     طريقة الرد: ").FontColor(DocumentTheme.Muted);
                    text.Span(refund.PaymentMethod.ToArabic()).SemiBold();
                    text.Span("     الموظف: ").FontColor(DocumentTheme.Muted);
                    text.Span(_data.UserName).SemiBold();
                });

                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(70);
                        c.RelativeColumn();
                        c.ConstantColumn(55);
                        c.ConstantColumn(70);
                        c.ConstantColumn(70);
                    });

                    table.Header(h =>
                    {
                        foreach (var title in new[] { "الرمز", "الصنف", "الكمية", "أُعيد للمخزون", "المبلغ" })
                        {
                            h.Cell().Element(DocumentTheme.HeaderCell).Text(title).SemiBold();
                        }
                    });

                    foreach (var (line, original) in _data.Lines)
                    {
                        table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Ltr(original.Sku));
                        table.Cell().Element(DocumentTheme.BodyCell).Text(original.Name);
                        table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Qty(line.Quantity));
                        table.Cell().Element(DocumentTheme.BodyCell).Text(line.Restocked ? "نعم" : "لا (تالف)");
                        table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Amount(line.LineTotal));
                    }
                });

                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text($"السبب: {refund.Reason}");
                        c.Item().PaddingTop(4).Element(x => DocumentTheme.NoteBox(x, ArabicNumberWords.Amount(refund.Total), strong: true));
                    });
                    row.ConstantItem(12);
                    row.ConstantItem(190).Border(0.75f).Padding(5).Column(c =>
                    {
                        DocumentTheme.TotalLine(c, "قيمة الأصناف", DocumentTheme.Amount(refund.Subtotal));
                        DocumentTheme.TotalLine(c, refund.PricesIncludeTax ? "الضريبة (مشمولة)" : "الضريبة", DocumentTheme.Amount(refund.TaxTotal));
                        DocumentTheme.TotalLine(c, "إجمالي المرتجع", DocumentTheme.Amount(refund.Total), emphasise: true);
                    });
                });

                col.Item().Element(c => DocumentTheme.SignatureLines(c, "توقيع العميل", "توقيع الموظف"));
            });

            page.Footer().Element(c => DocumentTheme.Footer(c));
        });
    }
}

// --- Till closing report -----------------------------------------------------------

/// <summary>The till closing report: the reconciliation walk-down, then the detail behind it.</summary>
public sealed class ShiftReportDocument : IDocument
{
    private readonly ShopProfile _shop;
    private readonly ShiftReconciliation _report;

    public ShiftReportDocument(ShopProfile shop, ShiftReconciliation report)
    {
        _shop = shop;
        _report = report;
    }

    public DocumentMetadata GetMetadata() => new() { Title = $"تقرير إغلاق الوردية {_report.Session.Id}" };

    public void Compose(IDocumentContainer container)
    {
        var s = _report.Session;
        var t = _report.Totals;

        container.Page(page =>
        {
            DocumentTheme.ApplyPage(page, PageSizes.A4);

            page.Header().Element(c => DocumentTheme.ShopHeader(c, _shop, "تقرير إغلاق وردية",
                ("رقم الوردية", s.Id.ToString()),
                ("الموظف", s.EmployeeName),
                ("الوردية", s.ShiftName),
                ("الحالة", s.Status.ToArabic())));

            page.Content().PaddingTop(10).Column(col =>
            {
                col.Spacing(10);

                col.Item().Text($"فُتحت: {DocumentTheme.DateTime(s.OpenedAt)}     أُغلقت: {(s.ClosedAt is { } c ? DocumentTheme.DateTime(c) : "ما زالت مفتوحة")}"
                    + (s.ClosedByName is not null && s.Status == ShiftSessionStatus.ForceClosed ? $"     بواسطة: {s.ClosedByName}" : ""));

                col.Item().Row(row =>
                {
                    row.RelativeItem().Border(1).Padding(8).Column(box =>
                    {
                        box.Item().PaddingBottom(4).Text("مطابقة الصندوق").Bold().FontSize(11);
                        DocumentTheme.TotalLine(box, "العهدة الافتتاحية", DocumentTheme.Amount(t.OpeningCash));
                        DocumentTheme.TotalLine(box, "+ المبيعات النقدية", DocumentTheme.Amount(t.CashSales));
                        DocumentTheme.TotalLine(box, "- المرتجعات النقدية", DocumentTheme.Amount(t.CashRefunds));
                        DocumentTheme.TotalLine(box, "+ مقبوضات على الدرج", DocumentTheme.Amount(t.CashReceipts));
                        DocumentTheme.TotalLine(box, "- مدفوعات من الدرج", DocumentTheme.Amount(t.CashPayouts));
                        box.Item().BorderTop(1).PaddingTop(2);
                        DocumentTheme.TotalLine(box, "المتوقع في الدرج", DocumentTheme.Amount(t.Expected), emphasise: true);
                        DocumentTheme.TotalLine(box, "المعدود فعلياً", DocumentTheme.Amount(s.ActualClosingCash));

                        if (s.CashDifference is { } diff)
                        {
                            var label = diff.IsNegative ? "عجز" : diff.IsPositive ? "زيادة" : "مطابق";
                            box.Item().PaddingTop(4).Background(diff.IsZero ? DocumentTheme.Shade : "#FDECEC").Padding(5).Row(r =>
                            {
                                r.RelativeItem().Text($"الفرق (المعدود - المتوقع): {label}").Bold();
                                r.AutoItem().Text(DocumentTheme.Amount(diff)).Bold().FontColor(diff.IsNegative ? DocumentTheme.Danger : DocumentTheme.Ink);
                            });

                            if (!diff.IsZero)
                            {
                                box.Item().PaddingTop(3).Text("سُجّل الفرق في حساب عجز وزيادة الصندوق ليطابق رصيد النقدية المبلغ الفعلي.")
                                    .FontSize(8).FontColor(DocumentTheme.Muted);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(s.OverrideReason))
                        {
                            box.Item().PaddingTop(3).Text($"سبب التدخل: {s.OverrideReason}").FontColor(DocumentTheme.Danger);
                        }
                    });

                    row.ConstantItem(12);

                    row.RelativeItem().Column(box =>
                    {
                        box.Item().PaddingBottom(4).Text("حسب طريقة الدفع").Bold().FontSize(11);
                        box.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn();
                                c.ConstantColumn(40);
                                c.ConstantColumn(70);
                                c.ConstantColumn(70);
                            });

                            table.Header(h =>
                            {
                                foreach (var title in new[] { "الطريقة", "عدد", "المبيعات", "المرتجعات" })
                                {
                                    h.Cell().Element(DocumentTheme.HeaderCell).Text(title).SemiBold();
                                }
                            });

                            foreach (var m in _report.ByPaymentMethod)
                            {
                                table.Cell().Element(DocumentTheme.BodyCell).Text(m.Method.ToArabic());
                                table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Ltr(m.SalesCount.ToString()));
                                table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Amount(m.Sales));
                                table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Amount(m.Refunds));
                            }
                        });
                    });
                });

                col.Item().Text($"الفواتير ({_report.Sales.Count})").Bold();
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(105);
                        c.ConstantColumn(50);
                        c.RelativeColumn();
                        c.ConstantColumn(70);
                        c.ConstantColumn(80);
                    });

                    table.Header(h =>
                    {
                        foreach (var title in new[] { "الفاتورة", "الوقت", "العميل", "الدفع", "الإجمالي" })
                        {
                            h.Cell().Element(DocumentTheme.HeaderCell).Text(title).SemiBold();
                        }
                    });

                    foreach (var sale in _report.Sales)
                    {
                        table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Ltr(sale.InvoiceNumber));
                        table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Time(sale.SoldAt));
                        table.Cell().Element(DocumentTheme.BodyCell).Text(sale.CustomerName ?? "نقدي");
                        table.Cell().Element(DocumentTheme.BodyCell).Text(sale.PaymentMethod.ToArabic());
                        table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Amount(sale.Total));
                    }
                });

                if (_report.Refunds.Count > 0)
                {
                    col.Item().Text($"المرتجعات ({_report.Refunds.Count})").Bold();
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(105);
                            c.ConstantColumn(105);
                            c.RelativeColumn();
                            c.ConstantColumn(70);
                            c.ConstantColumn(80);
                        });

                        foreach (var r in _report.Refunds)
                        {
                            table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Ltr(r.RefundNumber));
                            table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Ltr(r.InvoiceNumber));
                            table.Cell().Element(DocumentTheme.BodyCell).Text(r.Reason);
                            table.Cell().Element(DocumentTheme.BodyCell).Text(r.PaymentMethod.ToArabic());
                            table.Cell().Element(DocumentTheme.BodyCell).Text(DocumentTheme.Amount(r.Total));
                        }
                    });
                }

                if (_report.DrawerVouchers.Count > 0)
                {
                    col.Item().Text("حركات الدرج الأخرى").Bold();

                    foreach (var v in _report.DrawerVouchers)
                    {
                        col.Item().Row(row =>
                        {
                            row.ConstantItem(110).Text(v.Kind);
                            row.ConstantItem(120).Text(DocumentTheme.Ltr(v.Number));
                            row.ConstantItem(90).Text(DocumentTheme.Ltr((v.IsInflow ? "+" : "-") + v.Amount.ToDisplay()));
                            row.RelativeItem().Text(v.IsVoided ? "(ملغى)" : "").FontColor(DocumentTheme.Danger);
                        });
                    }
                }

                col.Item().Element(c => DocumentTheme.SignatureLines(c, $"عدّه: {s.EmployeeName}", "راجعه"));
            });

            page.Footer().Element(c => DocumentTheme.Footer(c));
        });
    }
}

// --- Vouchers ------------------------------------------------------------------------

public sealed record VoucherData(
    ShopProfile Shop,
    string Title,
    string Number,
    DateTime Date,
    string PartyLabel,
    string PartyName,
    Money Amount,
    PaymentMethod Method,
    string? Description,
    string? Reference,
    string UserName,
    bool IsVoided,
    string? VoidReason,
    string[] Signatures);

/// <summary>سند قبض / سند صرف / سند مصروف: one half-page voucher.</summary>
public sealed class VoucherDocument : IDocument
{
    private readonly VoucherData _data;

    public VoucherDocument(VoucherData data) => _data = data;

    public DocumentMetadata GetMetadata() => new() { Title = $"{_data.Title} {_data.Number}" };

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            DocumentTheme.ApplyPage(page, PageSizes.A5.Landscape());

            page.Header().Element(c => DocumentTheme.ShopHeader(c, _data.Shop, _data.Title,
                ("الرقم", _data.Number),
                ("التاريخ", _data.Date.ToString("yyyy-MM-dd HH:mm"))));

            page.Content().PaddingTop(12).Column(col =>
            {
                col.Spacing(8);

                if (_data.IsVoided)
                {
                    col.Item().Background("#FDECEC").Padding(6).Text($"سند ملغى - السبب: {_data.VoidReason}")
                        .Bold().FontColor(DocumentTheme.Danger);
                }

                col.Item().Row(row =>
                {
                    row.RelativeItem().Text(text =>
                    {
                        text.Span($"{_data.PartyLabel}: ").FontColor(DocumentTheme.Muted);
                        text.Span(_data.PartyName).Bold().FontSize(11);
                    });
                    row.AutoItem().Border(1.25f).PaddingVertical(4).PaddingHorizontal(10)
                        .Text($"{DocumentTheme.Amount(_data.Amount)} {_data.Shop.CurrencySymbol}").Bold().FontSize(13);
                });

                col.Item().Element(c => DocumentTheme.NoteBox(c, ArabicNumberWords.Amount(_data.Amount), strong: true));

                col.Item().Text(text =>
                {
                    text.Span("طريقة الدفع: ").FontColor(DocumentTheme.Muted);
                    text.Span(_data.Method.ToArabic());

                    if (!string.IsNullOrWhiteSpace(_data.Reference))
                    {
                        text.Span("     المرجع: ").FontColor(DocumentTheme.Muted);
                        text.Span(_data.Reference);
                    }
                });

                if (!string.IsNullOrWhiteSpace(_data.Description))
                {
                    col.Item().Text($"البيان: {_data.Description}");
                }

                col.Item().Text($"حرره: {_data.UserName}").FontColor(DocumentTheme.Muted);
                col.Item().Element(c => DocumentTheme.SignatureLines(c, _data.Signatures));
            });

            page.Footer().Element(c => DocumentTheme.Footer(c));
        });
    }
}
