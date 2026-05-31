using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Extraction;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Tests.Features.Processing;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ThanyMarcus.Cloud.Tests.Infrastructure.Sidecars;

[Collection(PostgresCollection.Name)]
public sealed class RealUrlFetcherClientTests(PostgresFixture postgres) : IAsyncLifetime
{
    private WireMockServer wm = null!;

    public ValueTask InitializeAsync()
    {
        wm = WireMockServer.Start();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        wm.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Html_happy_path_returns_metadata()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (noteId, _, attId) = await SeedUrlAttachmentAsync($"{wm.Url}/page.html");

        wm.Given(Request.Create().WithPath("/page.html").UsingHead())
          .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/html"));
        wm.Given(Request.Create().WithPath("/page.html").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(200)
              .WithHeader("Content-Type", "text/html; charset=utf-8")
              .WithBody(OgHtml("Article Headline", "Article description text", "Test Site")));

        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var att = await db.Attachments.SingleAsync(a => a.Id == attId, ct);
        var client = BuildClient(db);

        var outcome = await client.FetchAsync(noteId, att, ct);

        outcome.RedirectedToAttachmentId.ShouldBeNull();
        outcome.IsMinimal.ShouldBeFalse();
        outcome.ExtractedText.ShouldNotBeNullOrEmpty();
        outcome.ExtractedText!.ShouldContain("Article Headline");
        outcome.Extra.RootElement.GetProperty("http_status").GetInt32().ShouldBe(200);
        outcome.Extra.RootElement.GetProperty("title").GetString().ShouldBe("Article Headline");
        outcome.Extra.RootElement.GetProperty("description").GetString().ShouldBe("Article description text");
        outcome.Extra.RootElement.GetProperty("provider_name").GetString().ShouldBe("Test Site");
    }

    [Fact]
    public async Task Http_404_returns_minimal()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (noteId, _, attId) = await SeedUrlAttachmentAsync($"{wm.Url}/missing.html");

        wm.Given(Request.Create().WithPath("/missing.html").UsingHead())
          .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/html"));
        wm.Given(Request.Create().WithPath("/missing.html").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(404).WithHeader("Content-Type", "text/html"));

        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var att = await db.Attachments.SingleAsync(a => a.Id == attId, ct);
        var client = BuildClient(db);

        var outcome = await client.FetchAsync(noteId, att, ct);

        outcome.RedirectedToAttachmentId.ShouldBeNull();
        outcome.IsMinimal.ShouldBeTrue();
        outcome.ExtractedText.ShouldBe("(URL captured, no preview available)");
        outcome.Extra.RootElement.GetProperty("minimal_reason").GetString().ShouldBe("http: 404");
    }

    [Fact]
    public async Task Image_url_inserts_reroute_child()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var imgUrl = $"{wm.Url}/photo.jpg";
        var (noteId, jobId, attId) = await SeedUrlAttachmentAsync(imgUrl);

        wm.Given(Request.Create().WithPath("/photo.jpg").UsingHead())
          .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "image/jpeg"));

        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var att = await db.Attachments.SingleAsync(a => a.Id == attId, ct);
        var client = BuildClient(db);

        var outcome = await client.FetchAsync(noteId, att, ct);

        outcome.RedirectedToAttachmentId.ShouldNotBeNull();
        outcome.ExtractedText.ShouldBeNull();

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var child = await probe.Attachments.SingleAsync(a => a.Id == outcome.RedirectedToAttachmentId, ct);
        child.Kind.ShouldBe(AttachmentKind.Image);
        child.ParentAttachmentId.ShouldBe(attId);
        child.StorageProvider.ShouldBe("external");
        child.Url.ShouldBe(imgUrl);
        child.MimeType.ShouldBe("image/jpeg");

        var task = await probe.ExtractionTasks.SingleAsync(t => t.AttachmentId == child.Id, ct);
        task.TargetSidecar.ShouldBe(ExtractionTaskSidecar.Ollama);
        task.Status.ShouldBe(ExtractionTaskStatus.Queued);
        task.IngestJobId.ShouldBe(jobId);
    }

    [Fact]
    public async Task Pdf_url_routes_to_docling()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var pdfUrl = $"{wm.Url}/doc.pdf";
        var (noteId, _, attId) = await SeedUrlAttachmentAsync(pdfUrl);

        wm.Given(Request.Create().WithPath("/doc.pdf").UsingHead())
          .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/pdf"));

        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var att = await db.Attachments.SingleAsync(a => a.Id == attId, ct);
        var client = BuildClient(db);

        var outcome = await client.FetchAsync(noteId, att, ct);

        outcome.RedirectedToAttachmentId.ShouldNotBeNull();
        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var task = await probe.ExtractionTasks.SingleAsync(t => t.AttachmentId == outcome.RedirectedToAttachmentId, ct);
        task.TargetSidecar.ShouldBe(ExtractionTaskSidecar.Docling);
    }

    [Fact]
    public async Task Head_405_falls_back_to_range_get()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var imgUrl = $"{wm.Url}/picky.png";
        var (noteId, _, attId) = await SeedUrlAttachmentAsync(imgUrl);

        wm.Given(Request.Create().WithPath("/picky.png").UsingHead())
          .RespondWith(Response.Create().WithStatusCode((int)HttpStatusCode.MethodNotAllowed));
        wm.Given(Request.Create().WithPath("/picky.png").UsingGet())
          .RespondWith(Response.Create().WithStatusCode(206)
              .WithHeader("Content-Type", "image/png")
              .WithBody(new byte[] { 0 }));

        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var att = await db.Attachments.SingleAsync(a => a.Id == attId, ct);
        var client = BuildClient(db);

        var outcome = await client.FetchAsync(noteId, att, ct);

        outcome.RedirectedToAttachmentId.ShouldNotBeNull();
        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var child = await probe.Attachments.SingleAsync(a => a.Id == outcome.RedirectedToAttachmentId, ct);
        child.Kind.ShouldBe(AttachmentKind.Image);
        child.MimeType.ShouldBe("image/png");
    }

    [Fact]
    public async Task Unsupported_mime_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var url = $"{wm.Url}/blob.bin";
        var (noteId, _, attId) = await SeedUrlAttachmentAsync(url);

        wm.Given(Request.Create().WithPath("/blob.bin").UsingHead())
          .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/octet-stream"));

        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var att = await db.Attachments.SingleAsync(a => a.Id == attId, ct);
        var client = BuildClient(db);

        await Should.ThrowAsync<UnsupportedRerouteException>(
            async () => await client.FetchAsync(noteId, att, ct));
    }

    private static RealUrlFetcherClient BuildClient(CloudDbContext db)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(RealUrlFetcherClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient(UrlExtractor.HttpClientName);
        services.AddLogging();
        var sp = services.BuildServiceProvider();
        var clientFactory = sp.GetRequiredService<IHttpClientFactory>();
        var extractor = new UrlExtractor(clientFactory, NullLogger<UrlExtractor>.Instance);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IngestSaga:Sidecars:Url:HeadTimeoutSeconds"] = "5",
            ["IngestSaga:Sidecars:Url:GetTimeoutSeconds"] = "10",
            ["IngestSaga:Specialists:Video:Enabled"] = "true",
        }).Build();
        return new RealUrlFetcherClient(
            clientFactory, extractor, db, SystemClock.Instance, config,
            NullLogger<RealUrlFetcherClient>.Instance);
    }

    private async Task<(Guid noteId, Guid jobId, Guid attId)> SeedUrlAttachmentAsync(string url)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var note = new Note
        {
            Id = Guid.CreateVersion7(),
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt = now,
            Status = NoteStatus.Processing,
            BodyInput = "",
            CreatedAt = now, UpdatedAt = now,
        };
        var job = new IngestJob
        {
            Id = Guid.CreateVersion7(),
            NoteId = note.Id,
            Status = IngestJobStatus.ExtractingAttachments,
            ScheduledAt = now,
            CreatedAt = now, UpdatedAt = now,
        };
        var parentAttId = Guid.CreateVersion7();
        var att = new Attachment
        {
            Id = parentAttId,
            NoteId = note.Id,
            ClientAttachmentId = "u1",
            Kind = AttachmentKind.Url,
            StorageProvider = "external",
            StorageBucket = "",
            StorageKey = $"external://{parentAttId}",
            Url = url,
            Status = AttachmentStatus.Uploaded,
            ExtractionStatus = AttachmentExtractionStatus.Pending,
            Extra = JsonDocument.Parse("{}"),
            CreatedAt = now, UpdatedAt = now,
        };
        var task = new ExtractionTask
        {
            Id = Guid.CreateVersion7(),
            IngestJobId = job.Id,
            AttachmentId = att.Id,
            TargetSidecar = ExtractionTaskSidecar.Url,
            Status = ExtractionTaskStatus.Processing,
            ScheduledAt = now,
            EventsLog = JsonDocument.Parse("[]"),
            CreatedAt = now, UpdatedAt = now,
        };
        db.Notes.Add(note);
        db.IngestJobs.Add(job);
        db.Attachments.Add(att);
        db.ExtractionTasks.Add(task);
        await db.SaveChangesAsync();
        return (note.Id, job.Id, att.Id);
    }

    private static string LongHtml(string title, string body) =>
        $"<!doctype html><html><head><title>{title}</title></head><body><article><h1>{title}</h1><p>{body}</p></article></body></html>";

    private static string OgHtml(string title, string description, string siteName) =>
        $"<!doctype html><html><head>" +
        $"<title>{title}</title>" +
        $"<meta property=\"og:title\" content=\"{title}\" />" +
        $"<meta property=\"og:description\" content=\"{description}\" />" +
        $"<meta property=\"og:site_name\" content=\"{siteName}\" />" +
        $"</head><body><p>body</p></body></html>";
}
