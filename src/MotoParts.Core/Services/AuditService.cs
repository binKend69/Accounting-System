using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MotoParts.Core.Common;
using MotoParts.Core.Data;
using MotoParts.Core.Domain;
using MotoParts.Core.Security;

namespace MotoParts.Core.Services;

/// <summary>
/// Writes the audit trail.
///
/// Called explicitly by the services at the moments that matter - a journal
/// entry posted, a till closed or overridden, a price changed, a period locked
/// - with the before and after values, so a reviewer sees what changed and not
/// merely that something did. The table itself refuses updates and deletes.
/// </summary>
public sealed class AuditService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        // Keep Arabic readable in the stored JSON rather than \u-escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new MoneyJsonConverter(), new QuantityJsonConverter(), new JsonStringEnumConverter() },
    };

    private readonly DbSession _db;
    private readonly IClock _clock;

    public AuditService(DbSession db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public void Record(
        string auditableType,
        long? auditableId,
        AuditAction action,
        Actor? actor,
        object? oldValues = null,
        object? newValues = null,
        string? reason = null)
    {
        _db.Execute(
            """
            INSERT INTO audit_logs
                (auditable_type, auditable_id, action, old_values, new_values,
                 user_id, user_name, machine_name, reason, created_at)
            VALUES
                (@AuditableType, @AuditableId, @Action, @OldValues, @NewValues,
                 @UserId, @UserName, @MachineName, @Reason, @CreatedAt)
            """,
            new
            {
                AuditableType = auditableType,
                AuditableId = auditableId,
                Action = action.ToString(),
                OldValues = oldValues is null ? null : JsonSerializer.Serialize(oldValues, Json),
                NewValues = newValues is null ? null : JsonSerializer.Serialize(newValues, Json),
                UserId = actor?.Id,
                UserName = actor?.FullName,
                MachineName = Environment.MachineName,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                CreatedAt = _clock.Now,
            });
    }

    /// <summary>The audit trail with filters, newest first.</summary>
    public IReadOnlyList<AuditLog> Search(DateOnly? from, DateOnly? to, AuditAction? action = null, string? auditableType = null, long? userId = null) =>
        _db.Query<AuditLog>(
            """
            SELECT * FROM audit_logs
            WHERE (@from IS NULL OR date(created_at) >= @from)
              AND (@to IS NULL OR date(created_at) <= @to)
              AND (@action IS NULL OR action = @action)
              AND (@auditableType IS NULL OR auditable_type = @auditableType)
              AND (@userId IS NULL OR user_id = @userId)
            ORDER BY id DESC
            LIMIT 5000
            """,
            new { from, to, action = action?.ToString(), auditableType, userId }).ToList();

    public IReadOnlyList<string> AuditedTypes() =>
        _db.Query<string>("SELECT DISTINCT auditable_type FROM audit_logs ORDER BY auditable_type").ToList();

    public IReadOnlyList<AuditLog> For(string auditableType, long auditableId) =>
        _db.Query<AuditLog>(
            "SELECT * FROM audit_logs WHERE auditable_type = @auditableType AND auditable_id = @auditableId ORDER BY id",
            new { auditableType, auditableId }).ToList();

    private sealed class MoneyJsonConverter : JsonConverter<Money>
    {
        public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Money.Of(reader.GetDecimal());

        public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    private sealed class QuantityJsonConverter : JsonConverter<Quantity>
    {
        public override Quantity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Quantity.Of(reader.GetDecimal());

        public override void Write(Utf8JsonWriter writer, Quantity value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
