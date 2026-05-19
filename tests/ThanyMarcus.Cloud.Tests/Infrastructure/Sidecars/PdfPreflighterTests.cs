using System.Text.Json;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using UglyToad.PdfPig.Writer;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Sidecars;

public sealed class PdfPreflighterTests
{
    [Fact]
    public async Task Size_above_cap_skips_without_opening_pdf()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeArtifactStore();
        store.SeedBody("k/big.pdf", new byte[10], mimeType: "application/pdf");
        var att = NewAttachment("k/big.pdf", mime: "application/pdf", byteSize: 100_000_000);
        var preflighter = new PdfPreflighter(store, new DocumentFilterOptions { MaxSizeBytes = 1_000_000 });

        var result = await preflighter.CheckAsync(att, ct);

        result.ShouldExtract.ShouldBeFalse();
        result.SkipReason.ShouldStartWith("size_");
    }

    [Fact]
    public async Task Page_count_above_cap_skips()
    {
        var ct = TestContext.Current.CancellationToken;
        var pdf = MakePdf(pageCount: 5);
        var store = new FakeArtifactStore();
        store.SeedBody("k/small.pdf", pdf, mimeType: "application/pdf");
        var att = NewAttachment("k/small.pdf", mime: "application/pdf", byteSize: pdf.Length);
        var preflighter = new PdfPreflighter(store, new DocumentFilterOptions { MaxPageCount = 2 });

        var result = await preflighter.CheckAsync(att, ct);

        result.ShouldExtract.ShouldBeFalse();
        result.SkipReason.ShouldStartWith("pages_");
    }

    [Fact]
    public async Task Within_caps_passes_with_page_count_metadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var pdf = MakePdf(pageCount: 3);
        var store = new FakeArtifactStore();
        store.SeedBody("k/small.pdf", pdf, mimeType: "application/pdf");
        var att = NewAttachment("k/small.pdf", mime: "application/pdf", byteSize: pdf.Length);
        var preflighter = new PdfPreflighter(store, new DocumentFilterOptions());

        var result = await preflighter.CheckAsync(att, ct);

        result.ShouldExtract.ShouldBeTrue();
        result.Metadata.ShouldNotBeNull();
        result.Metadata!.RootElement.GetProperty("page_count").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Non_pdf_mime_passes_without_opening()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeArtifactStore();
        var att = NewAttachment("k/x.docx", mime: "application/vnd.openxmlformats", byteSize: 1024);
        var preflighter = new PdfPreflighter(store, new DocumentFilterOptions());

        var result = await preflighter.CheckAsync(att, ct);

        result.ShouldExtract.ShouldBeTrue();
        result.Metadata.ShouldBeNull();
    }

    [Fact]
    public async Task Corrupt_pdf_skips_with_open_failed_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FakeArtifactStore();
        store.SeedBody("k/x.pdf", System.Text.Encoding.UTF8.GetBytes("not a pdf"), mimeType: "application/pdf");
        var att = NewAttachment("k/x.pdf", mime: "application/pdf", byteSize: 9);
        var preflighter = new PdfPreflighter(store, new DocumentFilterOptions());

        var result = await preflighter.CheckAsync(att, ct);

        result.ShouldExtract.ShouldBeFalse();
        result.SkipReason.ShouldStartWith("pdf_open_failed");
    }

    private static byte[] MakePdf(int pageCount)
    {
        var builder = new PdfDocumentBuilder();
        for (var i = 0; i < pageCount; i++)
        {
            builder.AddPage(595, 842);
        }
        return builder.Build();
    }

    private static Attachment NewAttachment(string storageKey, string mime, long byteSize) => new()
    {
        Id = Guid.CreateVersion7(),
        NoteId = Guid.CreateVersion7(),
        ClientAttachmentId = "a",
        Kind = AttachmentKind.File,
        StorageProvider = "s3",
        StorageBucket = "test",
        StorageKey = storageKey,
        ByteSize = byteSize,
        MimeType = mime,
        Status = AttachmentStatus.Uploaded,
        ExtractionStatus = AttachmentExtractionStatus.Pending,
        Extra = JsonDocument.Parse("{}"),
        CreatedAt = SystemClock.Instance.GetCurrentInstant(),
        UpdatedAt = SystemClock.Instance.GetCurrentInstant(),
    };
}
