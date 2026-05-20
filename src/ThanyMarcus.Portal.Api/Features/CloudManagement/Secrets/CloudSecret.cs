using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;

public sealed class CloudSecret : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid CloudId { get; init; }
    public string Kind { get; init; } = null!;

    public byte[] Ciphertext { get; set; } = null!;
    public byte[] Nonce { get; set; } = null!;
    public byte[] Tag { get; set; } = null!;

    public Instant? ExpiresAt { get; set; }

    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}

public static class CloudSecretKind
{
    public const string DoSpacesAccessId = "do_spaces_access_id";
    public const string DoSpacesSecret   = "do_spaces_secret";
}
