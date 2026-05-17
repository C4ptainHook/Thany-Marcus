using NodaTime;

namespace ThanyMarcus.Shared.Database;

public interface IHasUpdatedAt
{
    Instant UpdatedAt { get; set; }
}
