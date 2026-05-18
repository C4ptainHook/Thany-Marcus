using NodaTime;

namespace ThanyMarcus.Cloud.Api.Features.Entities;

public sealed class Mention
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid EntityId { get; init; }
    public Guid NoteId { get; init; }
    public string AnchorText { get; init; } = null!;
    public int StartOffset { get; init; }
    public int EndOffset { get; init; }
    public float? Confidence { get; init; }
    public Instant CreatedAt { get; init; }
}
