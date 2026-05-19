using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Specialists;

public sealed class AttachmentExtractionCache(CloudDbContext db)
{
    public async Task<CachedExtraction?> LookupAsync(
        string? sha256, string cacheKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sha256)) return null;
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);

        var row = await db.Attachments
            .AsNoTracking()
            .Where(a => a.Sha256 == sha256
                     && a.ExtractionCacheKey == cacheKey
                     && a.ExtractedText != null
                     && a.ExtractionStatus == AttachmentExtractionStatus.Extracted)
            .OrderByDescending(a => a.UpdatedAt)
            .Select(a => new { a.ExtractedText, ExtraJson = a.Extra })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        JsonDocument extra;
        try
        {
            extra = JsonDocument.Parse(row.ExtraJson.RootElement.GetRawText());
        }
        catch
        {
            extra = JsonDocument.Parse("{}");
        }
        return new CachedExtraction(row.ExtractedText!, extra);
    }
}

public sealed record CachedExtraction(string ExtractedText, JsonDocument Extra);
