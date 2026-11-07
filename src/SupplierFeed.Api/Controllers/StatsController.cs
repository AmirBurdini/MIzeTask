using Microsoft.AspNetCore.Mvc;
using SupplierFeed.Api.Stats;

namespace SupplierFeed.Api.Controllers;

[ApiController]
[Route("api/reservations/stats")]
public sealed class StatsController : ControllerBase
{
    private readonly IStatsQuery _stats;

    public StatsController(IStatsQuery stats)
    {
        _stats = stats;
    }

    [HttpGet("{supplierId}")]
    public async Task<IActionResult> Get(string supplierId)
    {
        if (!_stats.DatabaseExists())
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Database not initialised." });
        }

        var stats = await _stats.GetAsync(supplierId);
        return Ok(new { supplierId = stats.SupplierId, ingested = stats.Ingested, throttled = stats.Throttled });
    }
}
