using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using MotoParts.Core.Services;

namespace MotoParts.Documents;

/// <summary>A column of a printed table report. Width 0 means "share the remaining space".</summary>
public sealed record ReportColumn(string Title, float Width = 0, bool IsAmount = false);

public sealed record ReportRow(IReadOnlyList<string> Cells, bool Bold = false, bool Danger = false);

/// <summary>
/// Any list-style report: a title, filters described in words, a table, an
/// optional totals row and summary figures. Used for the trial balance,
/// general ledger, cash reconciliation, stock, sales, statements and payroll.
/// </summary>
public sealed record TableReport(
    string Title,
    string? Subtitle,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<ReportRow> Rows,
    ReportRow? TotalRow = null,
    IReadOnlyList<(string Label, string Value)>? Summary = null,
    string? Banner = null,
    bool BannerIsWarning = false,
    bool Landscape = false,
    IReadOnlyList<string>? Signatures = null);

public sealed class TableReportDocument : IDocument
{
    private readonly ShopProfile _shop;
    private readonly TableReport _report;

    public TableReportDocument(ShopProfile shop, TableReport report)
    {
        _shop = shop;
        _report = report;
    }

    public DocumentMetadata GetMetadata() => new() { Title = _report.Title };

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            DocumentTheme.ApplyPage(page, _report.Landscape ? PageSizes.A4.Landscape() : PageSizes.A4, 10);

            page.Header().Element(c => DocumentTheme.ShopHeader(c, _shop, _report.Title, ("", _report.Subtitle)));

            page.Content().PaddingTop(8).Column(col =>
            {
                col.Spacing(6);

                if (!string.IsNullOrWhiteSpace(_report.Banner))
                {
                    col.Item().Background(_report.BannerIsWarning ? "#FDECEC" : DocumentTheme.Shade).Padding(6)
                        .Text(_report.Banner).Bold().FontColor(_report.BannerIsWarning ? DocumentTheme.Danger : DocumentTheme.Ink);
                }

                if (_report.Summary is { Count: > 0 })
                {
                    col.Item().Row(row =>
                    {
                        foreach (var (label, value) in _report.Summary)
                        {
                            row.RelativeItem().Border(0.75f).BorderColor(DocumentTheme.Rule).Padding(5).Column(c =>
                            {
                                c.Item().Text(label).FontColor(DocumentTheme.Muted).FontSize(8.5f);
                                c.Item().Text(DocumentTheme.Bidi(value)).Bold().FontSize(11);
                            });
                        }
                    });
                }

                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        foreach (var column in _report.Columns)
                        {
                            if (column.Width > 0)
                            {
                                c.ConstantColumn(column.Width);
                            }
                            else
                            {
                                c.RelativeColumn();
                            }
                        }
                    });

                    table.Header(h =>
                    {
                        foreach (var column in _report.Columns)
                        {
                            h.Cell().Element(DocumentTheme.HeaderCell).Text(column.Title).SemiBold();
                        }
                    });

                    if (_report.Rows.Count == 0)
                    {
                        table.Cell().ColumnSpan((uint)_report.Columns.Count).Element(DocumentTheme.BodyCell)
                            .Text("لا توجد بيانات في هذه الفترة.").FontColor(DocumentTheme.Muted);
                    }

                    foreach (var row in _report.Rows)
                    {
                        WriteRow(table, row, DocumentTheme.BodyCell);
                    }

                    if (_report.TotalRow is not null)
                    {
                        WriteRow(table, _report.TotalRow with { Bold = true }, DocumentTheme.TotalCell);
                    }
                });

                if (_report.Signatures is { Count: > 0 })
                {
                    col.Item().Element(c => DocumentTheme.SignatureLines(c, [.. _report.Signatures]));
                }
            });

            page.Footer().Element(c => DocumentTheme.Footer(c));
        });
    }

    private void WriteRow(TableDescriptor table, ReportRow row, Func<IContainer, IContainer> cellStyle)
    {
        for (var i = 0; i < _report.Columns.Count; i++)
        {
            var value = i < row.Cells.Count ? row.Cells[i] : "";
            var text = table.Cell().Element(cellStyle).Text(DocumentTheme.Bidi(value));

            if (row.Bold)
            {
                text.Bold();
            }

            if (row.Danger)
            {
                text.FontColor(DocumentTheme.Danger);
            }
        }
    }
}

public sealed record StatementLineItem(string Label, string Amount, bool Bold = false, bool Indent = true);

public sealed record StatementSection(string Title, IReadOnlyList<StatementLineItem> Lines, StatementLineItem? Total);

/// <summary>
/// Financial statements in the classic layout: sections of labelled amounts with
/// subtotals, ending in the figures that matter (net profit, totals that balance).
/// </summary>
public sealed record StatementReport(
    string Title,
    string? Subtitle,
    IReadOnlyList<StatementSection> Sections,
    IReadOnlyList<StatementLineItem> Conclusion,
    string? Banner = null,
    bool BannerIsWarning = false);

public sealed class StatementReportDocument : IDocument
{
    private readonly ShopProfile _shop;
    private readonly IReadOnlyList<StatementReport> _statements;

    public StatementReportDocument(ShopProfile shop, params StatementReport[] statements)
    {
        _shop = shop;
        _statements = statements;
    }

    public DocumentMetadata GetMetadata() => new() { Title = _statements[0].Title };

    public void Compose(IDocumentContainer container)
    {
        foreach (var statement in _statements)
        {
            container.Page(page =>
            {
                DocumentTheme.ApplyPage(page, PageSizes.A4);

                page.Header().Element(c => DocumentTheme.ShopHeader(c, _shop, statement.Title, ("", statement.Subtitle)));

                page.Content().PaddingTop(10).PaddingHorizontal(40).Column(col =>
                {
                    col.Spacing(4);

                    if (!string.IsNullOrWhiteSpace(statement.Banner))
                    {
                        col.Item().Background(statement.BannerIsWarning ? "#FDECEC" : DocumentTheme.Shade).Padding(6)
                            .Text(statement.Banner).Bold().FontColor(statement.BannerIsWarning ? DocumentTheme.Danger : DocumentTheme.Ink);
                    }

                    foreach (var section in statement.Sections)
                    {
                        col.Item().PaddingTop(8).BorderBottom(1).BorderColor(DocumentTheme.Ink).Text(section.Title).Bold().FontSize(11);

                        foreach (var line in section.Lines)
                        {
                            Line(col, line);
                        }

                        if (section.Total is not null)
                        {
                            col.Item().BorderTop(0.75f).BorderColor(DocumentTheme.Rule);
                            Line(col, section.Total with { Bold = true, Indent = false });
                        }
                    }

                    col.Item().PaddingTop(10).BorderTop(1.5f).BorderColor(DocumentTheme.Ink);

                    foreach (var line in statement.Conclusion)
                    {
                        Line(col, line with { Indent = false });
                    }
                });

                page.Footer().Element(c => DocumentTheme.Footer(c));
            });
        }
    }

    private static void Line(ColumnDescriptor col, StatementLineItem line)
    {
        col.Item().PaddingVertical(2).PaddingRight(line.Indent ? 14 : 0).Row(row =>
        {
            var label = row.RelativeItem().Text(line.Label);
            var amount = row.ConstantItem(120).AlignLeft().Text(DocumentTheme.Bidi(line.Amount));

            if (line.Bold)
            {
                label.Bold();
                amount.Bold();
            }
        });
    }
}
