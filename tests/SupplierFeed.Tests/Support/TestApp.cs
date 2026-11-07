using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SupplierFeed.Api.Infrastructure;

namespace SupplierFeed.Tests.Support;

/// <summary>
/// One in-process instance of the API pointed at a given SQLite file with a given clock.
/// Several instances can share a file and a clock to simulate multiple servers.
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly string _databasePath;
    private readonly FakeClock _clock;
    private readonly Action<IServiceCollection>? _configure;

    public TestApp(string databasePath, FakeClock clock, Action<IServiceCollection>? configure = null)
    {
        _databasePath = databasePath;
        _clock = clock;
        _configure = configure;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = _databasePath }));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(_clock);
            _configure?.Invoke(services);
        });
    }
}
