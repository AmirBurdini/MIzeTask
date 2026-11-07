using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SupplierFeed.Api.Ingest;
using SupplierFeed.Api.Models;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Functional;

public class ErrorMappingTests : ApiTestBase
{
    private HttpClient ClientWhereIngestThrows(Exception exception) =>
        CreateApp(services =>
        {
            services.RemoveAll<IIngestService>();
            services.AddSingleton<IIngestService>(new ThrowingIngestService(exception));
        }).CreateClient();

    [Fact]
    public async Task UnhandledException_Returns500WithoutDetails()
    {
        var client = ClientWhereIngestThrows(new InvalidOperationException("secret internal detail"));

        var response = await client.IngestAsync(TestData.Body());
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("secret internal detail", body);
        Assert.DoesNotContain("InvalidOperationException", body);
    }

    [Fact]
    public async Task SqliteBusy_Returns503()
    {
        const int sqliteBusy = 5;
        var client = ClientWhereIngestThrows(new SqliteException("database is locked", sqliteBusy));

        var response = await client.IngestAsync(TestData.Body());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed class ThrowingIngestService : IIngestService
    {
        private readonly Exception _exception;

        public ThrowingIngestService(Exception exception)
        {
            _exception = exception;
        }

        public Task<IngestResult> IngestAsync(
            string supplierId, IngestRequest request, IReadOnlyList<string> validationErrors) =>
            throw _exception;
    }
}
