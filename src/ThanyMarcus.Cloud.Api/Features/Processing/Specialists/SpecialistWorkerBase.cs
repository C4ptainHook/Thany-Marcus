using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Shared.Saga;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Specialists;

public abstract partial class SpecialistWorkerBase<TClient> : BackgroundService
    where TClient : notnull
{
    protected abstract string TargetSidecar { get; }
    protected abstract Task<SpecialistExtractionOutcome> ExtractAsync(
        IServiceProvider scopeServices,
        TClient client,
        ExtractionTask task,
        Attachment att,
        CancellationToken ct);

    private readonly IServiceProvider services;
    private readonly IConfiguration config;
    private readonly IClock clock;
    private readonly ILogger log;
    private readonly Duration leaseDuration;
    private readonly int maxAttempts;
    private readonly int backoffBaseSeconds;
    private readonly TimeSpan idlePoll;
    private readonly string connectionString;
    private readonly string workerId;
    private readonly Channel<bool> wakeup;

    protected SpecialistWorkerBase(
        IServiceProvider services,
        IConfiguration config,
        IHostEnvironment env,
        IClock clock,
        ILogger log)
    {
        this.services = services;
        this.config = config;
        this.clock = clock;
        this.log = log;
        leaseDuration = Duration.FromSeconds(
            config.GetValue("IngestSaga:ExtractionTasks:LeaseSeconds", 60));
        maxAttempts = config.GetValue("IngestSaga:ExtractionTasks:MaxAttempts", 3);
        backoffBaseSeconds = config.GetValue("IngestSaga:ExtractionTasks:BackoffSecondsBase", 2);
        idlePoll = TimeSpan.FromMilliseconds(
            config.GetValue("IngestSaga:ExtractionTasks:IdlePollMs", 60_000));
        connectionString = config.GetConnectionString("Cloud")
            ?? throw new InvalidOperationException("ConnectionStrings:Cloud not configured");
        workerId = $"{env.ApplicationName}.{ResolveSidecarStatic()}@{Environment.MachineName}/{Guid.NewGuid().ToString("N")[..8]}";
        wakeup = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    internal string WorkerId => workerId;

    private string ResolveSidecarStatic() => TargetSidecar;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(log, workerId, TargetSidecar);
        var listenTask = ListenLoopAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ExtractionTask? task;
                try
                {
                    task = await ClaimNextAsync(stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    LogClaimFailure(log, ex, TargetSidecar);
                    task = null;
                }

                if (task is null)
                {
                    try { await WaitForWakeupAsync(stoppingToken); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                try
                {
                    await ProcessAsync(task, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    LogProcessFailure(log, ex, task.Id, TargetSidecar);
                }
            }
        }
        catch (OperationCanceledException) { }

        try { await listenTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogListenLoopFailure(log, ex, TargetSidecar); }

        LogStopping(log, TargetSidecar);
    }

    internal async Task<ExtractionTask?> ClaimNextAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        var now = clock.GetCurrentInstant();
        var lease = (int)leaseDuration.TotalSeconds;
        var sidecar = TargetSidecar;

        var rows = await db.ExtractionTasks.FromSqlInterpolated($"""
            UPDATE extraction_tasks SET
                status             = 'processing',
                lease_owner        = {workerId},
                lease_expires_at   = now() + (interval '1 second' * {lease}),
                attempts           = attempts + 1,
                transition_version = transition_version + 1,
                started_at         = COALESCE(started_at, {now}),
                updated_at         = {now}
              WHERE id = (
                SELECT id FROM extraction_tasks
                 WHERE target_sidecar = {sidecar}
                   AND ((status = 'queued' AND scheduled_at <= {now})
                        OR (status = 'processing' AND (lease_expires_at IS NULL OR lease_expires_at <= {now})))
                 ORDER BY scheduled_at
                 LIMIT 1
                 FOR UPDATE SKIP LOCKED
              )
            RETURNING *;
            """).AsNoTracking().ToListAsync(ct);

        return rows.FirstOrDefault();
    }

    internal Task ProcessOnceAsync(ExtractionTask task, CancellationToken ct) => ProcessAsync(task, ct);

    private async Task ProcessAsync(ExtractionTask task, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        var client = scope.ServiceProvider.GetRequiredService<TClient>();
        var bus = scope.ServiceProvider.GetRequiredService<IIngestEventBus>();

        var attachment = await db.Attachments.SingleOrDefaultAsync(a => a.Id == task.AttachmentId, ct);
        if (attachment is null)
        {
            await MarkTerminalAsync(db, task, ExtractionTaskStatus.Failed, "attachment_missing", ct);
            await NotifyChangedAsync(db, task.IngestJobId, ct);
            return;
        }

        try
        {
            var outcome = await ExtractAsync(scope.ServiceProvider, client, task, attachment, ct);
            await MarkSucceededAsync(db, task, attachment, outcome, ct);
            await NotifyChangedAsync(db, task.IngestJobId, ct);
            await bus.PublishAttachmentStatusChangedAsync(
                attachment.NoteId, attachment.Id,
                AttachmentExtractionStatus.Pending,
                outcome.AttachmentStatus,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await OnFailureAsync(db, task, attachment, ex, ct);
            await NotifyChangedAsync(db, task.IngestJobId, ct);
            if (task.Attempts >= maxAttempts)
            {
                await bus.PublishAttachmentStatusChangedAsync(
                    attachment.NoteId, attachment.Id,
                    AttachmentExtractionStatus.Pending,
                    AttachmentExtractionStatus.Failed,
                    ct);
            }
        }
    }

    private async Task MarkSucceededAsync(
        CloudDbContext db, ExtractionTask task, Attachment att, SpecialistExtractionOutcome outcome, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var expectedVersion = task.TransitionVersion;
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE extraction_tasks SET
                status             = 'succeeded',
                lease_owner        = NULL,
                lease_expires_at   = NULL,
                finished_at        = {now},
                transition_version = transition_version + 1,
                updated_at         = {now}
              WHERE id = {task.Id}
                AND lease_owner = {workerId}
                AND transition_version = {expectedVersion}
            """, ct);
        if (rows == 0)
        {
            throw new SagaOwnershipLostException(task.Id, workerId);
        }
        var extraJson = outcome.Extra is null ? null : outcome.Extra.RootElement.GetRawText();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE attachments SET
                extracted_text        = {outcome.ExtractedText},
                extraction_status     = {outcome.AttachmentStatus},
                extraction_error      = {outcome.ExtractionError},
                extraction_cache_key  = {outcome.ExtractionCacheKey},
                extra                 = COALESCE({extraJson}::jsonb, extra),
                updated_at            = {now}
              WHERE id = {att.Id}
            """, ct);
    }

    private async Task OnFailureAsync(
        CloudDbContext db, ExtractionTask task, Attachment att, Exception ex, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var attempts = task.Attempts;
        if (attempts >= maxAttempts)
        {
            await MarkTerminalAsync(db, task, ExtractionTaskStatus.Failed, ex.Message, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE attachments SET
                    extraction_status = 'failed',
                    extraction_error  = {ex.Message},
                    updated_at        = {now}
                  WHERE id = {att.Id}
                """, ct);
        }
        else
        {
            var backoff = (int)Math.Pow(2, attempts) * backoffBaseSeconds;
            var nextAt = now.Plus(Duration.FromSeconds(backoff));
            var expectedVersion = task.TransitionVersion;
            var error = ex.Message;
            var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE extraction_tasks SET
                    status             = 'queued',
                    last_error         = {error},
                    lease_owner        = NULL,
                    lease_expires_at   = NULL,
                    scheduled_at       = {nextAt},
                    transition_version = transition_version + 1,
                    updated_at         = {now}
                  WHERE id = {task.Id}
                    AND lease_owner = {workerId}
                    AND transition_version = {expectedVersion}
                """, ct);
            if (rows == 0)
            {
                throw new SagaOwnershipLostException(task.Id, workerId);
            }
        }
    }

    private async Task MarkTerminalAsync(
        CloudDbContext db, ExtractionTask task, string status, string error, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var expectedVersion = task.TransitionVersion;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE extraction_tasks SET
                status             = {status},
                last_error         = {error},
                lease_owner        = NULL,
                lease_expires_at   = NULL,
                finished_at        = {now},
                transition_version = transition_version + 1,
                updated_at         = {now}
              WHERE id = {task.Id}
                AND transition_version = {expectedVersion}
            """, ct);
    }

    private static async Task NotifyChangedAsync(CloudDbContext db, Guid jobId, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var owns = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
            owns = true;
        }
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_notify(@chan, @payload)", conn);
            cmd.Parameters.AddWithValue("chan", SpecialistChannels.TasksChanged);
            cmd.Parameters.AddWithValue("payload", jobId.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
            await using var cmd2 = new NpgsqlCommand("SELECT pg_notify(@chan, @payload)", conn);
            cmd2.Parameters.AddWithValue("chan", JobOrchestratorWorker.ChangedChannel);
            cmd2.Parameters.AddWithValue("payload", jobId.ToString());
            await cmd2.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (owns) await db.Database.CloseConnectionAsync();
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var channel = SpecialistChannels.NewTasksChannel(TargetSidecar);
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            conn.Notification += (_, _) => _ = wakeup.Writer.TryWrite(true);
            await using (var cmd = new NpgsqlCommand($"LISTEN {channel};", conn))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            while (!ct.IsCancellationRequested)
            {
                await conn.WaitAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogListenLoopFailure(log, ex, TargetSidecar); }
    }

    private async Task WaitForWakeupAsync(CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(idlePoll);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try { await wakeup.Reader.ReadAsync(linked.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Specialist worker started: worker_id={WorkerId} sidecar={Sidecar}")]
    private static partial void LogStarted(ILogger logger, string workerId, string sidecar);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Specialist worker stopping: sidecar={Sidecar}")]
    private static partial void LogStopping(ILogger logger, string sidecar);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Specialist worker claim failed: sidecar={Sidecar}")]
    private static partial void LogClaimFailure(ILogger logger, Exception ex, string sidecar);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Specialist worker process failed: task={TaskId} sidecar={Sidecar}")]
    private static partial void LogProcessFailure(ILogger logger, Exception ex, Guid taskId, string sidecar);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "Specialist worker LISTEN loop terminated: sidecar={Sidecar}")]
    private static partial void LogListenLoopFailure(ILogger logger, Exception ex, string sidecar);
}
