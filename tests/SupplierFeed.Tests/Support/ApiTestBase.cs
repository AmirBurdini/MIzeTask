using Microsoft.Extensions.DependencyInjection;

namespace SupplierFeed.Tests.Support;

/// <summary>
/// xunit creates one instance per test, so each test gets its own database, clock and hosts.
/// Every host created through <see cref="CreateApp"/> shares that database and clock.
/// </summary>
public abstract class ApiTestBase : IDisposable
{
    private readonly List<TestApp> _apps = new();
    private HttpClient? _client;

    protected TempDatabase Db { get; } = new();

    protected FakeClock Clock { get; } = new();

    /// <summary>Client for a default host, created on first use.</summary>
    protected HttpClient Client => _client ??= CreateApp().CreateClient();

    protected TestApp CreateApp(Action<IServiceCollection>? configure = null)
    {
        var app = new TestApp(Db.FilePath, Clock, configure);
        _apps.Add(app);
        return app;
    }

    /// <summary>Starts several hosts (simulated server instances) on the same database file.</summary>
    protected List<HttpClient> CreateClients(int count) =>
        Enumerable.Range(0, count).Select(_ => CreateApp().CreateClient()).ToList();

    public void Dispose()
    {
        _client?.Dispose();
        foreach (var app in _apps)
        {
            app.Dispose();
        }

        Db.Dispose();
    }
}
