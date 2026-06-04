using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;

namespace ThanyMarcus.Portal.Api.Features.Provisioning.Providers;

public sealed class StubProvisioningProvider : IProvisioningProvider
{
    public string Key => KnownProviders.Stub;
    public bool UserCreatable => false;
    public bool RequiresCredentials => false;
    public bool MintsObjectStorageCredentials => false;
    public bool SupportsPricing => false;
    public string InitialStatusAfterCreate => SagaStatus.TfPlanning;

    public Task AddProvisioningEnvAsync(
        IDictionary<string, string> env, Cloud cloud, ReadOnlyMemory<byte> dek, CancellationToken ct) =>
        Task.CompletedTask;

    public Task RevokeCredentialsAsync(Cloud cloud, ReadOnlyMemory<byte> dek, CancellationToken ct) =>
        Task.CompletedTask;
}
