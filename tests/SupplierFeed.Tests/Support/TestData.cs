using System.Net;
using System.Text;
using System.Text.Json;

namespace SupplierFeed.Tests.Support;

public sealed record StatsResponse(HttpStatusCode Code, long Ingested, long Throttled);

public static class TestData
{
    public const string Ingest = "/api/reservations/ingest";

    /// <summary>Builds an ingest body. <paramref name="price"/> is raw JSON so tests can pass bad types.</summary>
    public static string Body(
        string supplier = "acme",
        string reservation = "r1",
        string room = "101",
        string checkIn = "2026-08-01T00:00:00Z",
        string checkOut = "2026-08-03T00:00:00Z",
        string price = "450",
        string updated = "2026-07-01T10:15:00Z") =>
        $$"""{"supplierId":"{{supplier}}","reservationId":"{{reservation}}","roomId":"{{room}}","checkIn":"{{checkIn}}","checkOut":"{{checkOut}}","price":{{price}},"updatedAtUtc":"{{updated}}"}""";

    /// <summary>A body that fails validation (checkOut before checkIn) but names a supplier.</summary>
    public static string InvalidBody(string supplier = "acme", string reservation = "r1") =>
        Body(supplier, reservation, checkOut: "2026-07-01T00:00:00Z");

    public static Task<HttpResponseMessage> IngestAsync(this HttpClient client, string json) =>
        client.PostAsync(Ingest, new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>Sends <paramref name="count"/> valid, distinct reservations one after another.</summary>
    public static async Task<List<HttpStatusCode>> IngestManyAsync(
        this HttpClient client, string supplier, int count, int firstIndex = 0)
    {
        var codes = new List<HttpStatusCode>(count);
        for (var i = firstIndex; i < firstIndex + count; i++)
        {
            codes.Add((await client.IngestAsync(Body(supplier, reservation: $"r{i}"))).StatusCode);
        }

        return codes;
    }

    /// <summary>Sends <paramref name="count"/> invalid requests one after another.</summary>
    public static async Task<List<HttpStatusCode>> IngestInvalidManyAsync(
        this HttpClient client, string supplier, int count)
    {
        var codes = new List<HttpStatusCode>(count);
        for (var i = 0; i < count; i++)
        {
            codes.Add((await client.IngestAsync(InvalidBody(supplier, $"r{i}"))).StatusCode);
        }

        return codes;
    }

    public static async Task<string?> ReadStatusAsync(this HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("status", out var status) ? status.GetString() : null;
    }

    public static async Task<StatsResponse> GetStatsAsync(this HttpClient client, string supplier)
    {
        var response = await client.GetAsync($"/api/reservations/stats/{supplier}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        return new StatsResponse(
            response.StatusCode,
            root.TryGetProperty("ingested", out var ingested) ? ingested.GetInt64() : -1,
            root.TryGetProperty("throttled", out var throttled) ? throttled.GetInt64() : -1);
    }
}
