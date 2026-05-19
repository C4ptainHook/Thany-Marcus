using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Features.Sync;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Tests.Features.Processing;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Sync;

[Collection(PostgresCollection.Name)]
public sealed class PostgresIngestEventBusTests(PostgresFixture postgres)
{
    [Fact]
    public async Task PublishNotePhaseChanged_emits_pg_notify_to_ingest_events()
    {
        await postgres.ResetAsync();
        var ct = TestContext.Current.CancellationToken;
        var noteId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var listenConn = new NpgsqlConnection(postgres.ConnectionString);
        await listenConn.OpenAsync(ct);
        listenConn.Notification += (_, args) =>
        {
            if (args.Channel == IngestEventKinds.Channel)
            {
                received.TrySetResult(args.Payload);
            }
        };
        await using (var cmd = new NpgsqlCommand($"LISTEN {IngestEventKinds.Channel};", listenConn))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var bus = new PostgresIngestEventBus(probe);
        await bus.PublishNotePhaseChangedAsync(noteId, jobId, "queued", "extracting_attachments", ct);

        var poll = Task.Run(async () =>
        {
            while (!received.Task.IsCompleted)
            {
                await listenConn.WaitAsync(ct);
            }
        }, ct);

        var winner = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));
        winner.ShouldBe(received.Task);

        var payload = await received.Task;
        using var doc = JsonDocument.Parse(payload);
        doc.RootElement.GetProperty("kind").GetString().ShouldBe(IngestEventKinds.NotePhaseChanged);
        doc.RootElement.GetProperty("noteId").GetGuid().ShouldBe(noteId);
        doc.RootElement.GetProperty("jobId").GetGuid().ShouldBe(jobId);
        doc.RootElement.GetProperty("from").GetString().ShouldBe("queued");
        doc.RootElement.GetProperty("to").GetString().ShouldBe("extracting_attachments");
        _ = poll;
    }

    [Fact]
    public async Task PublishNoteFailed_carries_error_message()
    {
        await postgres.ResetAsync();
        var ct = TestContext.Current.CancellationToken;
        var noteId = Guid.NewGuid();

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listenConn = new NpgsqlConnection(postgres.ConnectionString);
        await listenConn.OpenAsync(ct);
        listenConn.Notification += (_, args) =>
        {
            if (args.Channel == IngestEventKinds.Channel) received.TrySetResult(args.Payload);
        };
        await using (var cmd = new NpgsqlCommand($"LISTEN {IngestEventKinds.Channel};", listenConn))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var bus = new PostgresIngestEventBus(probe);
        await bus.PublishNoteFailedAsync(noteId, "user_cancelled", ct);

        var poll = Task.Run(async () =>
        {
            while (!received.Task.IsCompleted)
            {
                await listenConn.WaitAsync(ct);
            }
        }, ct);
        var winner = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));
        winner.ShouldBe(received.Task);
        using var doc = JsonDocument.Parse(await received.Task);
        doc.RootElement.GetProperty("kind").GetString().ShouldBe(IngestEventKinds.NoteFailed);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("user_cancelled");
        _ = poll;
    }
}
