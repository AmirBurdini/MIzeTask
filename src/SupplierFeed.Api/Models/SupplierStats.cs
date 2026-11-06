namespace SupplierFeed.Api.Models;

public sealed record SupplierStats(string SupplierId, long Ingested, long Throttled);
