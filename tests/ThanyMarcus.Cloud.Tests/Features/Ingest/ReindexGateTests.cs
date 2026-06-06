using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Pgvector;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Ingest;

[Collection(PostgresCollection.Name)]
public sealed class ReindexGateTests(PostgresFixture postgres) : IAsyncLifetime
{
    private CloudApiFactory factory = null!;

    public ValueTask InitializeAsync()
    {
        factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await factory.DisposeAsync();

    [Fact]
    public async Task Reports_reindexing_while_a_ready_note_has_null_embedding()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await SetFlagAsync(true, ct);
        await SeedReadyNoteAsync(embedded: false, ct);

        await using var scope = factory.Services.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<ReindexGate>();
        (await gate.IsReindexingAsync(ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Clears_flag_when_sweep_has_drained()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await SetFlagAsync(true, ct);
        await SeedReadyNoteAsync(embedded: true, ct);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var gate = scope.ServiceProvider.GetRequiredService<ReindexGate>();
            (await gate.IsReindexingAsync(ct)).ShouldBeFalse();
        }

        (await ReadFlagAsync(ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Single_note_re_embed_does_not_trip_the_gate_when_flag_unset()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await SetFlagAsync(false, ct);
        await SeedReadyNoteAsync(embedded: false, ct);

        await using var scope = factory.Services.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<ReindexGate>();
        (await gate.IsReindexingAsync(ct)).ShouldBeFalse();
    }

    private async Task SetFlagAsync(bool value, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        await db.CloudSettings
            .Where(s => s.Id == CloudSettings.SingletonId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReindexInProgress, value), ct);
    }

    private async Task<bool> ReadFlagAsync(CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        return await db.CloudSettings
            .Where(s => s.Id == CloudSettings.SingletonId)
            .Select(s => s.ReindexInProgress)
            .SingleAsync(ct);
    }

    private async Task SeedReadyNoteAsync(bool embedded, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        var now = SystemClock.Instance.GetCurrentInstant();
        db.Notes.Add(new Note
        {
            Status     = NoteStatus.Ready,
            BodyInput  = "body",
            CapturedAt = now,
            Embedding  = embedded ? new Vector(new float[256]) : null,
            CreatedAt  = now,
            UpdatedAt  = now,
        });
        await db.SaveChangesAsync(ct);
    }
}
