using System.Text.Json;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

public sealed class FakeTerraformRunner : ITerraformRunner
{
    private readonly Queue<TerraformResult> initResults = new();
    private readonly Queue<TerraformResult> planResults = new();
    private readonly Queue<TerraformResult> applyResults = new();
    private readonly Queue<TerraformResult> destroyResults = new();
    private readonly Queue<JsonDocument> outputs = new();

    public List<(string Command, string Workdir)> Calls { get; } = [];

    public void QueueInit(TerraformResult result) => initResults.Enqueue(result);
    public void QueueInitOk() => initResults.Enqueue(new TerraformResult(0, "", ""));
    public void QueuePlanOk() => planResults.Enqueue(new TerraformResult(0, "", ""));
    public void QueuePlanFailure() => planResults.Enqueue(new TerraformResult(1, "", "plan boom"));
    public void QueueApplyOk() => applyResults.Enqueue(new TerraformResult(0, "", ""));
    public void QueueApplyFailure() => applyResults.Enqueue(new TerraformResult(1, "", "apply boom"));
    public void QueueDestroyOk() => destroyResults.Enqueue(new TerraformResult(0, "", ""));
    public void QueueDestroyFailure() => destroyResults.Enqueue(new TerraformResult(1, "", "destroy boom"));
    public void QueueOutputJson(string json) => outputs.Enqueue(JsonDocument.Parse(json));

    public Task<TerraformResult> InitAsync(string workdir, IReadOnlyDictionary<string, string> backendConfig, CancellationToken ct)
    {
        Calls.Add(("init", workdir));
        return Task.FromResult(initResults.Count > 0 ? initResults.Dequeue() : new TerraformResult(0, "", ""));
    }

    public List<string> WorkspaceSelectCalls { get; } = [];
    public bool WorkspaceSelectShouldFail { get; set; }

    public Task<TerraformResult> SelectOrCreateWorkspaceAsync(string workdir, string workspaceName, CancellationToken ct)
    {
        Calls.Add(("workspace-select", workdir));
        WorkspaceSelectCalls.Add(workspaceName);
        return Task.FromResult(WorkspaceSelectShouldFail
            ? new TerraformResult(1, "", "workspace select/new boom (fake)")
            : new TerraformResult(0, "", ""));
    }

    public List<string> WorkspaceSelectOnlyCalls { get; } = [];
    public bool WorkspaceSelectOnlyShouldFail { get; set; }
    public string WorkspaceSelectOnlyFailStderr { get; set; } = "workspace select boom (fake)";

    public Task<TerraformResult> SelectWorkspaceAsync(string workdir, string workspaceName, CancellationToken ct)
    {
        Calls.Add(("workspace-select-only", workdir));
        WorkspaceSelectOnlyCalls.Add(workspaceName);
        return Task.FromResult(WorkspaceSelectOnlyShouldFail
            ? new TerraformResult(1, "", WorkspaceSelectOnlyFailStderr)
            : new TerraformResult(0, "", ""));
    }

    public Task<TerraformResult> PlanAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct)
    {
        Calls.Add(("plan", workdir));
        return Task.FromResult(planResults.Count > 0 ? planResults.Dequeue() : new TerraformResult(0, "", ""));
    }

    public Task<TerraformResult> ApplyAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct)
    {
        Calls.Add(("apply", workdir));
        return Task.FromResult(applyResults.Count > 0 ? applyResults.Dequeue() : new TerraformResult(0, "", ""));
    }

    public Task<TerraformResult> DestroyAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct)
    {
        Calls.Add(("destroy", workdir));
        return Task.FromResult(destroyResults.Count > 0 ? destroyResults.Dequeue() : new TerraformResult(0, "", ""));
    }

    public Task<JsonDocument> OutputJsonAsync(string workdir, CancellationToken ct)
    {
        Calls.Add(("output", workdir));
        return Task.FromResult(outputs.Count > 0
            ? outputs.Dequeue()
            : JsonDocument.Parse("{\"ip\":{\"value\":\"203.0.113.1\",\"type\":\"string\"}}"));
    }

    public Task ForceUnlockAsync(string workdir, string lockId, CancellationToken ct)
    {
        Calls.Add(("force-unlock", workdir));
        return Task.CompletedTask;
    }

    public List<string> DeleteWorkspaceCalls { get; } = [];
    public bool DeleteWorkspaceShouldFail { get; set; }

    public Task<TerraformResult> DeleteWorkspaceAsync(string workdir, string workspaceName, CancellationToken ct)
    {
        Calls.Add(("workspace-delete", workdir));
        DeleteWorkspaceCalls.Add(workspaceName);
        return Task.FromResult(DeleteWorkspaceShouldFail
            ? new TerraformResult(1, "", "workspace delete boom (fake)")
            : new TerraformResult(0, "", ""));
    }

    private readonly Queue<bool> hasResourcesResults = new();
    public void QueueHasResources(bool value) => hasResourcesResults.Enqueue(value);

    public Task<bool> HasResourcesAsync(string workdir, CancellationToken ct)
    {
        Calls.Add(("state-list", workdir));
        return Task.FromResult(hasResourcesResults.Count > 0 ? hasResourcesResults.Dequeue() : false);
    }
}
