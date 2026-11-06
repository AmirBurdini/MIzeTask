namespace SupplierFeed.Api.Models;

public enum IngestStatus
{
    Created,
    Updated,
    Unchanged,
    Stale,
    Throttled,
    Invalid
}

public sealed record IngestResult(IngestStatus Status, IReadOnlyList<string> Errors);
