using Microsoft.AspNetCore.Mvc;
using SupplierFeed.Api.Ingest;
using SupplierFeed.Api.Models;

namespace SupplierFeed.Api.Controllers;

[ApiController]
[Route("api/reservations")]
public sealed class ReservationsController : ControllerBase
{
    private readonly IngestRequestParser _parser;
    private readonly IngestRequestValidator _validator;
    private readonly IIngestService _ingest;

    public ReservationsController(
        IngestRequestParser parser, IngestRequestValidator validator, IIngestService ingest)
    {
        _parser = parser;
        _validator = validator;
        _ingest = ingest;
    }

    [HttpPost("ingest")]
    public async Task<IActionResult> Ingest()
    {
        // Raw body, not model binding: the throttle must run before type validation.
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync();

        var parsed = _parser.Parse(body);
        if (parsed.SupplierId is null || parsed.Request is null)
        {
            // No usable supplierId: nothing to throttle on, so it is not throttled or counted.
            return BadRequest(new
            {
                error = "Request body must be a JSON object with a non-blank supplierId."
            });
        }

        var errors = _validator.Validate(parsed.Request);
        var result = await _ingest.IngestAsync(parsed.SupplierId, parsed.Request, errors);

        return result.Status switch
        {
            IngestStatus.Created => StatusCode(StatusCodes.Status201Created, new { status = "created" }),
            IngestStatus.Updated => Ok(new { status = "updated" }),
            IngestStatus.Unchanged => Ok(new { status = "unchanged" }),
            IngestStatus.Stale => Ok(new { status = "stale" }),
            IngestStatus.Throttled => StatusCode(StatusCodes.Status429TooManyRequests,
                new { error = "Rate limit exceeded." }),
            _ => BadRequest(new { error = "Validation failed.", errors = result.Errors })
        };
    }
}
