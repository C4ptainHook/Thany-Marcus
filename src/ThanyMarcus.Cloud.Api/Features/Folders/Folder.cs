using NodaTime;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Cloud.Api.Features.Folders;

public sealed class Folder : IHasUpdatedAt
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public string Path { get; set; } = null!;
    public Instant? DeletedAt { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}
