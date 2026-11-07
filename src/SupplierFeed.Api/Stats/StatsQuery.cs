using Dapper;
using SupplierFeed.Api.Data;
using SupplierFeed.Api.Models;

namespace SupplierFeed.Api.Stats;

public interface IStatsQuery
{
    bool DatabaseExists();

    /// <summary>Lifetime counters for the supplier; zeroes if the supplier is unknown.</summary>
    Task<SupplierStats> GetAsync(string supplierId);
}

/// <summary>Read side. Uses a read-only connection, so it can never change data or create the file.</summary>
public sealed class StatsQuery : IStatsQuery
{
    private readonly ISqliteConnectionFactory _connections;

    public StatsQuery(ISqliteConnectionFactory connections)
    {
        _connections = connections;
    }

    public bool DatabaseExists() => _connections.DatabaseExists();

    public async Task<SupplierStats> GetAsync(string supplierId)
    {
        using var connection = _connections.OpenReadOnly();
        var row = await connection.QuerySingleOrDefaultAsync<CounterRow>(
            "SELECT ingested, throttled FROM supplier_stats WHERE supplier_id = @supplierId",
            new { supplierId });

        return new SupplierStats(supplierId, row?.Ingested ?? 0, row?.Throttled ?? 0);
    }

    private sealed class CounterRow
    {
        public long Ingested { get; set; }
        public long Throttled { get; set; }
    }
}
