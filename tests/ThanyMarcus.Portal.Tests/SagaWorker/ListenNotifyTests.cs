using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

namespace ThanyMarcus.Portal.Tests.SagaWorker;

public sealed class ListenNotifyTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Worker_picks_up_job_via_notify_before_safety_net_poll_fires()
    {
        var ct = TestContext.Current.CancellationToken;
        var dpKeysDir = MakeTempDir("notify-keys");
        var workspaceBase = MakeTempDir("notify-ws");
        try
        {
            var dp = new EphemeralDataProtectionProvider();
            Directory.CreateDirectory(Path.Combine(workspaceBase, "stub"));

            var blocker = new BlockingPlanRunner();
            var cf = new FakeCloudflareDnsClient();

            using var host = SagaHostBuilder.Build(
                Postgres.ConnectionString, dpKeysDir, workspaceBase, blocker, cf,
                maxConcurrent: 1, dataProtectionProvider: dp, clock: Clock,
                extraConfig: cfg =>
                {
                    // Safety net is high; only LISTEN/NOTIFY can deliver pickup quickly.
                    cfg["Provisioning:IdlePollMs"] = "30000";
                    cfg["Provisioning:HeartbeatSeconds"] = "60";
                    cfg["Provisioning:LeaseSeconds"] = "120";
                });
            await host.StartAsync(ct);
            try
            {
                // Let worker reach the idle wait before we insert.
                await Task.Delay(500, ct);

                var (_, _, job) = await SagaTestSeed.SeedAsync(
                    Db, Clock, dp,
                    status: SagaStatus.TfPlanning,
                    ct: ct);
                Db.ChangeTracker.Clear();

                // CreateCloudEndpoints fires this notification after the producer transaction commits.
                await using (var fireConn = new Npgsql.NpgsqlConnection(Postgres.ConnectionString))
                {
                    await fireConn.OpenAsync(ct);
                    await using var fireCmd = new Npgsql.NpgsqlCommand(
                        $"SELECT pg_notify('provisioning_new', '{job.Id}')", fireConn);
                    await fireCmd.ExecuteNonQueryAsync(ct);
                }

                // Expect plan called well within the 30s safety-net window. If LISTEN is wired,
                // this fires within ~1 second; if it falls back to safety-net, this test times out.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await WaitForAsync(
                    async () => await blocker.PlanCallCountAsync(ct) >= 1,
                    TimeSpan.FromSeconds(8), ct);
                sw.Stop();

                sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));

                blocker.ReleaseAll();
            }
            finally
            {
                blocker.ReleaseAll();
                await host.StopAsync(ct);
            }
        }
        finally
        {
            try { Directory.Delete(dpKeysDir, recursive: true); } catch { }
            try { Directory.Delete(workspaceBase, recursive: true); } catch { }
        }
    }

    private static string MakeTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100, ct);
        }
        throw new TimeoutException("condition not met within " + timeout);
    }

    private sealed class BlockingPlanRunner : ITerraformRunner
    {
        private readonly TaskCompletionSource<TerraformResult> release = new();
        private int planCalls;

        public Task<int> PlanCallCountAsync(CancellationToken ct) => Task.FromResult(Volatile.Read(ref planCalls));

        public void ReleaseAll() => release.TrySetResult(new TerraformResult(0, "", ""));

        public Task<TerraformResult> InitAsync(string workdir, IReadOnlyDictionary<string, string> backendConfig, CancellationToken ct) =>
            Task.FromResult(new TerraformResult(0, "", ""));

        public Task<TerraformResult> SelectOrCreateWorkspaceAsync(string workdir, string workspaceName, CancellationToken ct) =>
            Task.FromResult(new TerraformResult(0, "", ""));

        public Task<TerraformResult> SelectWorkspaceAsync(string workdir, string workspaceName, CancellationToken ct) =>
            Task.FromResult(new TerraformResult(0, "", ""));

        public Task<TerraformResult> PlanAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct)
        {
            Interlocked.Increment(ref planCalls);
            return release.Task.WaitAsync(ct);
        }

        public Task<TerraformResult> ApplyAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct) =>
            Task.FromResult(new TerraformResult(0, "", ""));

        public Task<TerraformResult> DestroyAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct) =>
            Task.FromResult(new TerraformResult(0, "", ""));

        public Task<JsonDocument> OutputJsonAsync(string workdir, CancellationToken ct) =>
            Task.FromResult(JsonDocument.Parse("{\"ip\":{\"value\":\"203.0.113.1\",\"type\":\"string\"}}"));

        public Task ForceUnlockAsync(string workdir, string lockId, CancellationToken ct) => Task.CompletedTask;

        public Task<TerraformResult> DeleteWorkspaceAsync(string workdir, string workspaceName, CancellationToken ct) =>
            Task.FromResult(new TerraformResult(0, "", ""));

        public Task<bool> HasResourcesAsync(string workdir, CancellationToken ct) =>
            Task.FromResult(false);
    }
}
