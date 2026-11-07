using SupplierFeed.Api.Infrastructure;

namespace SupplierFeed.Tests.Support;

/// <summary>Manually advanced clock, so 60-second window tests run instantly.</summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan amount) => UtcNow += amount;
}
