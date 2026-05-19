using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;

public sealed record DigitalOceanOAuthState(string State, Guid UserId, string ReturnTo, long IssuedAtUnixSeconds);

public sealed class DigitalOceanOAuthStateCookie(IDataProtectionProvider dpProvider)
{
    public const string CookieName = "__do_oauth_state";
    private static readonly TimeSpan TtlBudget = TimeSpan.FromMinutes(10);
    private readonly IDataProtector protector = dpProvider.CreateProtector("digitalocean-oauth-state.v1");

    public static string GenerateState() =>
        Base64Url(RandomNumberGenerator.GetBytes(32));

    public string Protect(DigitalOceanOAuthState state) =>
        protector.Protect(JsonSerializer.Serialize(state));

    public DigitalOceanOAuthState? Unprotect(string protectedValue)
    {
        try
        {
            var json  = protector.Unprotect(protectedValue);
            return JsonSerializer.Deserialize<DigitalOceanOAuthState>(json);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public bool IsFresh(DigitalOceanOAuthState state, long nowUnixSeconds) =>
        nowUnixSeconds - state.IssuedAtUnixSeconds <= (long)TtlBudget.TotalSeconds;

    public void Write(HttpResponse response, string protectedValue) =>
        response.Cookies.Append(CookieName, protectedValue, new CookieOptions
        {
            HttpOnly = true,
            Secure   = true,
            SameSite = SameSiteMode.Lax,
            Path     = "/",
            MaxAge   = TtlBudget,
        });

    public void Clear(HttpResponse response) =>
        response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure   = true,
            SameSite = SameSiteMode.Lax,
            Path     = "/",
        });

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
