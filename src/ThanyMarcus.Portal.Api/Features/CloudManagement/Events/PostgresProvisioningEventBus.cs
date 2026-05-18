using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Events;

public sealed class PostgresProvisioningEventBus(PortalDbContext db) : IProvisioningEventBus
{
    public const string PluginTokenIssuedChannel = "plugin_token_issued";

    public async Task PublishPluginTokenIssuedAsync(
        Guid cloudId, string rawToken, string deepLink, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            cloudId = cloudId,
            rawToken = rawToken,
            deepLink = deepLink,
        });
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_notify({0}, {1})",
            [PluginTokenIssuedChannel, payload], ct);
    }
}
