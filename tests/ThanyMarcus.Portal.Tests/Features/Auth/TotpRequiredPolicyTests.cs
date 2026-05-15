using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;

namespace ThanyMarcus.Portal.Tests.Features.Auth;

public sealed class TotpRequiredPolicyTests
{
    private static IAuthorizationService BuildAuthService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(opts =>
        {
            opts.AddPolicy(AuthPolicies.TotpRequired, p => p.RequireAssertion(c =>
                c.User.FindFirstValue(AuthClaimTypes.Totp)
                    is TotpClaimValues.Verified or TotpClaimValues.NotEnabled));
        });
        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal Principal(string totp)
    {
        var identity = new ClaimsIdentity(
            [new Claim(AuthClaimTypes.Totp, totp), new Claim(AuthClaimTypes.SubUs, Guid.NewGuid().ToString())],
            "Test");
        return new ClaimsPrincipal(identity);
    }

    [Theory]
    [InlineData(TotpClaimValues.Verified, true)]
    [InlineData(TotpClaimValues.NotEnabled, true)]
    [InlineData(TotpClaimValues.NotVerified, false)]
    public async Task TotpRequired_evaluates_correctly(string totp, bool expectAllowed)
    {
        var authService = BuildAuthService();
        var result = await authService.AuthorizeAsync(Principal(totp), null, AuthPolicies.TotpRequired);
        result.Succeeded.ShouldBe(expectAllowed);
    }
}
