namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Migrate;

public sealed record GreenDroplet(string DropletId, string Ipv4);

public interface IBlueGreenOperations
{
    string Provider { get; }

    Task<bool> HasCapacityAsync(string accessToken, CancellationToken ct);

    Task<string> FindActiveDropletIdAsync(string accessToken, Cloud cloud, CancellationToken ct);

    Task<string> SnapshotDataVolumeAsync(string accessToken, Cloud cloud, CancellationToken ct);

    Task<GreenDroplet> ProvisionGreenAsync(
        string accessToken, Cloud cloud, string snapshotId, string targetVersion, CancellationToken ct);

    Task<bool> HealthyAsync(string ipv4, CancellationToken ct);

    Task ReassignReservedIpAsync(string accessToken, Cloud cloud, string dropletId, CancellationToken ct);

    Task DestroyDropletAsync(string accessToken, string dropletId, CancellationToken ct);
}
