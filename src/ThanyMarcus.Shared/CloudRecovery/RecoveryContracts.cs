using System.Text.Json.Serialization;

namespace ThanyMarcus.Shared.CloudRecovery;

public sealed record ProvisionRecoveryCodeResponse(
    [property: JsonPropertyName("recovery_code")] string RecoveryCode);

public sealed record RedeemRecoveryRequest(
    [property: JsonPropertyName("recovery_code")] string RecoveryCode);

public sealed record RedeemRecoveryResponse(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("recovery_code")] string RecoveryCode);
