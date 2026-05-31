using System.Text.Json.Nodes;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.Captcha;
using ThanyMarcus.Portal.Api.Features.Auth.RateLimiting;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.Passkey;

/// <summary>
/// Username-only ("sovereign") signup: no Google, no email. An anonymous-write surface, so the
/// challenge step is CAPTCHA-gated and rate-limited; completion atomically creates the user and
/// its first passkey, then issues the session cookie.
/// </summary>
public static class PasskeySignupEndpoints
{
    public static void MapPasskeySignupEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/auth/passkey/signup");
        grp.MapPost("/challenge", SignupChallengeAsync)
            .RequireRateLimiting(AuthRateLimiterPolicies.SignupPasskey);
        grp.MapPost("/complete", SignupCompleteAsync);
    }

    private static async Task<IResult> SignupChallengeAsync(
        PasskeySignupChallengeRequest req,
        IFido2 fido2,
        PortalDbContext db,
        IPasskeyChallengeStore store,
        ITurnstileValidator turnstile,
        HttpContext http,
        CancellationToken ct)
    {
        // CAPTCHA first: bot defence for the anonymous-write surface. No-ops when Turnstile is
        // unconfigured (dev/test), which keeps the rest of the flow exercisable.
        var captcha = await turnstile.VerifyAsync(
            req.TurnstileToken ?? "", http.Connection.RemoteIpAddress?.ToString(), ct);
        if (!captcha.Success)
            return Results.BadRequest(new { error = "captcha_invalid" });

        if (!req.AcknowledgedNoRecovery)
            return Results.BadRequest(new { error = "acknowledgement_required" });

        var validated = UsernameValidator.Validate(req.Username);
        if (!validated.IsOk)
            return Results.BadRequest(new { error = $"username_{validated.Reason}" });

        if (await db.Users.AnyAsync(u => EF.Functions.ILike(u.Username, validated.Username), ct))
            return Results.Conflict(new { error = "username_taken" });

        var newUserId = Guid.CreateVersion7();
        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User
            {
                Id = newUserId.ToByteArray(),
                Name = validated.Username,
                DisplayName = validated.Username,
            },
            AuthenticatorSelection = new AuthenticatorSelection
            {
                // Resident (discoverable) credential is required: at login there is no username to
                // narrow the credential list, so the device must surface the passkey on its own.
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var json = options.ToJson();
        var challengeId = store.Stash(json, newUserId, PasskeyChallengeKind.Signup, validated.Username);
        return Results.Ok(new { challengeId, options = JsonNode.Parse(json) });
    }

    private static async Task<IResult> SignupCompleteAsync(
        PasskeySignupCompleteRequest req,
        IFido2 fido2,
        HttpContext http,
        PortalDbContext db,
        IPasskeyChallengeStore store,
        PasskeySignInHandler signIn,
        IClock clock,
        CancellationToken ct)
    {
        var record = store.Take(req.ChallengeId);
        if (record is null
            || record.Kind != PasskeyChallengeKind.Signup
            || record.UserId is not { } newUserId
            || record.Username is not { } username)
            return Results.BadRequest(new { error = "challenge_invalid" });

        if (await db.Users.AnyAsync(u => EF.Functions.ILike(u.Username, username), ct))
            return Results.Conflict(new { error = "username_taken" });

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
        var user = new User
        {
            Id = newUserId,
            GoogleSubject = null,
            Email = null,
            Username = username,
            Name = username,
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Users.Add(user);
        db.PasskeyCredentials.Add(new PasskeyCredential
        {
            UserId = newUserId,
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

        try
        {
            // One SaveChanges = one transaction: the user and its first credential land together,
            // and the unique index on lower(username) decides concurrent races.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return Results.Conflict(new { error = "username_taken" });
        }

        await signIn.SignInAsync(http, user, ct);
        return Results.NoContent();
    }
}
