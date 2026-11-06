using Dapper;
using Microsoft.Data.Sqlite;

namespace SupplierFeed.Api.Ingest;

public sealed class StatsCounters
{
    /// <summary>
    /// Adds to the supplier's lifetime counters in SQL (never read-then-write), so concurrent
    /// updates cannot lose increments. Runs inside a caller-owned transaction.
    /// </summary>
    public Task AddAsync(
        SqliteConnection connection, SqliteTransaction transaction, string supplierId, int ingested, int throttled) =>
        connection.ExecuteAsync(
            """
            INSERT INTO supplier_stats (supplier_id, ingested, throttled)
            VALUES (@supplierId, @ingested, @throttled)
            ON CONFLICT (supplier_id) DO UPDATE SET
                ingested = ingested + excluded.ingested,
                throttled = throttled + excluded.throttled
            """,
            new { supplierId, ingested, throttled }, transaction);
}
