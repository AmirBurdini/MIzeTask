namespace SupplierFeed.Api.Models;

/// <summary>
/// Parsed ingest body. Every field is nullable: a missing, null or wrong-typed
/// field is left null and reported by the validator.
/// </summary>
public sealed class IngestRequest
{
    public string? SupplierId { get; set; }
    public string? ReservationId { get; set; }
    public string? RoomId { get; set; }
    public DateTimeOffset? CheckIn { get; set; }
    public DateTimeOffset? CheckOut { get; set; }
    public double? Price { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}
