namespace ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;

public interface ICloudAdminTokenAccessor
{
    Task<string?> GetPlaintextAsync(Guid cloudId, CancellationToken ct);
}
