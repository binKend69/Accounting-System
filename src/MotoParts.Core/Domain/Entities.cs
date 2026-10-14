using MotoParts.Core.Common;

namespace MotoParts.Core.Domain;

// Row types, one per table. Plain data: every rule lives in the services and
// the schema. Property names map to snake_case columns automatically.

public sealed class User
{
    public long Id { get; set; }
    public string Username { get; set; } = "";
    public string FullName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string? EmployeeCode { get; set; }
    public string? Phone { get; set; }
    public DateOnly? HireDate { get; set; }
    public Money BaseSalary { get; set; }
    public bool IsActive { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Role
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public sealed class AuditLog
{
    public long Id { get; set; }
    public string AuditableType { get; set; } = "";
    public long? AuditableId { get; set; }
    public AuditAction Action { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public long? UserId { get; set; }
    public string? UserName { get; set; }
    public string? MachineName { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Account
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public AccountType Type { get; set; }
    public NormalBalance NormalBalance { get; set; }
    public long? ParentId { get; set; }
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; }

    public string Label => $"{Code} - {Name}";
}

public sealed class FiscalPeriod
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool IsLocked { get; set; }
    public DateTime? LockedAt { get; set; }
    public long? LockedBy { get; set; }
    public string? LockNote { get; set; }

    public bool Contains(DateOnly date) => date >= StartDate && date <= EndDate;
}

public sealed class JournalEntry
{
    public long Id { get; set; }
    public string EntryNumber { get; set; } = "";
    public long FiscalPeriodId { get; set; }
    public DateOnly EntryDate { get; set; }
    public string Memo { get; set; } = "";
    public JournalSource Source { get; set; }
    public string? SourceDocumentType { get; set; }
    public long? SourceDocumentId { get; set; }
    public Money TotalDebit { get; set; }
    public Money TotalCredit { get; set; }
    public DateTime? PostedAt { get; set; }
    public long? ReversesJournalEntryId { get; set; }
    public bool IsReversal { get; set; }
    public long? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool IsPosted => PostedAt is not null;
}

public sealed class JournalLine
{
    public long Id { get; set; }
    public long JournalEntryId { get; set; }
    public long AccountId { get; set; }
    public int LineNumber { get; set; }
    public Money Debit { get; set; }
    public Money Credit { get; set; }
    public string? Memo { get; set; }

    public bool IsDebit => Debit.IsPositive;
}

public sealed class Category
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; }
}

public sealed class Product
{
    public long Id { get; set; }
    public string Sku { get; set; } = "";
    public string? Barcode { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long? CategoryId { get; set; }
    public string? Brand { get; set; }
    public string? FitsModels { get; set; }
    public string Unit { get; set; } = "قطعة";
    public Money SellingPrice { get; set; }
    public Money CostPrice { get; set; }
    public Quantity StockQty { get; set; }
    public Money StockValue { get; set; }
    public Quantity ReorderLevel { get; set; }
    public bool IsActive { get; set; }

    public bool IsLowStock => ReorderLevel.IsPositive && StockQty <= ReorderLevel;
}

public sealed class StockMovement
{
    public long Id { get; set; }
    public long ProductId { get; set; }
    public StockMovementType Type { get; set; }
    public Quantity Quantity { get; set; }
    public Money ValueChange { get; set; }
    public Quantity BalanceAfter { get; set; }
    public Money ValueAfter { get; set; }
    public Money UnitCost { get; set; }
    public string? ReferenceType { get; set; }
    public long? ReferenceId { get; set; }
    public string? Notes { get; set; }
    public long? UserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Shift
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public bool IsActive { get; set; }
}

public sealed class ShiftSession
{
    public long Id { get; set; }
    public long ShiftId { get; set; }
    public long UserId { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public Money OpeningCash { get; set; }
    public Money? CashSalesTotal { get; set; }
    public Money? CashRefundsTotal { get; set; }
    public Money? CashReceiptsTotal { get; set; }
    public Money? CashPayoutsTotal { get; set; }
    public Money? ExpectedCash { get; set; }
    public Money? ActualClosingCash { get; set; }
    public Money? CashDifference { get; set; }
    public ShiftSessionStatus Status { get; set; }
    public long? ClosedBy { get; set; }
    public string? OverrideReason { get; set; }
    public string? Notes { get; set; }

    public bool IsOpen => Status == ShiftSessionStatus.Open;

    public bool IsShort => CashDifference is { IsNegative: true };

    public bool IsOver => CashDifference is { IsPositive: true };
}

public sealed class Attendance
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public DateOnly WorkDate { get; set; }
    public DateTime? CheckInAt { get; set; }
    public DateTime? CheckOutAt { get; set; }
    public string? Notes { get; set; }

    public TimeSpan? Worked => CheckInAt is { } inAt && CheckOutAt is { } outAt ? outAt - inAt : null;
}

public sealed class Customer
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? TaxNumber { get; set; }
    public Money CreditLimit { get; set; }
    public Money OpeningBalance { get; set; }
    public long? OpeningJournalEntryId { get; set; }
    public bool IsActive { get; set; }
    public string? Notes { get; set; }
}

public sealed class Sale
{
    public long Id { get; set; }
    public string InvoiceNumber { get; set; } = "";
    public DateOnly SaleDate { get; set; }
    public DateTime SoldAt { get; set; }
    public long? CustomerId { get; set; }
    public long UserId { get; set; }
    public long ShiftSessionId { get; set; }
    public bool PricesIncludeTax { get; set; }
    public int TaxRateBp { get; set; }
    public Money Subtotal { get; set; }
    public Money DiscountTotal { get; set; }
    public Money TaxTotal { get; set; }
    public Money Total { get; set; }
    public Money CogsTotal { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public Money AmountPaid { get; set; }
    public Money ChangeDue { get; set; }
    public SaleStatus Status { get; set; }
    public Money RefundedTotal { get; set; }
    public Money RefundedTax { get; set; }
    public long? JournalEntryId { get; set; }
    public string? Notes { get; set; }

    /// <summary>The sale after discount, in its own price basis.</summary>
    public Money NetOfDiscount => Subtotal - DiscountTotal;

    /// <summary>Revenue excluding tax.</summary>
    public Money Revenue => Total - TaxTotal;

    public decimal TaxRate => TaxRateBp / 10000m;
}

public sealed class SaleItem
{
    public long Id { get; set; }
    public long SaleId { get; set; }
    public long ProductId { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public Quantity Quantity { get; set; }
    public Money UnitPrice { get; set; }
    public Money Discount { get; set; }
    public Money LineTotal { get; set; }
    public Money UnitCost { get; set; }
    public Money LineCogs { get; set; }
    public Quantity RefundedQuantity { get; set; }
    public Money RefundedAmount { get; set; }
    public Money RefundedCogs { get; set; }

    public Quantity RefundableQuantity => Quantity - RefundedQuantity;
}

public sealed class SaleRefund
{
    public long Id { get; set; }
    public string RefundNumber { get; set; } = "";
    public long SaleId { get; set; }
    public DateOnly RefundDate { get; set; }
    public DateTime RefundedAt { get; set; }
    public long UserId { get; set; }
    public long ShiftSessionId { get; set; }
    public bool PricesIncludeTax { get; set; }
    public Money Subtotal { get; set; }
    public Money TaxTotal { get; set; }
    public Money Total { get; set; }
    public Money CogsTotal { get; set; }
    public Money RestockedCogs { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public string Reason { get; set; } = "";
    public long? JournalEntryId { get; set; }
}

public sealed class SaleRefundItem
{
    public long Id { get; set; }
    public long SaleRefundId { get; set; }
    public long SaleItemId { get; set; }
    public long ProductId { get; set; }
    public Quantity Quantity { get; set; }
    public Money LineTotal { get; set; }
    public Money LineCogs { get; set; }
    public bool Restocked { get; set; }
}

public sealed class Supplier
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? TaxNumber { get; set; }
    public Money OpeningBalance { get; set; }
    public long? OpeningJournalEntryId { get; set; }
    public bool IsActive { get; set; }
    public string? Notes { get; set; }
}

public sealed class Purchase
{
    public long Id { get; set; }
    public string PurchaseNumber { get; set; } = "";
    public long SupplierId { get; set; }
    public long UserId { get; set; }
    public DateOnly PurchaseDate { get; set; }
    public string? SupplierInvoiceNumber { get; set; }
    public int TaxRateBp { get; set; }
    public Money Subtotal { get; set; }
    public Money TaxTotal { get; set; }
    public Money Total { get; set; }
    public PurchaseStatus Status { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public long? ReceivedBy { get; set; }
    public DateTime? CancelledAt { get; set; }
    public long? CancelledBy { get; set; }
    public string? CancelReason { get; set; }
    public long? JournalEntryId { get; set; }
    public string? Notes { get; set; }

    public decimal TaxRate => TaxRateBp / 10000m;
}

public sealed class PurchaseItem
{
    public long Id { get; set; }
    public long PurchaseId { get; set; }
    public long ProductId { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public Quantity Quantity { get; set; }
    public Money UnitCost { get; set; }
    public Money LineTotal { get; set; }
}

public sealed class CustomerReceipt
{
    public long Id { get; set; }
    public string ReceiptNumber { get; set; } = "";
    public long CustomerId { get; set; }
    public DateOnly ReceiptDate { get; set; }
    public DateTime ReceivedAt { get; set; }
    public Money Amount { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public long? ShiftSessionId { get; set; }
    public long UserId { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public long? JournalEntryId { get; set; }
    public DateTime? VoidedAt { get; set; }
    public long? VoidedBy { get; set; }
    public string? VoidReason { get; set; }
    public long? VoidJournalEntryId { get; set; }

    public bool IsVoided => VoidedAt is not null;
}

public sealed class SupplierPayment
{
    public long Id { get; set; }
    public string PaymentNumber { get; set; } = "";
    public long SupplierId { get; set; }
    public DateOnly PaymentDate { get; set; }
    public DateTime PaidAt { get; set; }
    public Money Amount { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public long? ShiftSessionId { get; set; }
    public long UserId { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public long? JournalEntryId { get; set; }
    public DateTime? VoidedAt { get; set; }
    public long? VoidedBy { get; set; }
    public string? VoidReason { get; set; }
    public long? VoidJournalEntryId { get; set; }

    public bool IsVoided => VoidedAt is not null;
}

public sealed class ExpenseVoucher
{
    public long Id { get; set; }
    public string VoucherNumber { get; set; } = "";
    public long ExpenseAccountId { get; set; }
    public DateOnly VoucherDate { get; set; }
    public DateTime PaidAt { get; set; }
    public Money Amount { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public long? ShiftSessionId { get; set; }
    public long UserId { get; set; }
    public string? Payee { get; set; }
    public string Description { get; set; } = "";
    public long? JournalEntryId { get; set; }
    public DateTime? VoidedAt { get; set; }
    public long? VoidedBy { get; set; }
    public string? VoidReason { get; set; }
    public long? VoidJournalEntryId { get; set; }

    public bool IsVoided => VoidedAt is not null;
}

public sealed class PayrollRun
{
    public long Id { get; set; }
    public string? RunNumber { get; set; }
    public int PeriodYear { get; set; }
    public int PeriodMonth { get; set; }
    public PayrollStatus Status { get; set; }
    public Money TotalBase { get; set; }
    public Money TotalAdditions { get; set; }
    public Money TotalDeductions { get; set; }
    public Money TotalNet { get; set; }
    public DateOnly? PostingDate { get; set; }
    public DateTime? PostedAt { get; set; }
    public long? PostedBy { get; set; }
    public long? JournalEntryId { get; set; }
    public DateOnly? PaymentDate { get; set; }
    public DateTime? PaidAt { get; set; }
    public long? PaidBy { get; set; }
    public PaymentMethod? PaymentMethod { get; set; }
    public long? PaymentJournalEntryId { get; set; }
    public string? Notes { get; set; }
    public long? CreatedBy { get; set; }

    public string PeriodLabel => $"{PeriodYear:0000}-{PeriodMonth:00}";
}

public sealed class PayrollLine
{
    public long Id { get; set; }
    public long PayrollRunId { get; set; }
    public long UserId { get; set; }
    public string EmployeeName { get; set; } = "";
    public Money BaseSalary { get; set; }
    public Money Additions { get; set; }
    public Money Deductions { get; set; }
    public Money NetPay { get; set; }
    public string? Notes { get; set; }
}
