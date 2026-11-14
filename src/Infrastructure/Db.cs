using System;
using System.Collections.Generic;
using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace OpenForge.Infrastructure;

public sealed class Db : IDisposable
{
    private readonly ForgeOptions _options;
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;

    static Db()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public Db(ForgeOptions options)
    {
        _options = options;
    }

    public SqliteConnection Connection
    {
        get
        {
            _connection ??= Open(_options.RegistryPath);
            return _connection;
        }
    }

    public static SqliteConnection Open(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            DefaultTimeout = 20,
            Pooling = true,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        connection.Execute("PRAGMA foreign_keys=ON");

        return connection;
    }

    public void BeginImmediate()
    {
        if (_transaction != null)
            return;

        _transaction = Connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
    }

    public void Commit()
    {
        _transaction?.Commit();
        _transaction?.Dispose();
        _transaction = null;
    }

    public void Rollback()
    {
        _transaction?.Rollback();
        _transaction?.Dispose();
        _transaction = null;
    }

    public IEnumerable<T> Query<T>(string sql, object? param = null)
    {
        return Connection.Query<T>(sql, param, _transaction);
    }

    public T? One<T>(string sql, object? param = null)
    {
        return Connection.QueryFirstOrDefault<T>(sql, param, _transaction);
    }

    public T Scalar<T>(string sql, object? param = null)
    {
        return Connection.ExecuteScalar<T>(sql, param, _transaction)!;
    }

    public int Exec(string sql, object? param = null)
    {
        return Connection.Execute(sql, param, _transaction);
    }

    public bool Exists(string sql, object? param = null)
    {
        object? value = Connection.ExecuteScalar(sql, param, _transaction);
        return value != null && value is not DBNull;
    }

    public long LastInsertId()
    {
        return Connection.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: _transaction);
    }

    public void Dispose()
    {
        try
        {
            _transaction?.Rollback();
        }
        catch (Exception)
        {
        }

        _transaction?.Dispose();
        _connection?.Dispose();
    }
}
