namespace SupplierFeed.Api.Models;

/// <summary>
/// Row of the reservations table. Times are stored text (see StoredFormat).
/// Property names map to snake_case columns via Dapper's MatchNamesWithUnderscores.
/// </summary>
public sealed class Reservation
{
    public string SupplierId { get; set; } = "";
    public string ReservationId { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string CheckIn { get; set; } = "";
    public string CheckOut { get; set; } = "";
    public double Price { get; set; }
    public string UpdatedAtUtc { get; set; } = "";
}
