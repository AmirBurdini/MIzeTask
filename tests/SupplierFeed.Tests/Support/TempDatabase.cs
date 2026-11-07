using Dapper;
using Microsoft.Data.Sqlite;

namespace SupplierFeed.Tests.Support;

/// <summary>A unique SQLite file in a temp folder, deleted on dispose, with direct query helpers.</summary>
public sealed class TempDatabase : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "supplierfeed-tests", Guid.NewGuid().ToString("N"));

    public TempDatabase()
    {
        Directory.CreateDirectory(_directory);
    }

    public string FilePath => Path.Combine(_directory, "test.db");

    /// <summary>Runs a query straight against the file, bypassing the app (no pooling, read-only).</summary>
    public T Scalar<T>(string sql, object? parameters = null)
    {
        using var connection = new SqliteConnection($"Data Source={FilePath};Mode=ReadOnly;Pooling=False");
        return connection.ExecuteScalar<T>(sql, parameters)!;
    }

    /// <summary>Runs a write statement straight against the file, bypassing the app.</summary>
    public void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={FilePath};Pooling=False;Default Timeout=30");
        connection.Execute(sql);
    }

    /// <summary>
    /// Installs triggers that record every INSERT or UPDATE on reservations, even one that rewrites
    /// identical values. Call after the app has created the schema.
    /// </summary>
    public void AuditReservationWrites() => Execute("""
        CREATE TABLE write_audit (id INTEGER PRIMARY KEY AUTOINCREMENT);
        CREATE TRIGGER audit_insert AFTER INSERT ON reservations BEGIN INSERT INTO write_audit (id) VALUES (NULL); END;
        CREATE TRIGGER audit_update AFTER UPDATE ON reservations BEGIN INSERT INTO write_audit (id) VALUES (NULL); END;
        """);

    /// <summary>Number of reservation writes since <see cref="AuditReservationWrites"/>.</summary>
    public long ReservationWrites() => Scalar<long>("SELECT COUNT(*) FROM write_audit");

    /// <summary>Takes the write lock on the file from outside the app; dispose to release it.</summary>
    public IDisposable HoldWriteLock()
    {
        var connection = new SqliteConnection($"Data Source={FilePath};Pooling=False;Default Timeout=30");
        connection.Open();
        connection.Execute("BEGIN IMMEDIATE");
        return new WriteLock(connection);
    }

    private sealed class WriteLock : IDisposable
    {
        private readonly SqliteConnection _connection;

        public WriteLock(SqliteConnection connection)
        {
            _connection = connection;
        }

        public void Dispose()
        {
            try
            {
                _connection.Execute("ROLLBACK");
            }
            finally
            {
                _connection.Dispose();
            }
        }
    }

    /// <summary>Closes pooled connections and removes the database file and its WAL companions.</summary>
    public void DeleteFiles()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(FilePath + suffix);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
