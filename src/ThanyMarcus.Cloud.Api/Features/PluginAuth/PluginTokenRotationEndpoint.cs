using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Shared.CloudAdmin;

namespace ThanyMarcus.Cloud.Api.Features.PluginAuth;

public static class PluginTokenRotationEndpoint
{
    private const int ExpectedHashLength = 32;
    private const int MaxLabelLength = 128;

    public static void MapPluginTokenRotationEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/plugin-tokens/rotate", RotateAsync)
           .AddEndpointFilter<RequirePluginAuthFilter>()
           .WithName("RotatePluginToken")
           .Produces<AdminIssuePluginTokenResponse>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> RotateAsync(
        AdminIssuePluginTokenRequest req,
        HttpContext http,
        CloudDbContext db,
        IClock clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.TokenHashBase64))
            return Results.Problem("missing_token_hash", statusCode: StatusCodes.Status400BadRequest);
        if (string.IsNullOrWhiteSpace(req.Label) || req.Label.Length > MaxLabelLength)
            return Results.Problem("missing_label", statusCode: StatusCodes.Status400BadRequest);

        byte[] hashBytes;
        try
        {
            hashBytes = Convert.FromBase64String(req.TokenHashBase64);
        }
        catch (FormatException)
        {
            return Results.Problem("invalid_hash_encoding", statusCode: StatusCodes.Status400BadRequest);
        }
        if (hashBytes.Length != ExpectedHashLength)
            return Results.Problem("invalid_hash_length", statusCode: StatusCodes.Status400BadRequest);

        var caller = http.GetPluginPrincipal();
        var now = clock.GetCurrentInstant();

        var existing = await db.PluginTokens.SingleOrDefaultAsync(t => t.TokenHash == hashBytes, ct);
        Guid newId;
        if (existing is not null)
        {
            existing.Label = req.Label;
            existing.RevokedAt = null;
            newId = existing.Id;
        }
        else
        {
            var row = new PluginToken { TokenHash = hashBytes, Label = req.Label, CreatedAt = now };
            db.PluginTokens.Add(row);
            newId = row.Id;
        }

        if (caller.TokenId != newId)
        {
            var old = await db.PluginTokens.SingleOrDefaultAsync(t => t.Id == caller.TokenId, ct);
            if (old is not null && old.RevokedAt is null)
                old.RevokedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new AdminIssuePluginTokenResponse(newId));
    }
}
