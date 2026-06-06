namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Migrate;

public sealed class StubBlueGreenOperations : IBlueGreenOperations
{
    public string Provider => "stub";

    public Task<bool> HasCapacityAsync(string accessToken, CancellationToken ct) => Task.FromResult(true);

    public Task<string> FindActiveDropletIdAsync(string accessToken, Cloud cloud, CancellationToken ct) =>
        Task.FromResult($"old-{cloud.Id:N}");

    public Task<string> SnapshotDataVolumeAsync(string accessToken, Cloud cloud, CancellationToken ct) =>
        Task.FromResult($"snap-{cloud.Id:N}");

    public Task<GreenDroplet> ProvisionGreenAsync(
        string accessToken, Cloud cloud, string snapshotId, string targetVersion, CancellationToken ct) =>
        Task.FromResult(new GreenDroplet($"green-{cloud.Id:N}", "203.0.113.2"));

    public Task<bool> HealthyAsync(string ipv4, CancellationToken ct) => Task.FromResult(true);

    public Task ReassignReservedIpAsync(string accessToken, Cloud cloud, string dropletId, CancellationToken ct) =>
        Task.CompletedTask;

    public Task DestroyDropletAsync(string accessToken, string dropletId, CancellationToken ct) =>
        Task.CompletedTask;
}
