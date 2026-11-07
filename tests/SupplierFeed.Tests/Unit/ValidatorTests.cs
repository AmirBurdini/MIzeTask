using SupplierFeed.Api.Ingest;
using SupplierFeed.Api.Models;

namespace SupplierFeed.Tests.Unit;

public class ValidatorTests
{
    private readonly IngestRequestValidator _validator = new();

    private static IngestRequest ValidRequest() => new()
    {
        ReservationId = "r1",
        RoomId = "101",
        CheckIn = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
        CheckOut = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero),
        Price = 450,
        UpdatedAtUtc = new DateTimeOffset(2026, 7, 1, 10, 15, 0, TimeSpan.Zero)
    };

    [Fact]
    public void ValidRequest_HasNoErrors()
    {
        Assert.Empty(_validator.Validate(ValidRequest()));
    }

    [Fact]
    public void CheckInEqualToCheckOut_IsRejected()
    {
        var request = ValidRequest();
        request.CheckOut = request.CheckIn;

        var errors = _validator.Validate(request);

        Assert.Single(errors);
        Assert.Contains("checkIn", errors[0]);
    }

    [Fact]
    public void CheckInAfterCheckOut_IsRejected()
    {
        var request = ValidRequest();
        request.CheckIn = request.CheckOut!.Value.AddDays(1);

        var errors = _validator.Validate(request);

        Assert.Single(errors);
        Assert.Contains("earlier than checkOut", errors[0]);
    }

    [Fact]
    public void NegativePrice_IsRejected()
    {
        var request = ValidRequest();
        request.Price = -0.01;

        var errors = _validator.Validate(request);

        Assert.Single(errors);
        Assert.Contains("price", errors[0]);
    }

    [Fact]
    public void ZeroPrice_IsAccepted()
    {
        var request = ValidRequest();
        request.Price = 0;

        Assert.Empty(_validator.Validate(request));
    }

    [Theory]
    [InlineData("reservationId", null)]
    [InlineData("reservationId", "")]
    [InlineData("roomId", null)]
    [InlineData("roomId", "   ")]
    public void BlankRequiredString_IsRejectedWithFieldName(string field, string? value)
    {
        var request = ValidRequest();
        switch (field)
        {
            case "reservationId": request.ReservationId = value; break;
            case "roomId": request.RoomId = value; break;
        }

        var errors = _validator.Validate(request);

        var error = Assert.Single(errors);
        Assert.Contains(field, error);
    }

    [Fact]
    public void MissingDatesOrPrice_ReportOneErrorEach()
    {
        var request = ValidRequest();
        request.CheckIn = null;
        request.CheckOut = null;
        request.UpdatedAtUtc = null;
        request.Price = null;

        var errors = _validator.Validate(request);

        Assert.Equal(4, errors.Count);
        foreach (var field in new[] { "checkIn", "checkOut", "updatedAtUtc", "price" })
        {
            Assert.Single(errors, e => e.StartsWith(field));
        }
    }

    [Fact]
    public void MultipleProblems_AreAllReported()
    {
        var request = ValidRequest();
        request.CheckIn = request.CheckOut!.Value.AddDays(1);
        request.Price = -5;
        request.RoomId = " ";

        var errors = _validator.Validate(request);

        Assert.Equal(3, errors.Count);
    }
}
