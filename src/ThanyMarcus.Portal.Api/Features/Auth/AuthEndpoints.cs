using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/auth");

        grp.MapGet("/me", (ClaimsPrincipal user) =>
        {
            if (user.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();
            return Results.Ok(new MeResponse(
                UserId: Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!),
                Email: user.FindFirstValue(ClaimTypes.Email)!,
                Name: user.FindFirstValue(ClaimTypes.Name)!,
                ProfilePictureUrl: user.FindFirstValue("picture"),
                Totp: user.FindFirstValue(AuthClaimTypes.Totp)!));
        });

        grp.MapPost("/signout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        grp.MapGet("/signin", () =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = "/" },
                [GoogleDefaults.AuthenticationScheme]));

        app.MapGet("/totp-challenge", () => Results.Content(
            """
            <!DOCTYPE html>
            <html><body><h1>TOTP challenge</h1>
            <p>POST handler ships in PORTAL-003a.</p>
            </body></html>
            """,
            "text/html"));
    }
}
