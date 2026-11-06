using Dapper;
using Microsoft.Data.Sqlite;
using SupplierFeed.Api.Models;

namespace SupplierFeed.Api.Ingest;

/// <summary>Reservation reads and writes. Runs inside a caller-owned transaction.</summary>
public sealed class ReservationStore
{
    public Task<Reservation?> GetAsync(
        SqliteConnection connection, SqliteTransaction transaction, string supplierId, string reservationId) =>
        connection.QuerySingleOrDefaultAsync<Reservation>(
            """
            SELECT supplier_id, reservation_id, room_id, check_in, check_out, price, updated_at_utc
            FROM reservations
            WHERE supplier_id = @supplierId AND reservation_id = @reservationId
            """,
            new { supplierId, reservationId }, transaction);

    public Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction, Reservation reservation) =>
        connection.ExecuteAsync(
            """
            INSERT INTO reservations
                (supplier_id, reservation_id, room_id, check_in, check_out, price, updated_at_utc)
            VALUES
                (@SupplierId, @ReservationId, @RoomId, @CheckIn, @CheckOut, @Price, @UpdatedAtUtc)
            """,
            reservation, transaction);

    public Task UpdateAsync(SqliteConnection connection, SqliteTransaction transaction, Reservation reservation) =>
        connection.ExecuteAsync(
            """
            UPDATE reservations
            SET room_id = @RoomId, check_in = @CheckIn, check_out = @CheckOut,
                price = @Price, updated_at_utc = @UpdatedAtUtc
            WHERE supplier_id = @SupplierId AND reservation_id = @ReservationId
            """,
            reservation, transaction);
}
