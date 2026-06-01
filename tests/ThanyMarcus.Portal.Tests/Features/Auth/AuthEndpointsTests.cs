using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth;

public sealed class AuthEndpointsTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    private async Task<Guid> InsertUserAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = $"sub-{Guid.NewGuid()}",
            Email = $"u-{Guid.NewGuid():N}@x.com",
            Name = "U",
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return user.Id;
    }

    [Fact]
    public async Task Me_unauthenticated_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("https://example.com/pic.png")]
    [InlineData(null)]
    public async Task Me_authenticated_returns_claims(string? picture)
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = await InsertUserAsync();
        using var client = Factory.WithTestAuth(
            userId,
            email: "alice@example.com",
            name: "Alice",
            picture: picture,
            totp: TotpClaimValues.NotEnabled).CreateClient();

        var response = await client.GetAsync(new Uri("/api/auth/me", UriKind.Relative), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeResponse>(ct);
        body.ShouldNotBeNull();
        body!.UserId.ShouldBe(userId);
        body.Email.ShouldBe("alice@example.com");
        body.Name.ShouldBe("Alice");
        if (picture is null)
            body.ProfilePictureUrl.ShouldBeNull();
        else
            body.ProfilePictureUrl.ShouldBe(picture);
        body.Totp.ShouldBe(TotpClaimValues.NotEnabled);
        body.PassphraseSet.ShouldBeFalse();
    }

    [Fact]
    public async Task Signout_when_signed_in_clears_auth_cookie_and_redirects_home()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.WithTestAuth(Guid.CreateVersion7()).CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsync(new Uri("/api/auth/signout", UriKind.Relative), content: null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.OriginalString.ShouldBe("/");

        var setCookies = response.Headers.GetValues("Set-Cookie").ToList();
        var authCookie = setCookies.SingleOrDefault(c => c.StartsWith(".Portal.Auth=", StringComparison.Ordinal));
        authCookie.ShouldNotBeNull();
        authCookie.ShouldContain(".Portal.Auth=;");
        authCookie.ShouldContain("expires=Thu, 01 Jan 1970", Case.Insensitive);
    }

    [Fact]
    public async Task Signin_returns_302_to_google()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var response = await client.GetAsync(new Uri("/api/auth/signin", UriKind.Relative), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.Host.ShouldBe("accounts.google.com");
    }

    [Fact]
    public async Task TotpRequired_endpoint_returns_403_for_NotVerified_on_api_path()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.WithTestAuth(
            Guid.CreateVersion7(),
            totp: TotpClaimValues.NotVerified).CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

        var response = await client.GetAsync(new Uri("/api/test/totp-required", UriKind.Relative), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TotpRequired_endpoint_returns_200_for_Verified()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.WithTestAuth(
            Guid.CreateVersion7(),
            totp: TotpClaimValues.Verified).CreateClient();

        var response = await client.GetAsync(new Uri("/api/test/totp-required", UriKind.Relative), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
