using SupplierFeed.Api.Infrastructure;

namespace SupplierFeed.Tests.Unit;

public class StoredFormatTests
{
    [Fact]
    public void SameInstantInDifferentOffsets_FormatsIdentically()
    {
        var withOffset = new DateTimeOffset(2026, 8, 1, 2, 0, 0, TimeSpan.FromHours(2));
        var zulu = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(StoredFormat.Timestamp(zulu), StoredFormat.Timestamp(withOffset));
        Assert.Equal("2026-08-01T00:00:00.0000000Z", StoredFormat.Timestamp(zulu));
    }

    [Theory]
    [InlineData("2026-07-01T10:00:00.2500000Z", "2026-07-01T10:00:00.5000000Z")]
    [InlineData("2026-07-01T10:00:59.0000000Z", "2026-07-01T10:01:00.0000000Z")]
    [InlineData("2026-12-31T23:59:59.0000000Z", "2027-01-01T00:00:00.0000000Z")]
    public void EarlierInstant_SortsBeforeLaterAsText(string earlier, string later)
    {
        var earlierText = StoredFormat.Timestamp(DateTimeOffset.Parse(earlier));
        var laterText = StoredFormat.Timestamp(DateTimeOffset.Parse(later));

        Assert.True(string.CompareOrdinal(earlierText, laterText) < 0);
    }
}
