using SupplierFeed.Api.Models;

namespace SupplierFeed.Api.Ingest;

public sealed class IngestRequestValidator
{
    /// <summary>Returns one message per problem; an empty list means the request is valid.</summary>
    public IReadOnlyList<string> Validate(IngestRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.SupplierId))
            errors.Add("supplierId is required and must be a non-blank string.");
        if (string.IsNullOrWhiteSpace(request.ReservationId))
            errors.Add("reservationId is required and must be a non-blank string.");
        if (string.IsNullOrWhiteSpace(request.RoomId))
            errors.Add("roomId is required and must be a non-blank string.");

        if (request.CheckIn is null)
            errors.Add("checkIn is required and must be a date-time.");
        if (request.CheckOut is null)
            errors.Add("checkOut is required and must be a date-time.");
        if (request.UpdatedAtUtc is null)
            errors.Add("updatedAtUtc is required and must be a date-time.");
        if (request.Price is null)
            errors.Add("price is required and must be a number.");

        if (request.CheckIn is { } checkIn && request.CheckOut is { } checkOut && checkIn >= checkOut)
            errors.Add("checkIn must be earlier than checkOut.");
        if (request.Price is < 0)
            errors.Add("price must not be negative.");

        return errors;
    }
}
