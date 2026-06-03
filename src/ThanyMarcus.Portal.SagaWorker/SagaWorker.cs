using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning;
using ThanyMarcus.Shared.Saga;

namespace ThanyMarcus.Portal.SagaWorker;

public sealed partial class SagaWorker : BackgroundService
{
    private readonly IServiceProvider services;
    private readonly ILogger<SagaWorker> log;
    private readonly SemaphoreSlim concurrency;
    private readonly string workerId;
    private readonly Duration leaseDuration;
    private readonly Duration heartbeatInterval;
    private readonly TimeSpan safetyNetPoll;
    private readonly string connectionString;
    private readonly Channel<bool> wakeup;

    public SagaWorker(
        IServiceProvider services,
        IConfiguration config,
        IHostEnvironment env,
        ILogger<SagaWorker> log)
    {
        this.services = services;
        this.log = log;
        var max = config.GetValue("Provisioning:MaxConcurrentJobs", 3);
        concurrency = new SemaphoreSlim(max, max);
        leaseDuration = Duration.FromSeconds(config.GetValue("Provisioning:LeaseSeconds", 60));
        heartbeatInterval = Duration.FromSeconds(config.GetValue("Provisioning:HeartbeatSeconds", 30));
        safetyNetPoll = TimeSpan.FromMilliseconds(config.GetValue("Provisioning:IdlePollMs", 60_000));
        connectionString = config.GetConnectionString("Portal")
            ?? throw new InvalidOperationException("ConnectionStrings:Portal not configured");
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
        LogStarted(log, workerId);

        var listenTask = ListenLoopAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await concurrency.WaitAsync(stoppingToken);

                ProvisioningJob? job;
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
                    var heartbeatTask = HeartbeatLoopAsync(job.Id, workerId, heartbeatCts.Token);
                    try
                    {
                        await using var scope = services.CreateAsyncScope();
                        var dispatcher = scope.ServiceProvider.GetRequiredService<SagaPhaseDispatcher>();
                        await dispatcher.HandleAsync(job, stoppingToken);
                    }
                    catch (SagaOwnershipLostException ex)
                    {
                        LogOwnershipLost(log, ex.JobId, ex.AttemptedWorkerId);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                    }
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

    private async Task<ProvisioningJob?> ClaimNextAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();

        var sql = $$"""
            UPDATE provisioning_jobs
            SET claimed_by         = {0},
                lease_expires_at   = now() + (interval '1 second' * {1}),
                next_visible_at    = now() + (interval '1 second' * {1}),
                attempt_count      = attempt_count + 1,
                transition_version = transition_version + 1,
                updated_at         = now()
            WHERE id = (
              SELECT pj.id FROM provisioning_jobs pj
              WHERE pj.status <> ALL({2})
                AND pj.next_visible_at <= now()
                AND (pj.claimed_by IS NULL OR pj.lease_expires_at <= now())
                AND NOT EXISTS (
                  SELECT 1 FROM provisioning_jobs sib
                  WHERE sib.cloud_id = pj.cloud_id
                    AND sib.id <> pj.id
                    AND sib.claimed_by IS NOT NULL
                    AND sib.lease_expires_at > now()
                )
                AND pg_try_advisory_xact_lock(hashtext(pj.cloud_id::text)::bigint)
              ORDER BY pj.next_visible_at
              LIMIT 1
              FOR UPDATE SKIP LOCKED
            )
            RETURNING *
            """;

        var rows = await db.ProvisioningJobs
            .FromSqlRaw(sql, workerId, (int)leaseDuration.TotalSeconds, SagaStatus.Terminal.ToArray())
            .AsNoTracking()
            .ToListAsync(ct);
        return rows.FirstOrDefault();
    }

    private async Task HeartbeatLoopAsync(Guid jobId, string ownerWorkerId, CancellationToken ct)
    {
        var interval = heartbeatInterval.ToTimeSpan();
        var leaseSeconds = (int)leaseDuration.TotalSeconds;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                await using var scope = services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
                await db.Database.ExecuteSqlInterpolatedAsync($@"
                    UPDATE provisioning_jobs
                       SET lease_expires_at = now() + (interval '1 second' * {leaseSeconds}),
                           updated_at       = now()
                     WHERE id = {jobId} AND claimed_by = {ownerWorkerId}", ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                LogHeartbeatFailure(log, ex, jobId);
            }
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
                "LISTEN provisioning_new; LISTEN provisioning_job_changed;", conn))
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
        using var timeoutCts = new CancellationTokenSource(safetyNetPoll);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await wakeup.Reader.ReadAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // safety-net poll interval elapsed
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "SagaWorker started; worker_id={WorkerId}")]
    private static partial void LogStarted(ILogger logger, string workerId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "SagaWorker stopping")]
    private static partial void LogStopping(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "SagaWorker dispatch failed for job {JobId}")]
    private static partial void LogDispatchFailure(ILogger logger, Exception ex, Guid jobId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "SagaWorker ownership lost for job {JobId} (worker={WorkerId})")]
    private static partial void LogOwnershipLost(ILogger logger, Guid jobId, string? workerId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "SagaWorker heartbeat failed for job {JobId}")]
    private static partial void LogHeartbeatFailure(ILogger logger, Exception ex, Guid jobId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning,
        Message = "SagaWorker heartbeat teardown error for job {JobId}")]
    private static partial void LogHeartbeatTeardown(ILogger logger, Exception ex, Guid jobId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
        Message = "SagaWorker LISTEN loop terminated unexpectedly")]
    private static partial void LogListenLoopFailure(ILogger logger, Exception ex);
}
