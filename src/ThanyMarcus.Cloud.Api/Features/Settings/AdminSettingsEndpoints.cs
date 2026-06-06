using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Shared.CloudAdmin;

namespace ThanyMarcus.Cloud.Api.Features.Settings;

public static class AdminSettingsEndpoints
{
    public const string DataProtectionPurpose = "ThanyMarcus.Cloud.Settings.ExternalApiKey";

    public static void MapAdminSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/settings").AddEndpointFilter<RequirePluginAuthFilter>();

        group.MapGet("", GetAsync)
             .WithName("GetAdminSettings")
             .Produces<GetSettingsResponse>(StatusCodes.Status200OK)
             .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPut("", PutAsync)
             .WithName("PutAdminSettings")
             .Produces<GetSettingsResponse>(StatusCodes.Status200OK)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    private static async Task<IResult> GetAsync(CloudDbContext db, CancellationToken ct)
    {
        var s = await db.CloudSettings.SingleAsync(x => x.Id == CloudSettings.SingletonId, ct);
        return Results.Ok(new GetSettingsResponse(
            LlmMode:           s.LlmMode,
            LlmModel:          s.LlmModel,
            ExternalApiKeySet: s.EncryptedExternalApiKey is { Length: > 0 }));
    }

    private static async Task<IResult> PutAsync(
        PutSettingsRequest req,
        CloudDbContext db,
        IDataProtectionProvider dp,
        IClock clock,
        CancellationToken ct)
    {
        if (!LlmModes.IsValid(req.LlmMode))
        {
            return Results.Problem($"unknown llmMode '{req.LlmMode}'",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var s = await db.CloudSettings.SingleAsync(x => x.Id == CloudSettings.SingletonId, ct);
        s.LlmMode  = req.LlmMode;
        s.LlmModel = req.LlmModel;
        s.UpdatedAt = clock.GetCurrentInstant();

        if (!string.IsNullOrWhiteSpace(req.ExternalApiKey))
        {
            var protector = dp.CreateProtector(DataProtectionPurpose);
            var ciphertext = protector.Protect(Encoding.UTF8.GetBytes(req.ExternalApiKey));
            s.EncryptedExternalApiKey = ciphertext;
        }
        else if (req.LlmMode == LlmModes.Safe)
        {
            s.EncryptedExternalApiKey = null;
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(new GetSettingsResponse(
            LlmMode:           s.LlmMode,
            LlmModel:          s.LlmModel,
            ExternalApiKeySet: s.EncryptedExternalApiKey is { Length: > 0 }));
    }
}
