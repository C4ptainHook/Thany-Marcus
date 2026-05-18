namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Events;

public interface IProvisioningEventBus
{
    Task PublishPluginTokenIssuedAsync(Guid cloudId, string rawToken, string deepLink, CancellationToken ct);
}
