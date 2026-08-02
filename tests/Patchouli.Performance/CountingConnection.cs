using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace Patchouli.Performance;

/// <summary>
/// A <see cref="SqliteConnection"/> whose commands report statement counts and rows read into a
/// <see cref="QueryCounters"/> sink. Nothing about the statements themselves is recorded, so the
/// counters remain safe for logs.
/// </summary>
public sealed class CountingSqliteConnection : SqliteConnection
{
    private readonly QueryCounters _counters;
    private readonly Func<bool>? _isUiThread;

    public CountingSqliteConnection(string connectionString, QueryCounters counters) : base(connectionString)
    {
        _counters = counters;
    }

    public CountingSqliteConnection(string connectionString, QueryCounters counters, Func<bool> isUiThread)
        : base(connectionString)
    {
        _counters = counters;
        _isUiThread = isUiThread;
    }

    protected override DbCommand CreateDbCommand()
    {
        SqliteCommand inner = (SqliteCommand)base.CreateDbCommand();
        return new CountingDbCommand(inner, _counters, _isUiThread);
    }
}

public sealed class CountingDbCommand : DbCommand
{
    private readonly SqliteCommand _inner;
    private readonly QueryCounters _counters;
    private readonly Func<bool>? _isUiThread;

    public CountingDbCommand(SqliteCommand inner, QueryCounters counters) : this(inner, counters, null)
    {
    }

    public CountingDbCommand(SqliteCommand inner, QueryCounters counters, Func<bool>? isUiThread)
    {
        _inner = inner;
        _counters = counters;
        _isUiThread = isUiThread;
    }

    private void RecordExecutionContext()
    {
        if (_isUiThread is not null && _isUiThread())
        {
            _counters.RecordUiThreadCommand();
        }
    }

    [AllowNull]
    public override string CommandText
    {
        get => _inner.CommandText;
        set => _inner.CommandText = value ?? string.Empty;
    }

    public override int CommandTimeout
    {
        get => _inner.CommandTimeout;
        set => _inner.CommandTimeout = value;
    }

    public override CommandType CommandType
    {
        get => _inner.CommandType;
        set => _inner.CommandType = value;
    }

    public override bool DesignTimeVisible
    {
        get => _inner.DesignTimeVisible;
        set => _inner.DesignTimeVisible = value;
    }

    public override UpdateRowSource UpdatedRowSource
    {
        get => _inner.UpdatedRowSource;
        set => _inner.UpdatedRowSource = value;
    }

    protected override DbConnection? DbConnection
    {
        get => _inner.Connection;
        set => _inner.Connection = value as SqliteConnection;
    }

    protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => _inner.Transaction;
        set => _inner.Transaction = value as SqliteTransaction;
    }

    public override void Cancel()
    {
        _inner.Cancel();
    }

    public override void Prepare()
    {
        _inner.Prepare();
    }

    protected override DbParameter CreateDbParameter()
    {
        return _inner.CreateParameter();
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        _counters.RecordStatement();
        RecordExecutionContext();
        return new CountingDbDataReader(_inner.ExecuteReader(behavior), _counters);
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        _counters.RecordStatement();
        RecordExecutionContext();
        DbDataReader reader = await _inner.ExecuteReaderAsync(behavior, cancellationToken);
        return new CountingDbDataReader(reader, _counters);
    }

    public override int ExecuteNonQuery()
    {
        _counters.RecordStatement();
        _counters.RecordWrite();
        RecordExecutionContext();
        return _inner.ExecuteNonQuery();
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        _counters.RecordStatement();
        _counters.RecordWrite();
        RecordExecutionContext();
        return _inner.ExecuteNonQueryAsync(cancellationToken);
    }

    public override object? ExecuteScalar()
    {
        _counters.RecordStatement();
        RecordExecutionContext();
        return _inner.ExecuteScalar();
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        _counters.RecordStatement();
        RecordExecutionContext();
        return _inner.ExecuteScalarAsync(cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        return _inner.DisposeAsync();
    }
}

public sealed class CountingDbDataReader : DbDataReader
{
    private readonly DbDataReader _inner;
    private readonly QueryCounters _counters;

    public CountingDbDataReader(DbDataReader inner, QueryCounters counters)
    {
        _inner = inner;
        _counters = counters;
    }

    public override int Depth => _inner.Depth;
    public override int FieldCount => _inner.FieldCount;
    public override bool HasRows => _inner.HasRows;
    public override bool IsClosed => _inner.IsClosed;
    public override int RecordsAffected => _inner.RecordsAffected;
    public override object this[int ordinal] => _inner[ordinal];
    public override object this[string name] => _inner[name];

    public override bool Read()
    {
        bool hasRow = _inner.Read();
        if (hasRow)
        {
            _counters.RecordRow();
        }

        return hasRow;
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        bool hasRow = await _inner.ReadAsync(cancellationToken);
        if (hasRow)
        {
            _counters.RecordRow();
        }

        return hasRow;
    }

    public override bool NextResult()
    {
        return _inner.NextResult();
    }

    public override async Task<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        return await _inner.NextResultAsync(cancellationToken);
    }

    public override string GetName(int ordinal)
    {
        return _inner.GetName(ordinal);
    }

    public override int GetOrdinal(string name)
    {
        return _inner.GetOrdinal(name);
    }

    public override Type GetFieldType(int ordinal)
    {
        return _inner.GetFieldType(ordinal);
    }

    public override string GetDataTypeName(int ordinal)
    {
        return _inner.GetDataTypeName(ordinal);
    }

    public override object GetValue(int ordinal)
    {
        return _inner.GetValue(ordinal);
    }

    public override int GetValues(object[] values)
    {
        return _inner.GetValues(values);
    }

    public override bool IsDBNull(int ordinal)
    {
        return _inner.IsDBNull(ordinal);
    }

    public override bool GetBoolean(int ordinal)
    {
        return _inner.GetBoolean(ordinal);
    }

    public override byte GetByte(int ordinal)
    {
        return _inner.GetByte(ordinal);
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        return _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    }

    public override char GetChar(int ordinal)
    {
        return _inner.GetChar(ordinal);
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        return _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    }

    public override DateTime GetDateTime(int ordinal)
    {
        return _inner.GetDateTime(ordinal);
    }

    public override decimal GetDecimal(int ordinal)
    {
        return _inner.GetDecimal(ordinal);
    }

    public override double GetDouble(int ordinal)
    {
        return _inner.GetDouble(ordinal);
    }

    public override float GetFloat(int ordinal)
    {
        return _inner.GetFloat(ordinal);
    }

    public override Guid GetGuid(int ordinal)
    {
        return _inner.GetGuid(ordinal);
    }

    public override short GetInt16(int ordinal)
    {
        return _inner.GetInt16(ordinal);
    }

    public override int GetInt32(int ordinal)
    {
        return _inner.GetInt32(ordinal);
    }

    public override long GetInt64(int ordinal)
    {
        return _inner.GetInt64(ordinal);
    }

    public override string GetString(int ordinal)
    {
        return _inner.GetString(ordinal);
    }

    public override IEnumerator GetEnumerator()
    {
        IEnumerator enumerator = _inner.GetEnumerator();
        while (enumerator.MoveNext())
        {
            _counters.RecordRow();
            yield return enumerator.Current;
        }
    }

    public override void Close()
    {
        _inner.Close();
    }

    public override Task CloseAsync()
    {
        return _inner.CloseAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        return _inner.DisposeAsync();
    }
}
