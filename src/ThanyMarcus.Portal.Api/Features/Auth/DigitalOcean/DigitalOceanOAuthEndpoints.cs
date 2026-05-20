using System.Security.Claims;
using System.Security.Cryptography;
using System.Web;
using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;

namespace ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;

public static class DigitalOceanOAuthEndpoints
{
    private const string DefaultReturnTo = "/clouds/new";

    public static void MapDigitalOceanOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/oauth/digitalocean/start", (
            HttpContext http,
            ClaimsPrincipal user,
            DigitalOceanOAuthStateCookie stateCookie,
            IOptions<DigitalOceanOAuthOptions> options,
            IClock clock,
            string? return_to) =>
        {
            var userIdClaim = user.FindFirstValue(AuthClaimTypes.SubUs);
            if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
                return Results.Unauthorized();

            var returnTo = SanitizeReturnTo(return_to);

            var stateNonce = DigitalOceanOAuthStateCookie.GenerateState();
            var state = new DigitalOceanOAuthState(
                State: stateNonce,
                UserId: userId,
                ReturnTo: returnTo,
                IssuedAtUnixSeconds: clock.GetCurrentInstant().ToUnixTimeSeconds());

            stateCookie.Write(http.Response, stateCookie.Protect(state));

            var opts = options.Value;
            var qs   = HttpUtility.ParseQueryString(string.Empty);
            qs["response_type"] = "code";
            qs["client_id"]     = opts.ClientId;
            qs["redirect_uri"]  = opts.CallbackUrl;
            qs["scope"]         = "read write";
            qs["state"]         = stateNonce;

            return Results.Redirect($"{opts.AuthorizeEndpoint}?{qs}");
        })
        .RequireAuthorization(AuthPolicies.TotpRequired);

        app.MapGet("/oauth/digitalocean/callback", async (
            HttpContext http,
            ClaimsPrincipal user,
            DigitalOceanOAuthStateCookie stateCookie,
            IDigitalOceanOAuthClient doClient,
            IInfraOpUnlockCache unlockCache,
            IDigitalOceanOAuthConnections connections,
            IClock clock,
            string? code,
            string? state,
            string? error,
            CancellationToken ct) =>
        {
            stateCookie.Clear(http.Response);

            if (!string.IsNullOrEmpty(error))
                return Results.Redirect($"{DefaultReturnTo}?error={Uri.EscapeDataString(error)}");

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
                return Results.BadRequest(new { error = "missing_code_or_state" });

            if (!http.Request.Cookies.TryGetValue(DigitalOceanOAuthStateCookie.CookieName, out var rawCookie))
                return Results.BadRequest(new { error = "missing_state_cookie" });

            var stored = stateCookie.Unprotect(rawCookie);
            if (stored is null)
                return Results.BadRequest(new { error = "invalid_state_cookie" });

            var nowInstant = clock.GetCurrentInstant();
            if (!stateCookie.IsFresh(stored, nowInstant.ToUnixTimeSeconds()))
                return Results.BadRequest(new { error = "state_expired" });

            if (!CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(stored.State),
                    System.Text.Encoding.UTF8.GetBytes(state)))
                return Results.BadRequest(new { error = "state_mismatch" });

            var userIdClaim = user.FindFirstValue(AuthClaimTypes.SubUs);
            if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId) || userId != stored.UserId)
                return Results.Unauthorized();

            var returnTo = SanitizeReturnTo(stored.ReturnTo);

            var dek = new byte[32];
            try
            {
                if (!await unlockCache.TryGetAsync(userId, dek, ct))
                    return Results.Redirect($"{returnTo}?error=step_up_required");

                DoTokenResponse token;
                try
                {
                    token = await doClient.ExchangeCodeAsync(code, ct);
                }
                catch (DigitalOceanOAuthException)
                {
                    return Results.Redirect($"{returnTo}?error=oauth_failed");
                }

                var accessExpiresAt = nowInstant.Plus(Duration.FromSeconds(token.ExpiresIn));
                await connections.SaveAsync(userId,
                    token.AccessToken, token.RefreshToken, accessExpiresAt, dek, ct);

                return Results.Redirect($"{returnTo}?connected=1");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        })
        .RequireAuthorization(AuthPolicies.TotpRequired);
    }

    private static string SanitizeReturnTo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultReturnTo;
        if (!value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal))
            return DefaultReturnTo;
        return value;
    }
}
