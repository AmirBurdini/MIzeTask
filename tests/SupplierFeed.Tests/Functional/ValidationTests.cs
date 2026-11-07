using System.Net;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Functional;

/// <summary>Field-level rules on an otherwise readable request from an identifiable supplier.</summary>
public class ValidationTests : ApiTestBase
{
    [Fact]
    public async Task CheckInAfterCheckOut_Returns400AndStoresNothing()
    {
        var response = await Client.IngestAsync(TestData.InvalidBody());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("checkIn must be earlier than checkOut", await response.Content.ReadAsStringAsync());
        Assert.Equal(0L, Db.Scalar<long>("SELECT COUNT(*) FROM reservations"));
    }

    [Fact]
    public async Task NegativePrice_Returns400AndStoresNothing()
    {
        var response = await Client.IngestAsync(TestData.Body(price: "-1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("price", await response.Content.ReadAsStringAsync());
        Assert.Equal(0L, Db.Scalar<long>("SELECT COUNT(*) FROM reservations"));
    }

    [Fact]
    public async Task WrongTypePrice_Returns400AndStoresNothing()
    {
        var response = await Client.IngestAsync(TestData.Body(price: "\"abc\""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("price", await response.Content.ReadAsStringAsync());
        Assert.Equal(0L, Db.Scalar<long>("SELECT COUNT(*) FROM reservations"));
    }

    [Theory]
    [InlineData("10:15")]
    [InlineData("7/1/2026")]
    public async Task NonIsoCheckIn_Returns400AndStoresNothing(string checkIn)
    {
        // A far-future checkOut: if the bad value were parsed as a date it could not fail the ordering check.
        var response = await Client.IngestAsync(TestData.Body(checkIn: checkIn, checkOut: "2099-01-01T00:00:00Z"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("checkIn is required and must be an ISO-8601", await response.Content.ReadAsStringAsync());
        Assert.Equal(0L, Db.Scalar<long>("SELECT COUNT(*) FROM reservations"));
    }

    [Fact]
    public async Task InvalidRequestAgainstExistingReservation_LeavesItUntouched()
    {
        await Client.IngestAsync(TestData.Body(price: "450"));

        var response = await Client.IngestAsync(TestData.Body(price: "-1", updated: "2026-07-05T10:15:00Z"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(450.0, Db.Scalar<double>("SELECT price FROM reservations WHERE reservation_id = 'r1'"));
    }
}
