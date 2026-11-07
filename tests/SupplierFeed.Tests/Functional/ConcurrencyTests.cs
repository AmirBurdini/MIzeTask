using System.Net;
using SupplierFeed.Api.Infrastructure;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Functional;

/// <summary>
/// Several in-process hosts (simulated server instances) share one SQLite file and one clock.
/// The clock never advances, so the 60-second window cannot expire mid-test.
/// </summary>
public class ConcurrencyTests : ApiTestBase
{
    [Fact]
    public async Task ParallelRequestsAcrossHosts_AcceptExactly100()
    {
        var clients = CreateClients(3);

        var codes = await Task.WhenAll(Enumerable.Range(0, 200).Select(i =>
            Task.Run(async () =>
                (await clients[i % 3].IngestAsync(TestData.Body("acme", $"r{i}"))).StatusCode)));

        Assert.Equal(100, codes.Count(c => c == HttpStatusCode.Created));
        Assert.Equal(100, codes.Count(c => c == HttpStatusCode.TooManyRequests));
        var stats = await clients[0].GetStatsAsync("acme");
        Assert.Equal(100, stats.Ingested);
        Assert.Equal(100, stats.Throttled);
    }

    [Fact]
    public async Task ParallelIdenticalNewReservation_CreatesExactlyOne()
    {
        var clients = CreateClients(3);

        var statuses = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            Task.Run(async () =>
            {
                var response = await clients[i % 3].IngestAsync(TestData.Body());
                return (response.StatusCode, Status: await response.ReadStatusAsync());
            })));

        Assert.Equal(1, statuses.Count(s => s.Status == "created"));
        Assert.Equal(49, statuses.Count(s => s.Status == "unchanged"));
        Assert.DoesNotContain(statuses, s => s.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal(1L, Db.Scalar<long>("SELECT COUNT(*) FROM reservations"));
    }

    [Fact]
    public async Task ParallelUpdates_KeepLatestTimestamp()
    {
        var clients = CreateClients(3);
        var start = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);

        // 100 updates with increasing timestamps, sent in a scrambled parallel order.
        var codes = await Task.WhenAll(Enumerable.Range(1, 100).Select(i =>
            Task.Run(async () =>
            {
                var body = TestData.Body(price: i.ToString(), updated: start.AddMinutes(i).ToString("yyyy-MM-ddTHH:mm:ssZ"));
                return (await clients[i % 3].IngestAsync(body)).StatusCode;
            })));

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, codes);
        Assert.Equal(100.0, Db.Scalar<double>("SELECT price FROM reservations WHERE reservation_id = 'r1'"));
        Assert.Equal(StoredFormat.Timestamp(start.AddMinutes(100)),
            Db.Scalar<string>("SELECT updated_at_utc FROM reservations WHERE reservation_id = 'r1'"));
    }

    [Fact]
    public async Task TwoSuppliersOverLimit_EachGets100()
    {
        var clients = CreateClients(3);

        var results = await Task.WhenAll(Enumerable.Range(0, 300).Select(i =>
            Task.Run(async () =>
            {
                var supplier = i % 2 == 0 ? "acme" : "globex";
                var response = await clients[i % 3].IngestAsync(TestData.Body(supplier, $"r{i}"));
                return (Supplier: supplier, response.StatusCode);
            })));

        foreach (var supplier in new[] { "acme", "globex" })
        {
            Assert.Equal(100, results.Count(r => r.Supplier == supplier && r.StatusCode == HttpStatusCode.Created));
            Assert.Equal(50, results.Count(r => r.Supplier == supplier && r.StatusCode == HttpStatusCode.TooManyRequests));
        }
    }

    [Fact]
    public async Task StatsReadsDuringIngestLoad_NeverFail()
    {
        var clients = CreateClients(2);

        var writes = Enumerable.Range(0, 150).Select(i =>
            Task.Run(async () => (await clients[0].IngestAsync(TestData.Body("acme", $"r{i}"))).StatusCode));
        var reads = Enumerable.Range(0, 100).Select(_ =>
            Task.Run(async () => (await clients[1].GetStatsAsync("acme")).Code));

        var readCodes = await Task.WhenAll(reads);
        await Task.WhenAll(writes);

        Assert.All(readCodes, code => Assert.Equal(HttpStatusCode.OK, code));
    }

    [Fact]
    public async Task HostsStartingTogether_InitialiseSchemaWithoutErrors()
    {
        var apps = new[] { CreateApp(), CreateApp(), CreateApp() };

        var clients = await Task.WhenAll(apps.Select(app => Task.Run(app.CreateClient)));

        foreach (var client in clients)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetStatsAsync("acme")).Code);
        }
    }
}
