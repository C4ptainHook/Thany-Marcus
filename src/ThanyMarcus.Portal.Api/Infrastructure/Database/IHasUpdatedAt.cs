using NodaTime;

namespace ThanyMarcus.Portal.Api.Infrastructure.Database;

public interface IHasUpdatedAt
{
    Instant UpdatedAt { get; set; }
}
