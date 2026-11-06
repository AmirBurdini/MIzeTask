using System.Globalization;
using System.Text.Json;
using SupplierFeed.Api.Models;

namespace SupplierFeed.Api.Ingest;

/// <param name="SupplierId">
/// Non-null only when the body is a JSON object whose "supplierId" is a non-blank string.
/// Without it the request cannot be attributed to a supplier, so it is never throttled.
/// </param>
/// <param name="Request">Null when the body is not a JSON object.</param>
public sealed record ParsedIngest(string? SupplierId, IngestRequest? Request);

/// <summary>
/// Reads the raw body field by field instead of using model binding, so the throttle
/// can run before (and independently of) type validation. A missing, null or
/// wrong-typed field is left null; the validator reports it.
/// </summary>
public sealed class IngestRequestParser
{
    private const DateTimeStyles UtcStyles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    public ParsedIngest Parse(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return new ParsedIngest(null, null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ParsedIngest(null, null);
            }

            var request = new IngestRequest
            {
                SupplierId = ReadString(root, "supplierId"),
                ReservationId = ReadString(root, "reservationId"),
                RoomId = ReadString(root, "roomId"),
                CheckIn = ReadDate(root, "checkIn"),
                CheckOut = ReadDate(root, "checkOut"),
                Price = ReadDouble(root, "price"),
                UpdatedAtUtc = ReadDate(root, "updatedAtUtc")
            };

            var supplierId = string.IsNullOrWhiteSpace(request.SupplierId) ? null : request.SupplierId;
            return new ParsedIngest(supplierId, request);
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadDate(JsonElement root, string name)
    {
        var text = ReadString(root, name);
        return text is not null
               && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, UtcStyles, out var parsed)
            ? parsed
            : null;
    }

    private static double? ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number)
            ? number
            : null;
}
