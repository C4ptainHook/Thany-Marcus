using System.Security.Claims;
using System.Text.Json.Nodes;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.Passkey;

public static class PasskeyEndpoints
{
    public static void MapPasskeyEndpoints(this IEndpointRouteBuilder app)
    {
        var pk = app.MapGroup("/api/auth/passkey");
        pk.MapPost("/register/challenge", RegisterChallengeAsync).RequireAuthorization();
        pk.MapPost("/register/complete", RegisterCompleteAsync).RequireAuthorization();
        pk.MapPost("/login/challenge", LoginChallenge);
        pk.MapPost("/login/complete", LoginCompleteAsync);

        app.MapGet("/api/auth/passkeys", ListAsync).RequireAuthorization();
        app.MapDelete("/api/auth/passkeys/{id:guid}", RevokeAsync).RequireAuthorization();
    }

    private static async Task<IResult> RegisterChallengeAsync(
        IFido2 fido2,
        ClaimsPrincipal principal,
        PortalDbContext db,
        IPasskeyChallengeStore store,
        CancellationToken ct)
    {
        var userId = Guid.Parse(principal.FindFirstValue(AuthClaimTypes.SubUs)!);
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        var existing = await db.PasskeyCredentials
            .Where(p => p.UserId == userId && p.RevokedAt == null)
            .Select(p => p.CredentialId)
            .ToListAsync(ct);

        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User
            {
                Id = userId.ToByteArray(),
                Name = user.Email ?? user.Username,
                DisplayName = user.Name,
            },
            ExcludeCredentials = existing.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var json = options.ToJson();
        var challengeId = store.Stash(json, userId, PasskeyChallengeKind.Register);
        return Results.Ok(new { challengeId, options = JsonNode.Parse(json) });
    }

    private static async Task<IResult> RegisterCompleteAsync(
        PasskeyRegisterCompleteRequest req,
        IFido2 fido2,
        ClaimsPrincipal principal,
        PortalDbContext db,
        IPasskeyChallengeStore store,
        IClock clock,
        CancellationToken ct)
    {
        var userId = Guid.Parse(principal.FindFirstValue(AuthClaimTypes.SubUs)!);

        var record = store.Take(req.ChallengeId);
        if (record is null || record.Kind != PasskeyChallengeKind.Register || record.UserId != userId)
            return Results.BadRequest(new { error = "challenge_invalid" });

        if (await db.PasskeyCredentials.AnyAsync(
                p => p.CredentialId == req.Response.RawId && p.RevokedAt == null, ct))
            return Results.Conflict(new { error = "credential_already_registered" });

        var options = CredentialCreateOptions.FromJson(record.OptionsJson);

        RegisteredPublicKeyCredential credential;
        try
        {
            credential = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = req.Response,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = async (args, token) =>
                    !await db.PasskeyCredentials.AnyAsync(
                        p => p.CredentialId == args.CredentialId && p.RevokedAt == null, token),
            }, ct);
        }
        catch (Fido2VerificationException)
        {
            return Results.BadRequest(new { error = "attestation_invalid" });
        }

        var now = clock.GetCurrentInstant();
        db.PasskeyCredentials.Add(new PasskeyCredential
        {
            UserId = userId,
            CredentialId = credential.Id,
            PublicKey = credential.PublicKey,
            SignCount = credential.SignCount,
            Aaguid = credential.AaGuid,
            AuthenticatorName = AaguidNames.Resolve(credential.AaGuid),
            Transports = credential.Transports?.Select(t => t.ToString().ToLowerInvariant()).ToArray() ?? [],
            BackedUp = credential.IsBackedUp,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static IResult LoginChallenge(IFido2 fido2, IPasskeyChallengeStore store)
    {
        // Discoverable credentials: empty AllowedCredentials lets the device pick from
        // its stored passkeys without the client supplying a username.
        var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required,
        });

        var json = options.ToJson();
        var challengeId = store.Stash(json, userId: null, PasskeyChallengeKind.Login);
        return Results.Ok(new { challengeId, options = JsonNode.Parse(json) });
    }

    private static async Task<IResult> LoginCompleteAsync(
        PasskeyLoginCompleteRequest req,
        IFido2 fido2,
        HttpContext http,
        PortalDbContext db,
        IPasskeyChallengeStore store,
        PasskeySignInHandler signIn,
        IClock clock,
        CancellationToken ct)
    {
        var record = store.Take(req.ChallengeId);
        if (record is null || record.Kind != PasskeyChallengeKind.Login)
            return Results.BadRequest(new { error = "challenge_invalid" });

        var cred = await db.PasskeyCredentials
            .SingleOrDefaultAsync(p => p.CredentialId == req.Response.RawId && p.RevokedAt == null, ct);
        if (cred is null)
            return Results.Json(new { error = "credential_not_found" }, statusCode: StatusCodes.Status401Unauthorized);

        var options = AssertionOptions.FromJson(record.OptionsJson);

        VerifyAssertionResult result;
        try
        {
            result = await fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = req.Response,
                OriginalOptions = options,
                StoredPublicKey = cred.PublicKey,
                StoredSignatureCounter = (uint)cred.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, token) =>
                {
                    if (args.UserHandle is not { Length: 16 })
                        return Task.FromResult(false);
                    var handleUserId = new Guid(args.UserHandle);
                    return db.PasskeyCredentials.AnyAsync(
                        p => p.CredentialId == args.CredentialId
                          && p.UserId == handleUserId
                          && p.RevokedAt == null, token);
                },
            }, ct);
        }
        catch (Fido2VerificationException)
        {
            return Results.Json(new { error = "assertion_invalid" }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var user = await db.Users.SingleAsync(u => u.Id == cred.UserId, ct);
        cred.SignCount = result.SignCount;
        cred.BackedUp = result.IsBackedUp;
        cred.LastUsedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);

        await signIn.SignInAsync(http, user, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        PortalDbContext db,
        CancellationToken ct)
    {
        var userId = Guid.Parse(principal.FindFirstValue(AuthClaimTypes.SubUs)!);
        var rows = await db.PasskeyCredentials
            .Where(p => p.UserId == userId && p.RevokedAt == null)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new { p.Id, p.AuthenticatorName, p.BackedUp, p.CreatedAt, p.LastUsedAt })
            .ToListAsync(ct);

        var dtos = rows.Select(p => new PasskeyDto(
            p.Id,
            p.AuthenticatorName ?? AaguidNames.Fallback,
            p.BackedUp,
            p.CreatedAt.ToDateTimeOffset(),
            p.LastUsedAt?.ToDateTimeOffset())).ToList();
        return Results.Ok(dtos);
    }

    private static async Task<IResult> RevokeAsync(
        Guid id,
        ClaimsPrincipal principal,
        PortalDbContext db,
        IClock clock,
        CancellationToken ct)
    {
        var userId = Guid.Parse(principal.FindFirstValue(AuthClaimTypes.SubUs)!);
        var cred = await db.PasskeyCredentials
            .SingleOrDefaultAsync(p => p.Id == id && p.UserId == userId && p.RevokedAt == null, ct);
        if (cred is null)
            return Results.NotFound();
        cred.RevokedAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
