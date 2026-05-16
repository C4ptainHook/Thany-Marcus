namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Callback;

public sealed record CloudCallbackRequest(Guid CloudId, string EnrollmentToken, string CloudAdminToken);
