using System.Text;

namespace SupplierFeed.Api.Infrastructure;

public static class BoundedBodyReader
{
    /// <summary>Largest request body accepted by the ingest endpoint (64 KB).</summary>
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>
    /// Reads the stream as UTF-8 text, stopping as soon as it passes <paramref name="maxBytes"/>
    /// instead of buffering an oversized body. Returns null if the body is too large.
    /// </summary>
    public static async Task<string?> ReadAsync(Stream body, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[maxBytes + 1];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await body.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                return Encoding.UTF8.GetString(buffer, 0, total);
            }

            total += read;
        }

        return null; // more than maxBytes were available
    }
}
