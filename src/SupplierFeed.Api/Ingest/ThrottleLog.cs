using Dapper;
using Microsoft.Data.Sqlite;
using SupplierFeed.Api.Infrastructure;

namespace SupplierFeed.Api.Ingest;

/// <summary>
/// Sliding-window log shared by every instance through the database. All methods run
/// inside a transaction owned by the caller (which holds the write lock).
/// </summary>
public sealed class ThrottleLog
{
    public const int MaxRequestsPerWindow = 100;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Prunes expired rows (all suppliers), logs this request, and returns how many of the
    /// supplier's requests fall in the window, including this one. The window is the rows
    /// newer than <c>now - 60s</c>.
    /// </summary>
    public async Task<int> RecordAndCountAsync(
        SqliteConnection connection, SqliteTransaction transaction, string supplierId, DateTimeOffset now)
    {
        var cutoff = StoredFormat.Timestamp(now - Window);

        await connection.ExecuteAsync(
            "DELETE FROM request_log WHERE requested_at_utc <= @cutoff",
            new { cutoff }, transaction);

        await connection.ExecuteAsync(
            "INSERT INTO request_log (supplier_id, requested_at_utc) VALUES (@supplierId, @requestedAt)",
            new { supplierId, requestedAt = StoredFormat.Timestamp(now) }, transaction);

        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM request_log WHERE supplier_id = @supplierId AND requested_at_utc > @cutoff",
            new { supplierId, cutoff }, transaction);
    }
}
