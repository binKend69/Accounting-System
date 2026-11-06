using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using MotoParts.Core.Common;

namespace MotoParts.Core.Data;

/// <summary>
/// One connection, and the transaction on it, shared by every service taking
/// part in a single operation.
///
/// Services call <see cref="InTransaction{T}"/> freely. The outermost call
/// begins the transaction; nested calls join it. So when checkout calls the
/// inventory service, which calls the accounting service, all three write in
/// one transaction, and any failure rolls all of it back: no stock moved, no
/// entry posted, and the invoice number returned to the sequence.
///
/// Write transactions start with BEGIN IMMEDIATE, which takes SQLite's write
/// lock up front. Anything read inside the transaction therefore cannot change
/// before it commits, which is what makes check-then-act (stock available?
/// then decrement) safe.
///
/// Not thread-safe: one session belongs to one operation at a time.
/// </summary>
public sealed class DbSession : IDisposable
{
    private readonly Database _database;
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;
    private int _depth;

    public DbSession(Database database)
    {
        _database = database;
    }

    public bool InTransactionScope => _depth > 0;

    private SqliteConnection Connection => _connection ??= _database.Open();

    public T InTransaction<T>(Func<T> work)
    {
        if (_depth > 0)
        {
            _depth++;

            try
            {
                return work();
            }
            finally
            {
                _depth--;
            }
        }

        // IMMEDIATE: take the write lock now, not on the first write.
        _transaction = Connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        _depth = 1;

        try
        {
            var result = work();
            _transaction.Commit();
            return result;
        }
        catch
        {
            _transaction.Rollback();
            throw;
        }
        finally
        {
            _transaction.Dispose();
            _transaction = null;
            _depth = 0;
        }
    }

    public void InTransaction(Action work) => InTransaction(() =>
    {
        work();
        return 0;
    });

    /// <summary>Throws unless called inside a transaction. For code that would be unsafe outside one.</summary>
    public void RequireTransaction(string what)
    {
        if (_depth == 0)
        {
            throw new InvalidOperationException($"{what} must run inside a transaction.");
        }
    }

    public IEnumerable<T> Query<T>(string sql, object? parameters = null) =>
        Translate(() => Connection.Query<T>(sql, parameters, _transaction));

    public T? QueryFirstOrDefault<T>(string sql, object? parameters = null) =>
        Translate(() => Connection.QueryFirstOrDefault<T>(sql, parameters, _transaction));

    public T QuerySingle<T>(string sql, object? parameters = null) =>
        Translate(() => Connection.QuerySingle<T>(sql, parameters, _transaction));

    public T? ExecuteScalar<T>(string sql, object? parameters = null) =>
        Translate(() => Connection.ExecuteScalar<T>(sql, parameters, _transaction));

    public int Execute(string sql, object? parameters = null) =>
        Translate(() => Connection.Execute(sql, parameters, _transaction));

    /// <summary>INSERT ... RETURNING id, as a single round trip.</summary>
    public long Insert(string sql, object? parameters = null) =>
        Translate(() => Connection.ExecuteScalar<long>(sql, parameters, _transaction));

    public void Dispose()
    {
        _transaction?.Dispose();
        _connection?.Dispose();
        _connection = null;
    }

    /// <summary>
    /// Turns a trigger's RAISE(ABORT, 'message') into a <see cref="BusinessRuleException"/>
    /// carrying the Arabic message, so a rule enforced by the database reaches
    /// the operator the same way as one enforced by a service.
    /// </summary>
    private static T Translate<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (SqliteException ex) when (TryGetRuleMessage(ex, out var message))
        {
            throw new BusinessRuleException(message, ex);
        }
    }

    internal static bool TryGetRuleMessage(SqliteException ex, out string message)
    {
        message = string.Empty;

        // SQLITE_CONSTRAINT (19) covers RAISE(ABORT), CHECK, UNIQUE and FK.
        if (ex.SqliteErrorCode != 19)
        {
            return false;
        }

        // RAISE messages are ours and already readable; anything Arabic is one.
        var text = ex.Message;
        var marker = text.IndexOf("Error 19: '", StringComparison.Ordinal);

        if (marker >= 0)
        {
            var start = marker + "Error 19: '".Length;
            var end = text.LastIndexOf('\'');

            if (end > start)
            {
                text = text[start..end];
            }
        }

        if (text.Any(c => c is >= '؀' and <= 'ۿ'))
        {
            message = text;
            return true;
        }

        // A raw CHECK/UNIQUE/FK failure is still a refused write, not a crash,
        // but its text is SQLite's. Map the common shapes to plain Arabic.
        if (text.Contains("UNIQUE constraint failed", StringComparison.Ordinal))
        {
            message = "القيمة المدخلة مستخدمة من قبل في سجل آخر.";
            return true;
        }

        if (text.Contains("FOREIGN KEY constraint failed", StringComparison.Ordinal))
        {
            message = "لا يمكن تنفيذ العملية لأن السجل مرتبط بسجلات أخرى.";
            return true;
        }

        return false;
    }
}
