using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HisabTak.Infrastructure.Persistence;

internal sealed class SqlLedger(HisabTakDbContext db)
{
    public async Task<SqlCommand> CommandAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] values)
    {
        if (db.Database.GetDbConnection().State != ConnectionState.Open)
            await db.Database.OpenConnectionAsync(ct);
        var command = ((SqlConnection)db.Database.GetDbConnection()).CreateCommand();
        command.CommandText = sql;
        command.Transaction = (SqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction();
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value is DateOnly date ? date.ToDateTime(TimeOnly.MinValue) : value ?? DBNull.Value);
        return command;
    }

    public async Task<long> ScalarAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] values)
    {
        await using var command = await CommandAsync(sql, ct, values);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    public async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] values)
    {
        await using var command = await CommandAsync(sql, ct, values);
        await command.ExecuteNonQueryAsync(ct);
    }
}
