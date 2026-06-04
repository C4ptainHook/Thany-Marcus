using ThanyMarcus.Portal.Api.Features.CloudManagement;

namespace ThanyMarcus.Portal.Api.Features.Provisioning.Providers;

public interface IProvisioningProvider
{
    string Key { get; }

    bool UserCreatable { get; }

    bool RequiresCredentials { get; }

    bool MintsObjectStorageCredentials { get; }

    bool SupportsPricing { get; }

    string InitialStatusAfterCreate { get; }

    Task AddProvisioningEnvAsync(
        IDictionary<string, string> env, Cloud cloud, ReadOnlyMemory<byte> dek, CancellationToken ct);

    Task RevokeCredentialsAsync(Cloud cloud, ReadOnlyMemory<byte> dek, CancellationToken ct);
}
