using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SupplierFeed.Tests.Support;

namespace SupplierFeed.Tests.Functional;

/// <summary>What the endpoint does with a body that is unreadable, empty or too large.</summary>
public class RequestBodyTests : ApiTestBase
{
    private static string Oversized() => TestData.Body(room: new string('x', 70 * 1024));

    [Theory]
    [InlineData("this is not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    public async Task UnreadableBody_Returns400AndIsNotCounted(string body)
    {
        var response = await Client.IngestAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, Db.Scalar<long>("SELECT COUNT(*) FROM request_log"));
    }

    [Fact]
    public async Task BodyOver64KB_Returns413AndIsNotCounted()
    {
        var response = await Client.IngestAsync(Oversized());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0L, Db.Scalar<long>("SELECT COUNT(*) FROM request_log"));
    }

    [Fact]
    public async Task BodyOver64KBWithoutContentLength_Returns413()
    {
        // The in-memory test server fills in Content-Length itself, so a middleware removes it
        // before the controller runs. That forces the cap to be enforced while reading the stream.
        var client = CreateApp(services => services.AddTransient<IStartupFilter, StripContentLengthFilter>())
            .CreateClient();

        var response = await client.PostAsync(TestData.Ingest,
            new StringContent(Oversized(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private sealed class StripContentLengthFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Request.ContentLength = null;
                await nextMiddleware();
            });
            next(app);
        };
    }
}
