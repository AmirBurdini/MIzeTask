using System.Net;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Functional;

public class DedupTests : ApiTestBase
{
    [Fact]
    public async Task NewReservation_Returns201Created()
    {
        var response = await Client.IngestAsync(TestData.Body());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("created", await response.ReadStatusAsync());
    }

    [Fact]
    public async Task IdenticalResend_Returns200UnchangedWithoutWriting()
    {
        await Client.IngestAsync(TestData.Body());
        Db.AuditReservationWrites();

        var response = await Client.IngestAsync(TestData.Body());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("unchanged", await response.ReadStatusAsync());
        Assert.Equal(0L, Db.ReservationWrites());
    }

    [Fact]
    public async Task SameTimestampDifferentPrice_UpdatesRecord()
    {
        await Client.IngestAsync(TestData.Body(price: "450"));

        var response = await Client.IngestAsync(TestData.Body(price: "500"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("updated", await response.ReadStatusAsync());
        Assert.Equal(500.0, Db.Scalar<double>("SELECT price FROM reservations WHERE reservation_id = 'r1'"));
    }

    [Fact]
    public async Task NewerTimestamp_UpdatesRecord()
    {
        await Client.IngestAsync(TestData.Body(price: "450", updated: "2026-07-01T10:15:00Z"));

        var response = await Client.IngestAsync(TestData.Body(price: "480", updated: "2026-07-02T10:15:00Z"));

        Assert.Equal("updated", await response.ReadStatusAsync());
        Assert.Equal(480.0, Db.Scalar<double>("SELECT price FROM reservations WHERE reservation_id = 'r1'"));
        Assert.StartsWith("2026-07-02T10:15:00",
            Db.Scalar<string>("SELECT updated_at_utc FROM reservations WHERE reservation_id = 'r1'"));
    }

    [Fact]
    public async Task OlderTimestamp_ReturnsStaleAndKeepsStoredValue()
    {
        await Client.IngestAsync(TestData.Body(price: "450", updated: "2026-07-01T10:15:00Z"));
        Db.AuditReservationWrites();

        var response = await Client.IngestAsync(TestData.Body(price: "999", updated: "2026-06-01T10:15:00Z"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("stale", await response.ReadStatusAsync());
        Assert.Equal(450.0, Db.Scalar<double>("SELECT price FROM reservations WHERE reservation_id = 'r1'"));
        Assert.Equal(0L, Db.ReservationWrites());
    }

    [Fact]
    public async Task SameInstantInOtherOffset_IsUnchanged()
    {
        await Client.IngestAsync(TestData.Body(updated: "2026-07-01T10:15:00Z", checkIn: "2026-08-01T00:00:00Z"));

        var response = await Client.IngestAsync(
            TestData.Body(updated: "2026-07-01T12:15:00+02:00", checkIn: "2026-08-01T02:00:00+02:00"));

        Assert.Equal("unchanged", await response.ReadStatusAsync());
    }

    [Fact]
    public async Task SameReservationIdForTwoSuppliers_CreatesBoth()
    {
        var first = await Client.IngestAsync(TestData.Body(supplier: "acme", reservation: "shared"));
        var second = await Client.IngestAsync(TestData.Body(supplier: "globex", reservation: "shared"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(2L, Db.Scalar<long>("SELECT COUNT(*) FROM reservations"));
    }

    [Fact]
    public async Task SupplierIdCaseDiffers_IsSeparateSupplier()
    {
        var lower = await Client.IngestAsync(TestData.Body(supplier: "acme"));
        var upper = await Client.IngestAsync(TestData.Body(supplier: "Acme"));

        Assert.Equal(HttpStatusCode.Created, lower.StatusCode);
        Assert.Equal(HttpStatusCode.Created, upper.StatusCode);
        Assert.Equal(1, (await Client.GetStatsAsync("acme")).Ingested);
        Assert.Equal(1, (await Client.GetStatsAsync("Acme")).Ingested);
    }

    [Theory]
    [InlineData("room")]
    [InlineData("checkIn")]
    [InlineData("checkOut")]
    public async Task SameTimestampChangedRoomOrDates_UpdatesRecord(string changed)
    {
        await Client.IngestAsync(TestData.Body());

        var variant = changed switch
        {
            "room" => TestData.Body(room: "202"),
            "checkIn" => TestData.Body(checkIn: "2026-08-02T00:00:00Z"),
            _ => TestData.Body(checkOut: "2026-08-04T00:00:00Z")
        };
        var response = await Client.IngestAsync(variant);

        Assert.Equal("updated", await response.ReadStatusAsync());
    }
}
