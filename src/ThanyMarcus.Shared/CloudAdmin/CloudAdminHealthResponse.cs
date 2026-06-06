using System.Text.Json.Serialization;

namespace ThanyMarcus.Shared.CloudAdmin;

public sealed record CloudAdminHealthResponse(
    [property: JsonPropertyName("cert_ready")] bool CertReady,
    [property: JsonPropertyName("cloud_id")] Guid CloudId,
    [property: JsonPropertyName("registration_status")] string RegistrationStatus,
    [property: JsonPropertyName("api_version")] string ApiVersion,
    [property: JsonPropertyName("current_version")] string CurrentVersion)
{
    public const string CurrentApiVersion = "0.1.0";

    public const string RegistrationPending    = "pending";
    public const string RegistrationRegistered = "registered";
    public const string RegistrationFailed     = "failed";
}
