using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Shared.CloudAdmin;

namespace ThanyMarcus.Cloud.Api.Features.Admin.PluginTokens;

public static class AdminPluginTokenEndpoints
{
    private const int ExpectedHashLength = 32;
    private const int MaxLabelLength     = 128;

    public static void MapAdminPluginTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/plugin-tokens", PostAsync)
           .AddEndpointFilter<RequireCloudAdminTokenFilter>()
           .WithName("PostAdminPluginTokens")
           .Produces<AdminIssuePluginTokenResponse>(StatusCodes.Status200OK)
           .ProducesProblem(StatusCodes.Status400BadRequest)
           .ProducesProblem(StatusCodes.Status401Unauthorized)
           .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> PostAsync(
        AdminIssuePluginTokenRequest req,
        CloudDbContext db,
        IClock clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.TokenHashBase64))
        {
            return Results.Problem("missing_token_hash", statusCode: StatusCodes.Status400BadRequest);
        }
        if (string.IsNullOrWhiteSpace(req.Label) || req.Label.Length > MaxLabelLength)
        {
            return Results.Problem("missing_label", statusCode: StatusCodes.Status400BadRequest);
        }

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
        {
            return Results.Problem("invalid_hash_length", statusCode: StatusCodes.Status400BadRequest);
        }

        var existing = await db.PluginTokens.SingleOrDefaultAsync(t => t.TokenHash == hashBytes, ct);
        if (existing is not null)
        {
            existing.Label = req.Label;
            existing.RevokedAt = null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new AdminIssuePluginTokenResponse(existing.Id));
        }

        var settings = await db.CloudSettings.SingleAsync(s => s.Id == CloudSettings.SingletonId, ct);
        if (settings.BootstrapConsumedAt is not null)
        {
            return Results.Problem("bootstrap_token_consumed", statusCode: StatusCodes.Status409Conflict);
        }

        var now = clock.GetCurrentInstant();
        var row = new PluginToken
        {
            TokenHash = hashBytes,
            Label     = req.Label,
            CreatedAt = now,
        };
        db.PluginTokens.Add(row);
        settings.BootstrapConsumedAt = now;
        settings.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new AdminIssuePluginTokenResponse(row.Id));
    }
}
