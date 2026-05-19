using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Features.Processing.Phases;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Phases;

[Collection(PostgresCollection.Name)]
public sealed class CancelHandlerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Cancel_transitions_job_to_dead_lettered_and_note_to_failed()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);

        var now = SystemClock.Instance.GetCurrentInstant();
        using (var seed = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString))
        {
            var note = new Note
            {
                Id = Guid.CreateVersion7(),
                ClientNoteId = Guid.NewGuid().ToString(),
                CapturedAt = now,
                Status = NoteStatus.Processing,
                BodyInput = "body",
                DeletedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var job = new IngestJob
            {
                Id = Guid.CreateVersion7(),
                NoteId = note.Id,
                Status = IngestJobStatus.Composing,
                Attempts = 1,
                LeaseOwner = "test-worker/abc12345",
                LeaseExpiresAt = now.Plus(Duration.FromSeconds(60)),
                ScheduledAt = now,
                StartedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            seed.Notes.Add(note);
            seed.IngestJobs.Add(job);
            await seed.SaveChangesAsync(ct);

            using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
            var loaded = await probe.IngestJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, ct);

            await using var scope = sp.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<CancelHandler>();
            await handler.HandleAsync(loaded, ct);
        }

        using var verify = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await verify.IngestJobs.SingleAsync(ct);
        after.Status.ShouldBe(IngestJobStatus.DeadLettered);
        after.LastError.ShouldBe("user_cancelled");

        var noteAfter = await verify.Notes.SingleAsync(ct);
        noteAfter.Status.ShouldBe(NoteStatus.Failed);
    }
}
