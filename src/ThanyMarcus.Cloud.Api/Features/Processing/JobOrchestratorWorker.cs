using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Shared.Saga;

namespace ThanyMarcus.Cloud.Api.Features.Processing;

public sealed partial class JobOrchestratorWorker : BackgroundService
{
    public const string NewChannel = "ingest_jobs_new";
    public const string ChangedChannel = "ingest_jobs_changed";

    private readonly IServiceProvider services;
    private readonly IConfiguration config;
    private readonly IClock clock;
    private readonly ILogger<JobOrchestratorWorker> log;
    private readonly SemaphoreSlim concurrency;
    private readonly Duration leaseDuration;
    private readonly Duration heartbeatInterval;
    private readonly TimeSpan idlePoll;
    private readonly string connectionString;
    private readonly string workerId;
    private readonly Channel<bool> wakeup;

    public JobOrchestratorWorker(
        IServiceProvider services,
        IConfiguration config,
        IHostEnvironment env,
        IClock clock,
        ILogger<JobOrchestratorWorker> log)
    {
        this.services = services;
        this.config = config;
        this.clock = clock;
        this.log = log;
        var max = config.GetValue("IngestSaga:Orchestrator:MaxConcurrentJobs", 3);
        concurrency = new SemaphoreSlim(max, max);
        leaseDuration = Duration.FromSeconds(config.GetValue("IngestSaga:Orchestrator:LeaseSeconds", 60));
        heartbeatInterval = Duration.FromSeconds(config.GetValue("IngestSaga:Orchestrator:HeartbeatSeconds", 20));
        idlePoll = TimeSpan.FromMilliseconds(config.GetValue("IngestSaga:Orchestrator:IdlePollMs", 60_000));
        connectionString = config.GetConnectionString("Cloud")
            ?? throw new InvalidOperationException("ConnectionStrings:Cloud not configured");
        workerId = $"{env.ApplicationName}@{Environment.MachineName}/{Guid.NewGuid().ToString("N")[..8]}";
        wakeup = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    internal string WorkerId => workerId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(log, workerId);
        var listenTask = ListenLoopAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await concurrency.WaitAsync(stoppingToken);

                IngestJob? job;
                try
                {
                    job = await ClaimNextAsync(stoppingToken);
                }
                catch
                {
                    concurrency.Release();
                    throw;
                }

                if (job is null)
                {
                    concurrency.Release();
                    try { await WaitForWakeupAsync(stoppingToken); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var heartbeatTask = HeartbeatLoopAsync(job.Id, heartbeatCts.Token);
                    try
                    {
                        await using var scope = services.CreateAsyncScope();
                        var dispatcher = scope.ServiceProvider.GetRequiredService<IngestPhaseDispatcher>();
                        await dispatcher.DispatchAsync(job, stoppingToken);
                    }
                    catch (SagaOwnershipLostException ex)
                    {
                        LogOwnershipLost(log, ex.JobId, ex.AttemptedWorkerId);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                    catch (Exception ex)
                    {
                        LogDispatchFailure(log, ex, job.Id);
                    }
                    finally
                    {
                        await heartbeatCts.CancelAsync();
                        try { await heartbeatTask; }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { LogHeartbeatTeardown(log, ex, job.Id); }
                        heartbeatCts.Dispose();
                        concurrency.Release();
                        _ = wakeup.Writer.TryWrite(true);
                    }
                }, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { }

        try { await listenTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogListenLoopFailure(log, ex); }

        LogStopping(log);
    }

    internal async Task<IngestJob?> ClaimNextAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        var now = clock.GetCurrentInstant();
        var lease = (int)leaseDuration.TotalSeconds;

        var rows = await db.IngestJobs.FromSqlInterpolated($"""
            UPDATE ingest_jobs SET
                status             = CASE
                                       WHEN status = 'queued' AND kind <> 'hub_regen'
                                            THEN 'extracting_attachments'
                                       ELSE status
                                     END,
                lease_owner        = {workerId},
                lease_expires_at   = now() + (interval '1 second' * {lease}),
                attempts           = attempts + 1,
                consecutive_crashes = CASE
                                        WHEN lease_owner IS NOT NULL
                                          THEN consecutive_crashes + 1
                                        ELSE consecutive_crashes
                                      END,
                transition_version = transition_version + 1,
                started_at         = COALESCE(started_at, {now}),
                updated_at         = {now}
              WHERE id = (
                SELECT id FROM ingest_jobs
                 WHERE status NOT IN ('succeeded','failed_extraction','failed_composition',
                                      'failed_route','failed_entities','failed_synthesis','failed_embedding','dead_lettered','cancelled')
                   AND scheduled_at <= {now}
                   AND ((status = 'queued')
                        OR (status IN ('extracting_attachments','composing','routing','extracting_entities','synthesizing','embedding')
                            AND (lease_expires_at IS NULL OR lease_expires_at <= {now})))
                 ORDER BY scheduled_at
                 LIMIT 1
                 FOR UPDATE SKIP LOCKED
              )
            RETURNING *;
            """).AsNoTracking().ToListAsync(ct);

        return rows.FirstOrDefault();
    }

    private async Task HeartbeatLoopAsync(Guid jobId, CancellationToken ct)
    {
        var interval = heartbeatInterval.ToTimeSpan();
        var lease = (int)leaseDuration.TotalSeconds;
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
                    UPDATE ingest_jobs SET
                        lease_expires_at = now() + (interval '1 second' * {lease}),
                        updated_at       = {now}
                      WHERE id = {jobId}
                        AND lease_owner = {workerId}
                        AND status NOT IN ('succeeded','failed_extraction','failed_composition',
                                           'failed_route','failed_entities','failed_synthesis','failed_embedding','dead_lettered','cancelled')
                    """, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { LogHeartbeatFailure(log, ex, jobId); }
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            conn.Notification += (_, _) => _ = wakeup.Writer.TryWrite(true);
            await using (var cmd = new NpgsqlCommand(
                $"LISTEN {NewChannel}; LISTEN {ChangedChannel};", conn))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            while (!ct.IsCancellationRequested)
            {
                await conn.WaitAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogListenLoopFailure(log, ex); }
    }

    private async Task WaitForWakeupAsync(CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(idlePoll);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try { await wakeup.Reader.ReadAsync(linked.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "JobOrchestratorWorker started; worker_id={WorkerId}")]
    private static partial void LogStarted(ILogger logger, string workerId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "JobOrchestratorWorker stopping")]
    private static partial void LogStopping(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "JobOrchestratorWorker dispatch failed for job {JobId}")]
    private static partial void LogDispatchFailure(ILogger logger, Exception ex, Guid jobId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "JobOrchestratorWorker ownership lost for job {JobId} (worker={WorkerId})")]
    private static partial void LogOwnershipLost(ILogger logger, Guid jobId, string? workerId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "JobOrchestratorWorker heartbeat failed for job {JobId}")]
    private static partial void LogHeartbeatFailure(ILogger logger, Exception ex, Guid jobId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message = "JobOrchestratorWorker heartbeat teardown error for job {JobId}")]
    private static partial void LogHeartbeatTeardown(ILogger logger, Exception ex, Guid jobId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
        Message = "JobOrchestratorWorker LISTEN loop terminated unexpectedly")]
    private static partial void LogListenLoopFailure(ILogger logger, Exception ex);
}
