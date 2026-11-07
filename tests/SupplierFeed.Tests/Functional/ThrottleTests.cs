using System.Net;
using SupplierFeed.Api.Infrastructure;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Functional;

public class ThrottleTests : ApiTestBase
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Exactly100Requests_AreAllAccepted()
    {
        var codes = await Client.IngestManyAsync("acme", 100);

        Assert.All(codes, code => Assert.Equal(HttpStatusCode.Created, code));
    }

    [Fact]
    public async Task Request101_Returns429()
    {
        await Client.IngestManyAsync("acme", 100);

        var response = await Client.IngestAsync(TestData.Body("acme", "r100"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task RejectedRequests_CountAndKeepSupplierBlocked()
    {
        await Client.IngestManyAsync("acme", 100); // t0: fills the window

        Clock.Advance(59 * Second);
        var rejected = await Client.IngestManyAsync("acme", 100, firstIndex: 100); // logged although rejected
        Assert.All(rejected, code => Assert.Equal(HttpStatusCode.TooManyRequests, code));

        Clock.Advance(2 * Second); // t0 + 61s: the original 100 have aged out, the rejected ones have not
        var next = await Client.IngestManyAsync("acme", 1, firstIndex: 200);

        Assert.Equal(HttpStatusCode.TooManyRequests, next.Single());
    }

    [Fact]
    public async Task After60QuietSeconds_SupplierIsAcceptedAgain()
    {
        await Client.IngestManyAsync("acme", 101); // the 101st is rejected

        Clock.Advance(61 * Second);
        var next = await Client.IngestManyAsync("acme", 1, firstIndex: 500);

        Assert.Equal(HttpStatusCode.Created, next.Single());
    }

    [Fact]
    public async Task WindowSlides_UntilOldRequestsAgeOut()
    {
        Assert.All(await Client.IngestManyAsync("acme", 60), c => Assert.Equal(HttpStatusCode.Created, c));

        Clock.Advance(30 * Second);
        Assert.All(await Client.IngestManyAsync("acme", 40, firstIndex: 60),
            c => Assert.Equal(HttpStatusCode.Created, c));
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await Client.IngestManyAsync("acme", 1, firstIndex: 100)).Single()); // 101st in the window

        Clock.Advance(31 * Second); // the first 60 aged out; 40 accepted + 1 rejected remain
        Assert.All(await Client.IngestManyAsync("acme", 59, firstIndex: 101),
            c => Assert.Equal(HttpStatusCode.Created, c));
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await Client.IngestManyAsync("acme", 1, firstIndex: 160)).Single());
    }

    [Theory]
    [InlineData("checkOutBeforeCheckIn")] // fails validation
    [InlineData("wrongTypePrice")]        // fails before validation, in the parser
    public async Task InvalidRequests_UseUpQuota(string kind)
    {
        for (var i = 0; i < 100; i++)
        {
            var body = kind == "wrongTypePrice"
                ? TestData.Body("acme", $"r{i}", price: "\"abc\"")
                : TestData.InvalidBody("acme", $"r{i}");
            Assert.Equal(HttpStatusCode.BadRequest, (await Client.IngestAsync(body)).StatusCode);
        }

        var response = await Client.IngestAsync(TestData.Body("acme", "fresh"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task MissingSupplierId_Returns400AndIsNeverThrottled()
    {
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 105; i++)
        {
            codes.Add((await Client.IngestAsync("""{"reservationId":"r1"}""")).StatusCode);
        }

        Assert.All(codes, code => Assert.Equal(HttpStatusCode.BadRequest, code));
        Assert.Equal(0L, Db.Scalar<long>("SELECT COUNT(*) FROM request_log"));
    }

    [Fact]
    public async Task Before60Seconds_SupplierIsStillBlocked()
    {
        await Client.IngestManyAsync("acme", 100);

        Clock.Advance(59 * Second);
        var next = await Client.IngestManyAsync("acme", 1, firstIndex: 100);

        Assert.Equal(HttpStatusCode.TooManyRequests, next.Single());
    }

    [Fact]
    public async Task UnchangedAndStaleResends_UseUpQuota()
    {
        Assert.Equal(HttpStatusCode.Created, (await Client.IngestAsync(TestData.Body())).StatusCode);
        for (var i = 0; i < 49; i++)
        {
            Assert.Equal("unchanged", await (await Client.IngestAsync(TestData.Body())).ReadStatusAsync());
        }

        for (var i = 0; i < 50; i++)
        {
            var stale = await Client.IngestAsync(TestData.Body(updated: "2026-06-01T10:15:00Z"));
            Assert.Equal("stale", await stale.ReadStatusAsync());
        }

        var response = await Client.IngestAsync(TestData.Body());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task ThrottledSupplier_DoesNotAffectOthers()
    {
        await Client.IngestManyAsync("acme", 100);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await Client.IngestAsync(TestData.Body("acme", "extra"))).StatusCode);

        var other = await Client.IngestAsync(TestData.Body("globex"));

        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    [Fact]
    public async Task ThrottledValidRequest_WritesNoReservation()
    {
        await Client.IngestManyAsync("acme", 100);

        var throttled = await Client.IngestAsync(TestData.Body("acme", "rnew"));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal(0L, Db.Scalar<long>("SELECT COUNT(*) FROM reservations WHERE reservation_id = 'rnew'"));

        Clock.Advance(61 * Second);
        var retried = await Client.IngestAsync(TestData.Body("acme", "rnew"));

        Assert.Equal("created", await retried.ReadStatusAsync()); // not "unchanged": it was never stored
    }

    [Fact]
    public async Task RequestWaitingForLock_IsLoggedWithTimeAfterTheWait()
    {
        await Client.GetStatsAsync("warmup"); // starts the host so the schema exists
        var lockHandle = Db.HoldWriteLock();

        var request = Task.Run(() => Client.IngestAsync(TestData.Body()));
        await Task.Delay(500);          // the request has arrived and is now blocked on the write lock
        Clock.Advance(59 * Second);     // time passes while it waits
        lockHandle.Dispose();
        var response = await request;

        // A timestamp taken before the wait would make the row expire up to 59s too early.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(StoredFormat.Timestamp(Clock.UtcNow),
            Db.Scalar<string>("SELECT requested_at_utc FROM request_log"));
    }

    [Fact]
    public async Task ExpiredLogRows_ArePrunedOnNextRequest()
    {
        await Client.IngestManyAsync("acme", 5);
        Assert.Equal(5L, Db.Scalar<long>("SELECT COUNT(*) FROM request_log"));

        Clock.Advance(61 * Second);
        await Client.IngestManyAsync("acme", 1, firstIndex: 5);

        Assert.Equal(1L, Db.Scalar<long>("SELECT COUNT(*) FROM request_log"));
    }
}
