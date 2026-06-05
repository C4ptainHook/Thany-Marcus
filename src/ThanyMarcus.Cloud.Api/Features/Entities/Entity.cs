using NodaTime;
using Pgvector;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Cloud.Api.Features.Entities;

public sealed class Entity : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public string Kind { get; set; } = null!;
    public string CanonicalName { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string[] Aliases { get; set; } = [];
    public string? Description { get; set; }
    public Vector? Embedding { get; set; }
    public Guid? HubNoteId { get; set; }
    public bool HubSuppressed { get; set; }
    public Guid? StubNoteId { get; set; }
    public int MentionCount { get; set; }
    public string Source { get; set; } = null!;
    public string? VaultFolder { get; set; }
    public Instant? DeletedAt { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}

public static class EntityKind
{
    public const string Person = "person";
    public const string Organization = "organization";
    public const string Place = "place";
    public const string Concept = "concept";
    public const string Other = "other";
}

public static class EntitySource
{
    public const string User = "user";
    public const string Llm = "llm";
}
