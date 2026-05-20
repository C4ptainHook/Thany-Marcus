using System.Text.Json;

namespace ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;

public interface ITerraformRunner
{
    Task<TerraformResult> InitAsync(string workdir, IReadOnlyDictionary<string, string> backendConfig, CancellationToken ct);
    Task<TerraformResult> SelectOrCreateWorkspaceAsync(string workdir, string workspaceName, CancellationToken ct);
    Task<TerraformResult> SelectWorkspaceAsync(string workdir, string workspaceName, CancellationToken ct);
    Task<TerraformResult> PlanAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct);
    Task<TerraformResult> ApplyAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct);
    Task<TerraformResult> DestroyAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct);
    Task<JsonDocument> OutputJsonAsync(string workdir, CancellationToken ct);
    Task ForceUnlockAsync(string workdir, string lockId, CancellationToken ct);
    Task<TerraformResult> DeleteWorkspaceAsync(string workdir, string workspaceName, CancellationToken ct);
    Task<bool> HasResourcesAsync(string workdir, CancellationToken ct);
}
