using System.Text.Json;
using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Portal.Api.Features.Provisioning;

public sealed class ProvisioningJob : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid CloudId { get; init; }
    public Guid UserId { get; init; }
    public string Kind { get; init; } = null!;
    public JsonDocument Payload { get; init; } = null!;
    public string Status { get; set; } = null!;
    public string? EnrollmentToken { get; set; }
    public byte[]? AdminTokenCiphertext { get; set; }

    public Instant NextVisibleAt { get; set; }
    public Instant? LeaseExpiresAt { get; set; }
    public string? ClaimedBy { get; set; }
    public short AttemptCount { get; set; }
    public long TransitionVersion { get; set; }
    public Instant? PhaseStartedAt { get; set; }
    public JsonDocument EventsLog { get; set; } = null!;
    public JsonDocument? TfOutputs { get; set; }
    public string? LastError { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
