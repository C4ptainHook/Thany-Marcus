using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Portal.Api.Features.Auth.Captcha;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/auth");

        grp.MapGet("/me", async (
            ClaimsPrincipal user,
            PortalDbContext db,
            CancellationToken ct) =>
        {
            if (user.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var passphraseSet = await db.Users
                .Where(u => u.Id == userId)
                .Select(u => u.PassphraseWrappedDek != null)
                .SingleAsync(ct);
            return Results.Ok(new MeResponse(
                UserId: userId,
                Email: user.FindFirstValue(ClaimTypes.Email),
                Username: user.FindFirstValue(AuthClaimTypes.Username)!,
                Name: user.FindFirstValue(ClaimTypes.Name)!,
                ProfilePictureUrl: user.FindFirstValue("picture"),
                Totp: user.FindFirstValue(AuthClaimTypes.Totp)!,
                PassphraseSet: passphraseSet));
        });

        grp.MapPost("/signout", async (
            ClaimsPrincipal user,
            HttpContext http,
            IInfraOpUnlockCache cache,
            CancellationToken ct) =>
        {
            var sub = user.FindFirstValue(AuthClaimTypes.SubUs);
            if (sub is not null && Guid.TryParse(sub, out var userId))
                await cache.InvalidateAsync(userId, ct);
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/");
        });

        grp.MapGet("/signin", () =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = "/" },
                [GoogleDefaults.AuthenticationScheme]))
           .AddEndpointFilter<RequireTurnstileFilter>()
           .WithMetadata(new TurnstileKindMetadata(TurnstileKinds.Signin));
    }
}
