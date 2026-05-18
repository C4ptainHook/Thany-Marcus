using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Processing;

public sealed partial class IngestSagaWorker : BackgroundService
{
    public const string NotifyChannel = "ingest_jobs_new";
    public static readonly Duration LeaseDuration = Duration.FromSeconds(60);
    public static readonly Duration HeartbeatInterval = Duration.FromSeconds(20);
    public static readonly TimeSpan IdlePoll = TimeSpan.FromMinutes(1);
    public const int MaxAttempts = 3;

    private readonly IServiceProvider services;
    private readonly IConfiguration config;
    private readonly IClock clock;
    private readonly ILogger<IngestSagaWorker> log;
    private readonly string workerId;
    private readonly Channel<bool> wakeup;

    public IngestSagaWorker(
        IServiceProvider services,
        IConfiguration config,
        IHostEnvironment env,
        IClock clock,
        ILogger<IngestSagaWorker> log)
    {
        this.services = services;
        this.config = config;
        this.clock = clock;
        this.log = log;
        workerId = $"{env.ApplicationName}@{Environment.MachineName}/{Guid.NewGuid().ToString("N")[..8]}";
        wakeup = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = config.GetConnectionString("Cloud")
            ?? throw new InvalidOperationException("ConnectionStrings:Cloud not configured");

        LogStarted(log, workerId);
        var listenTask = ListenLoopAsync(connectionString, stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                IngestJob? job;
                try { job = await ClaimNextAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { LogClaimFailure(log, ex); job = null; }

                if (job is null)
                {
                    try { await WaitForWakeupAsync(stoppingToken); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                await ProcessJobAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }

        try { await listenTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogListenLoopFailure(log, ex); }

        LogStopping(log);
    }

    private async Task ProcessJobAsync(IngestJob job, CancellationToken stoppingToken)
    {
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeatTask = HeartbeatLoopAsync(job.Id, heartbeatCts.Token);

        try
        {
            await using var scope = services.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<IIngestJobHandler>();
            await handler.HandleAsync(job, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LogHandlerFailure(log, ex, job.Id);
            await ReleaseOrDeadLetterAsync(job, ex, stoppingToken);
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try { await heartbeatTask; } catch (OperationCanceledException) { }
            catch (Exception ex) { LogHeartbeatTeardown(log, ex, job.Id); }
        }
    }

    private async Task<IngestJob?> ClaimNextAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        var now = clock.GetCurrentInstant();

        var rows = await db.IngestJobs.FromSqlInterpolated($"""
            UPDATE ingest_jobs
               SET status            = 'processing',
                   lease_owner       = {workerId},
                   lease_expires_at  = {now.Plus(LeaseDuration)},
                   started_at        = COALESCE(started_at, {now}),
                   attempts          = attempts + 1,
                   updated_at        = {now}
             WHERE id = (
               SELECT id FROM ingest_jobs
                WHERE (status = 'queued' AND scheduled_at <= {now})
                   OR (status = 'processing' AND lease_expires_at <= {now})
                ORDER BY scheduled_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
             )
            RETURNING *;
            """).AsNoTracking().ToListAsync(ct);

        return rows.FirstOrDefault();
    }

    private async Task ReleaseOrDeadLetterAsync(IngestJob job, Exception ex, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        var now = clock.GetCurrentInstant();

        if (job.Attempts >= MaxAttempts)
        {
            var backoffSeconds = (int)Math.Pow(2, job.Attempts);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ingest_jobs SET
                    status      = 'dead_lettered',
                    last_error  = {ex.Message},
                    finished_at = {now},
                    updated_at  = {now}
                  WHERE id = {job.Id}
                """, ct);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE notes SET
                    status     = 'failed',
                    provenance = jsonb_build_object('error', {ex.Message}::text),
                    updated_at = {now}
                  WHERE id = {job.NoteId}
                """, ct);
        }
        else
        {
            var backoffSeconds = (int)Math.Pow(2, job.Attempts);
            var nextRunAt = now.Plus(Duration.FromSeconds(backoffSeconds));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE ingest_jobs SET
                    status            = 'queued',
                    last_error        = {ex.Message},
                    lease_owner       = NULL,
                    lease_expires_at  = NULL,
                    scheduled_at      = {nextRunAt},
                    updated_at        = {now}
                  WHERE id = {job.Id}
                """, ct);
        }
    }

    private async Task HeartbeatLoopAsync(Guid jobId, CancellationToken ct)
    {
        var interval = HeartbeatInterval.ToTimeSpan();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
                var now = clock.GetCurrentInstant();
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE ingest_jobs
                       SET lease_expires_at = {now.Plus(LeaseDuration)},
                           updated_at       = {now}
                     WHERE id = {jobId}
                       AND lease_owner = {workerId}
                       AND status = 'processing'
                    """, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                LogHeartbeatFailure(log, ex, jobId);
            }
        }
    }

    private async Task ListenLoopAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            conn.Notification += (_, _) => _ = wakeup.Writer.TryWrite(true);
            await using (var cmd = new NpgsqlCommand($"LISTEN {NotifyChannel}", conn))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            while (!ct.IsCancellationRequested)
            {
                await conn.WaitAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogListenLoopFailure(log, ex);
        }
    }

    private async Task WaitForWakeupAsync(CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(IdlePoll);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try { await wakeup.Reader.ReadAsync(linked.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* idle poll */ }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "IngestSagaWorker started; worker_id={WorkerId}")]
    private static partial void LogStarted(ILogger logger, string workerId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "IngestSagaWorker stopping")]
    private static partial void LogStopping(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "IngestSagaWorker claim failed")]
    private static partial void LogClaimFailure(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "IngestSagaWorker handler failed for job {JobId}")]
    private static partial void LogHandlerFailure(ILogger logger, Exception ex, Guid jobId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "IngestSagaWorker heartbeat failed for job {JobId}")]
    private static partial void LogHeartbeatFailure(ILogger logger, Exception ex, Guid jobId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "IngestSagaWorker heartbeat teardown error for job {JobId}")]
    private static partial void LogHeartbeatTeardown(ILogger logger, Exception ex, Guid jobId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "IngestSagaWorker LISTEN loop terminated unexpectedly")]
    private static partial void LogListenLoopFailure(ILogger logger, Exception ex);
}
