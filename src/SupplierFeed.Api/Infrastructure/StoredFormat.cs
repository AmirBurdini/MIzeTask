using System.Globalization;

namespace SupplierFeed.Api.Infrastructure;

/// <summary>
/// Fixed-width UTC text used for every stored timestamp, so ordinal string
/// ordering equals chronological ordering.
/// </summary>
public static class StoredFormat
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);
}
