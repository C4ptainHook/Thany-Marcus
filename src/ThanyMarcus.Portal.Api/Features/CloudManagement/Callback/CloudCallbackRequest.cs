using System.Text.Json.Serialization;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Callback;

public sealed record CloudCallbackRequest(
    [property: JsonPropertyName("cloud_id")] Guid CloudId,
    [property: JsonPropertyName("enrollment_token")] string EnrollmentToken,
    [property: JsonPropertyName("cloud_admin_token")] string CloudAdminToken);
