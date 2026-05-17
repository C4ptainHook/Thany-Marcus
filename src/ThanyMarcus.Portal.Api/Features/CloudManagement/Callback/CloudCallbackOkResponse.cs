namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Callback;

public sealed record CloudCallbackOkResponse(bool? Ok = null, bool? Idempotent = null);
