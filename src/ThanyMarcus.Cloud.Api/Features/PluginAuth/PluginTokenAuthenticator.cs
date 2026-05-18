using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.PluginAuth;

public sealed class PluginTokenAuthenticator(CloudDbContext db) : IPluginTokenAuthenticator
{
    private const string BearerPrefix = "Bearer ";

    public async Task<PluginPrincipal?> AuthenticateAsync(string? authorizationHeader, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var token = authorizationHeader[BearerPrefix.Length..].Trim();
        if (token.Length == 0)
        {
            return null;
        }

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));

        var candidates = await db.Set<PluginToken>()
            .Where(t => t.RevokedAt == null)
            .Select(t => new { t.Id, t.Label, t.TokenHash })
            .ToListAsync(ct);

        foreach (var c in candidates)
        {
            if (c.TokenHash.Length == presentedHash.Length &&
                CryptographicOperations.FixedTimeEquals(c.TokenHash, presentedHash))
            {
                return new PluginPrincipal(c.Id, c.Label);
            }
        }

        return null;
    }
}
