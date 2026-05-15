using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace ThanyMarcus.Portal.Tests.Infrastructure;

public sealed class TestAuthOptions : AuthenticationSchemeOptions
{
    public Claim[] Claims { get; set; } = [];
    public Instant? IssuedAt { get; set; }
}

public sealed class TestAuthHandler(
    IOptionsMonitor<TestAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<TestAuthOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(Options.Claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var props = new AuthenticationProperties();
        if (Options.IssuedAt is { } issued)
            props.IssuedUtc = issued.ToDateTimeOffset();
        var ticket = new AuthenticationTicket(principal, props, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
