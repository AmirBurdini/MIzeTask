using SupplierFeed.Api.Data;
using SupplierFeed.Api.Infrastructure;
using SupplierFeed.Api.Models;

namespace SupplierFeed.Api.Ingest;

public interface IIngestService
{
    /// <param name="validationErrors">
    /// Result of in-memory validation, computed before any lock is taken. A non-empty list
    /// still logs the request (it counts toward the limit) but writes no reservation.
    /// </param>
    Task<IngestResult> IngestAsync(string supplierId, IngestRequest request, IReadOnlyList<string> validationErrors);
}

public sealed class IngestService : IIngestService
{
    private static readonly IReadOnlyList<string> NoErrors = Array.Empty<string>();

    private readonly ISqliteConnectionFactory _connections;
    private readonly IClock _clock;
    private readonly ThrottleLog _throttleLog;
    private readonly ReservationStore _reservations;
    private readonly StatsCounters _counters;

    public IngestService(
        ISqliteConnectionFactory connections,
        IClock clock,
        ThrottleLog throttleLog,
        ReservationStore reservations,
        StatsCounters counters)
    {
        _connections = connections;
        _clock = clock;
        _throttleLog = throttleLog;
        _reservations = reservations;
        _counters = counters;
    }

    public async Task<IngestResult> IngestAsync(
        string supplierId, IngestRequest request, IReadOnlyList<string> validationErrors)
    {
        using var connection = _connections.OpenWrite();
        // deferred: false = BEGIN IMMEDIATE. The write lock is taken up front, so the
        // read-then-write below cannot interleave with another instance. A deferred
        // transaction could fail on lock upgrade with a busy error that is not retried.
        using var transaction = connection.BeginTransaction(deferred: false);

        // Read the clock only once the lock is held. A request can wait up to the busy timeout
        // (30 s) for the lock; a time taken before the wait would be logged too early, so its
        // row would expire from the window earlier than it should.
        var now = _clock.UtcNow;

        var count = await _throttleLog.RecordAndCountAsync(connection, transaction, supplierId, now);
        if (count > ThrottleLog.MaxRequestsPerWindow)
        {
            await _counters.AddAsync(connection, transaction, supplierId, ingested: 0, throttled: 1);
            transaction.Commit();
            return new IngestResult(IngestStatus.Throttled, NoErrors);
        }

        if (validationErrors.Count > 0)
        {
            transaction.Commit(); // the log row stays: invalid requests count toward the limit
            return new IngestResult(IngestStatus.Invalid, validationErrors);
        }

        var incoming = new Reservation
        {
            SupplierId = supplierId,
            ReservationId = request.ReservationId!,
            RoomId = request.RoomId!,
            CheckIn = StoredFormat.Timestamp(request.CheckIn!.Value),
            CheckOut = StoredFormat.Timestamp(request.CheckOut!.Value),
            Price = request.Price!.Value,
            UpdatedAtUtc = StoredFormat.Timestamp(request.UpdatedAtUtc!.Value)
        };

        var stored = await _reservations.GetAsync(connection, transaction, supplierId, incoming.ReservationId);

        IngestStatus status;
        if (stored is null)
        {
            await _reservations.InsertAsync(connection, transaction, incoming);
            status = IngestStatus.Created;
        }
        else if (string.CompareOrdinal(stored.UpdatedAtUtc, incoming.UpdatedAtUtc) > 0)
        {
            status = IngestStatus.Stale; // older than what we hold: ignored, no write
        }
        else if (SameDetails(stored, incoming))
        {
            status = IngestStatus.Unchanged; // identical resend: no write
        }
        else
        {
            // Newer, or equal timestamp with different details (last arrival wins on a tie).
            await _reservations.UpdateAsync(connection, transaction, incoming);
            status = IngestStatus.Updated;
        }

        if (status is IngestStatus.Created or IngestStatus.Updated)
        {
            await _counters.AddAsync(connection, transaction, supplierId, ingested: 1, throttled: 0);
        }

        transaction.Commit();
        return new IngestResult(status, NoErrors);
    }

    private static bool SameDetails(Reservation a, Reservation b) =>
        a.RoomId == b.RoomId
        && a.CheckIn == b.CheckIn
        && a.CheckOut == b.CheckOut
        && a.Price == b.Price
        && a.UpdatedAtUtc == b.UpdatedAtUtc;
}
