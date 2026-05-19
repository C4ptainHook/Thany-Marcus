using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Pgvector;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Features.Processing.Phases;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Cloud.Tests.Infrastructure.Embedding;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Phases;

[Collection(PostgresCollection.Name)]
public sealed class EmbeddingHandlerTests(PostgresFixture postgres)
{
    private const string Template = "compose-v1";

    [Fact]
    public async Task First_time_capture_embeds_and_writes_body_hash()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var fake = new FakeEmbeddingClient(FakeEmbeddingClient.DeterministicUnitVector);
        var (noteId, jobId) = await SeedJobAsync(IngestJobKind.Capture,
            bodyOutput: "first body", initialHash: null, initialEmbedding: null);

        await using var sp = BuildHost(fake);
        var job = await LoadJobAsync(jobId);
        job.LastComposeTemplate = Template;
        await DispatchAsync(sp, job, ct);

        fake.CallCount.ShouldBe(1);
        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var note = await probe.Notes.SingleAsync(n => n.Id == noteId, ct);
        note.BodyHash.ShouldNotBeNullOrEmpty();
        note.Embedding.ShouldNotBeNull();
        note.Embedding!.ToArray().Sum(Math.Abs).ShouldBeGreaterThan(0);
        note.Status.ShouldBe(NoteStatus.Ready);

        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.Succeeded);
        var stages = StagesOf(after);
        stages.ShouldContain("embedding_emit");
        stages.ShouldNotContain("embedding_skip");
    }

    [Fact]
    public async Task Reprocess_skips_embed_when_body_hash_unchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var body = "stable body content";
        var existingHash = EmbeddingHandler.ComputeBodyHash(Template,body);
        var existingVec = FakeEmbeddingClient.DeterministicUnitVector("previous");

        var fake = new FakeEmbeddingClient(FakeEmbeddingClient.DeterministicUnitVector);
        var (noteId, jobId) = await SeedJobAsync(IngestJobKind.Reprocess,
            bodyOutput: body, initialHash: existingHash, initialEmbedding: existingVec);

        await using var sp = BuildHost(fake);
        var job = await LoadJobAsync(jobId);
        job.LastComposeTemplate = Template;
        await DispatchAsync(sp, job, ct);

        fake.CallCount.ShouldBe(0);
        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var note = await probe.Notes.SingleAsync(n => n.Id == noteId, ct);
        note.BodyHash.ShouldBe(existingHash);
        note.Embedding!.ToArray().ShouldBe(existingVec);

        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.Succeeded);
        var stages = StagesOf(after);
        stages.ShouldContain("embedding_skip");
        stages.ShouldNotContain("embedding_emit");
    }

    [Fact]
    public async Task Reprocess_reembeds_when_body_changed()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var oldBody = "old body";
        var newBody = "new body";
        var existingHash = EmbeddingHandler.ComputeBodyHash(Template,oldBody);
        var existingVec = FakeEmbeddingClient.DeterministicUnitVector("previous");

        var fake = new FakeEmbeddingClient(FakeEmbeddingClient.DeterministicUnitVector);
        var (noteId, jobId) = await SeedJobAsync(IngestJobKind.Reprocess,
            bodyOutput: newBody, initialHash: existingHash, initialEmbedding: existingVec);

        await using var sp = BuildHost(fake);
        var job = await LoadJobAsync(jobId);
        job.LastComposeTemplate = Template;
        await DispatchAsync(sp, job, ct);

        fake.CallCount.ShouldBe(1);
        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var note = await probe.Notes.SingleAsync(n => n.Id == noteId, ct);
        note.BodyHash.ShouldNotBe(existingHash);
        note.Embedding!.ToArray().ShouldNotBe(existingVec);

        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        StagesOf(after).ShouldContain("embedding_emit");
    }

    [Fact]
    public async Task Hub_regen_always_embeds_even_when_hash_matches()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var body = "hub body";
        var existingHash = EmbeddingHandler.ComputeBodyHash(Template,body);
        var existingVec = FakeEmbeddingClient.DeterministicUnitVector("prev-hub");

        var fake = new FakeEmbeddingClient(FakeEmbeddingClient.DeterministicUnitVector);
        var (noteId, jobId) = await SeedJobAsync(IngestJobKind.HubRegen,
            bodyOutput: body, initialHash: existingHash, initialEmbedding: existingVec);

        await using var sp = BuildHost(fake);
        var job = await LoadJobAsync(jobId);
        job.LastComposeTemplate = Template;
        await DispatchAsync(sp, job, ct);

        fake.CallCount.ShouldBe(1);
        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        StagesOf(after).ShouldContain("embedding_emit");
    }

    private ServiceProvider BuildHost(IEmbeddingClient client) =>
        ProcessingTestHost.Build(postgres.ConnectionString, customize: services =>
        {
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(IEmbeddingClient))
                    services.RemoveAt(i);
            }
            services.AddSingleton<IEmbeddingClient>(client);
        });

    private async Task<(Guid noteId, Guid jobId)> SeedJobAsync(
        string kind, string bodyOutput, string? initialHash, float[]? initialEmbedding)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var noteId = Guid.CreateVersion7();
        var note = new Note
        {
            Id           = noteId,
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt   = now,
            Status       = NoteStatus.Processing,
            BodyInput    = "input",
            BodyOutput   = bodyOutput,
            BodyHash     = initialHash,
            Embedding    = initialEmbedding is null ? null : new Vector(initialEmbedding),
            RelativePath = $"Inbox/{noteId}.md",
            CreatedAt    = now,
            UpdatedAt    = now,
        };
        var job = new IngestJob
        {
            Id             = Guid.CreateVersion7(),
            NoteId         = note.Id,
            Kind           = kind,
            Status         = IngestJobStatus.Embedding,
            Attempts       = 1,
            LeaseOwner     = "test-worker/abc12345",
            LeaseExpiresAt = now.Plus(Duration.FromSeconds(60)),
            ScheduledAt    = now,
            StartedAt      = now,
            CreatedAt      = now,
            UpdatedAt      = now,
        };
        db.Notes.Add(note);
        db.IngestJobs.Add(job);
        await db.SaveChangesAsync();
        return (note.Id, job.Id);
    }

    private async Task<IngestJob> LoadJobAsync(Guid jobId)
    {
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        return await db.IngestJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    private static async Task DispatchAsync(ServiceProvider sp, IngestJob job, CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IngestPhaseDispatcher>();
        await dispatcher.DispatchAsync(job, ct);
    }

    private static List<string> StagesOf(IngestJob job)
    {
        var list = new List<string>();
        foreach (var el in job.EventsLog.RootElement.EnumerateArray())
        {
            if (el.TryGetProperty("stage", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String)
                list.Add(s.GetString() ?? "");
        }
        return list;
    }
}

