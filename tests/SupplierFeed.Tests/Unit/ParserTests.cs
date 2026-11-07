using SupplierFeed.Api.Infrastructure;
using SupplierFeed.Api.Ingest;

namespace SupplierFeed.Tests.Unit;

public class ParserTests
{
    private readonly IngestRequestParser _parser = new();

    private const string ValidBody = """
        {"supplierId":"acme","reservationId":"r1","roomId":"101",
         "checkIn":"2026-08-01T00:00:00Z","checkOut":"2026-08-03T00:00:00Z",
         "price":450.00,"updatedAtUtc":"2026-07-01T10:15:00Z"}
        """;

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("null")]
    [InlineData("{not json")]
    [InlineData("")]
    public void NonObjectOrInvalidJson_HasNoSupplierIdAndNoRequest(string body)
    {
        var parsed = _parser.Parse(body);

        Assert.Null(parsed.SupplierId);
        Assert.Null(parsed.Request);
    }

    [Fact]
    public void BlankSupplierId_IsTreatedAsMissing()
    {
        var parsed = _parser.Parse("""{"supplierId":"   "}""");

        Assert.Null(parsed.SupplierId);
        Assert.NotNull(parsed.Request);
    }

    [Fact]
    public void NonStringSupplierId_IsTreatedAsMissing()
    {
        var parsed = _parser.Parse("""{"supplierId":5}""");

        Assert.Null(parsed.SupplierId);
    }

    [Fact]
    public void ValidBody_PopulatesAllFields()
    {
        var parsed = _parser.Parse(ValidBody);

        Assert.Equal("acme", parsed.SupplierId);
        var request = parsed.Request!;
        Assert.Equal("r1", request.ReservationId);
        Assert.Equal("101", request.RoomId);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), request.CheckIn);
        Assert.Equal(new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero), request.CheckOut);
        Assert.Equal(450.0, request.Price);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 10, 15, 0, TimeSpan.Zero), request.UpdatedAtUtc);
    }

    [Fact]
    public void StringPrice_BecomesNullButKeepsSupplierId()
    {
        var parsed = _parser.Parse("""{"supplierId":"acme","price":"abc"}""");

        Assert.Equal("acme", parsed.SupplierId);
        Assert.Null(parsed.Request!.Price);
    }

    [Fact]
    public void IntegerPrice_IsConvertedToDouble()
    {
        var parsed = _parser.Parse("""{"supplierId":"acme","price":450}""");

        Assert.Equal(450.0, parsed.Request!.Price);
    }

    [Fact]
    public void OffsetAndZuluOfSameInstant_AreEqual()
    {
        var withOffset = _parser.Parse("""{"supplierId":"a","checkIn":"2026-08-01T02:00:00+02:00"}""");
        var zulu = _parser.Parse("""{"supplierId":"a","checkIn":"2026-08-01T00:00:00Z"}""");

        Assert.Equal(zulu.Request!.CheckIn, withOffset.Request!.CheckIn);
        Assert.Equal(TimeSpan.Zero, withOffset.Request.CheckIn!.Value.Offset);
    }

    [Fact]
    public void DateWithoutOffset_IsTreatedAsUtc()
    {
        var parsed = _parser.Parse("""{"supplierId":"a","checkIn":"2026-08-01T00:00:00"}""");

        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), parsed.Request!.CheckIn);
        Assert.Equal(TimeSpan.Zero, parsed.Request.CheckIn!.Value.Offset);
    }

    [Theory]
    [InlineData("10:15")]                  // time only: would silently become today's date
    [InlineData("7/1/2026")]               // ambiguous day/month order
    [InlineData("July 1 2026")]
    [InlineData("2026")]
    [InlineData("2026-08-01 00:00:00")]    // space instead of 'T'
    [InlineData("2026-13-01T00:00:00Z")]   // month 13
    [InlineData("2026-08-01T25:00:00Z")]   // hour 25
    [InlineData("")]
    public void NonIsoDate_IsTreatedAsMissing(string text)
    {
        var request = _parser.Parse($$"""{"supplierId":"a","checkIn":"{{text}}"}""").Request!;

        Assert.Null(request.CheckIn);
    }

    [Theory]
    [InlineData("2026-08-01", "2026-08-01T00:00:00.0000000Z")]
    [InlineData("2026-08-01T10:15Z", "2026-08-01T10:15:00.0000000Z")]
    [InlineData("2026-08-01T10:15:30Z", "2026-08-01T10:15:30.0000000Z")]
    [InlineData("2026-08-01T10:15:30.5Z", "2026-08-01T10:15:30.5000000Z")]
    [InlineData("2026-08-01T10:15:30.1234567Z", "2026-08-01T10:15:30.1234567Z")]
    [InlineData("2026-08-01T12:15:30+02:00", "2026-08-01T10:15:30.0000000Z")]
    [InlineData("2026-08-01T10:15:30", "2026-08-01T10:15:30.0000000Z")]
    public void IsoDate_IsParsedAsUtcInstant(string text, string expectedUtc)
    {
        var request = _parser.Parse($$"""{"supplierId":"a","checkIn":"{{text}}"}""").Request!;

        Assert.NotNull(request.CheckIn);
        Assert.Equal(expectedUtc, StoredFormat.Timestamp(request.CheckIn!.Value));
    }

    [Fact]
    public void SupplierIdWithSpaces_IsNotTrimmed()
    {
        var parsed = _parser.Parse("""{"supplierId":" acme "}""");

        Assert.Equal(" acme ", parsed.SupplierId);
    }

    [Fact]
    public void MissingFields_AreNull()
    {
        var request = _parser.Parse("""{"supplierId":"acme"}""").Request!;

        Assert.Null(request.ReservationId);
        Assert.Null(request.RoomId);
        Assert.Null(request.CheckIn);
        Assert.Null(request.CheckOut);
        Assert.Null(request.Price);
        Assert.Null(request.UpdatedAtUtc);
    }
}
