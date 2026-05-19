using System.Text.Json;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight;

public sealed class PdfPreflighter : IDocumentPreflighter
{
    private readonly IArtifactStore store;
    private readonly DocumentFilterOptions options;

    public PdfPreflighter(IArtifactStore store, DocumentFilterOptions options)
    {
        this.store = store;
        this.options = options;
    }

    public async Task<PreflightResult> CheckAsync(Attachment att, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(att);

        if (att.ByteSize is { } size && size > options.MaxSizeBytes)
        {
            return PreflightResult.Skip($"size_{size}_gt_{options.MaxSizeBytes}");
        }

        if (!string.Equals(att.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return PreflightResult.Pass;
        }

        await using var stream = await store.OpenReadAsync(att.StorageKey, ct);
        using var buffered = await CopyToSeekableAsync(stream, ct);

        try
        {
            using var pdf = PdfDocument.Open(buffered);
            var pageCount = pdf.NumberOfPages;
            if (pageCount > options.MaxPageCount)
            {
                return PreflightResult.Skip($"pages_{pageCount}_gt_{options.MaxPageCount}");
            }
            return PreflightResult.PassWith(BuildMeta(pageCount));
        }
        catch (PdfDocumentEncryptedException)
        {
            return PreflightResult.Skip("pdf_encrypted");
        }
        catch (Exception ex)
        {
            return PreflightResult.Skip($"pdf_open_failed_{ex.GetType().Name}");
        }
    }

    private static async Task<MemoryStream> CopyToSeekableAsync(Stream source, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await source.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    private static JsonDocument BuildMeta(int pageCount)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("page_count", pageCount);
            writer.WriteEndObject();
        }
        stream.Position = 0;
        return JsonDocument.Parse(stream);
    }
}
