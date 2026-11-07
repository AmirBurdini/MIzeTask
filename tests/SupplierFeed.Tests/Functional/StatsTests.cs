using System.Net;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Functional;

public class StatsTests : ApiTestBase
{
    [Fact]
    public async Task Ingested_CountsCreatedAndUpdated()
    {
        await Client.IngestAsync(TestData.Body(price: "450")); // created
        await Client.IngestAsync(TestData.Body(price: "500")); // updated

        var stats = await Client.GetStatsAsync("acme");

        Assert.Equal(new StatsResponse(HttpStatusCode.OK, 2, 0), stats);
    }

    [Fact]
    public async Task UnchangedStaleAndInvalid_AreNotIngested()
    {
        await Client.IngestAsync(TestData.Body());                                         // created
        await Client.IngestAsync(TestData.Body());                                         // unchanged
        await Client.IngestAsync(TestData.Body(updated: "2026-06-01T10:15:00Z"));          // stale
        await Client.IngestAsync(TestData.InvalidBody());                                  // invalid

        var stats = await Client.GetStatsAsync("acme");

        Assert.Equal(1, stats.Ingested);
        Assert.Equal(0, stats.Throttled);
    }

    [Fact]
    public async Task Throttled_MatchesNumberOf429s()
    {
        var codes = await Client.IngestManyAsync("acme", 103);

        var stats = await Client.GetStatsAsync("acme");

        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.TooManyRequests));
        Assert.Equal(100, stats.Ingested);
        Assert.Equal(3, stats.Throttled);
    }

    [Fact]
    public async Task OnlyInvalidThenThrottled_ShowsZeroIngestedAndPositiveThrottled()
    {
        await Client.IngestInvalidManyAsync("acme", 100);
        await Client.IngestManyAsync("acme", 4);

        var stats = await Client.GetStatsAsync("acme");

        Assert.Equal(new StatsResponse(HttpStatusCode.OK, 0, 4), stats);
    }

    [Fact]
    public async Task Counters_SurviveThrottleWindowExpiry()
    {
        await Client.IngestManyAsync("acme", 5);

        Clock.Advance(TimeSpan.FromMinutes(5)); // all log rows expire and are pruned on the next request
        await Client.IngestManyAsync("acme", 1, firstIndex: 5);

        Assert.Equal(6, (await Client.GetStatsAsync("acme")).Ingested);
    }

    [Fact]
    public async Task UnknownSupplier_ReturnsZeroes()
    {
        var stats = await Client.GetStatsAsync("nobody");

        Assert.Equal(new StatsResponse(HttpStatusCode.OK, 0, 0), stats);
    }

    [Fact]
    public async Task UnknownSupplier_LookupCreatesNoRow()
    {
        await Client.GetStatsAsync("ghost");
        await Client.GetStatsAsync("ghost");

        Assert.Equal(0L, Db.Scalar<long>("SELECT COUNT(*) FROM supplier_stats"));
    }

    [Fact]
    public async Task MissingDatabaseFile_Returns503AndCreatesNoFile()
    {
        var client = Client; // starts the host, which creates the schema
        Db.DeleteFiles();

        var response = await client.GetAsync("/api/reservations/stats/acme");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(File.Exists(Db.FilePath));
    }
}
