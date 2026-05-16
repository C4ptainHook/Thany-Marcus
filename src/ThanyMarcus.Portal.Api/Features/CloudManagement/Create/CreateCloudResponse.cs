namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Create;

public sealed record CreateCloudResponse(Guid CloudId, Guid JobId, string Hostname);
