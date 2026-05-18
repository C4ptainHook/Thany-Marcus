using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Cloud.Tests.Features.PluginAuth;

[Collection(PostgresCollection.Name)]
public sealed class PluginTokenAuthenticatorTests(PostgresFixture postgres) : IAsyncLifetime
{
    private CloudDbContext db = null!;
    private PluginTokenAuthenticator auth = null!;

    public async ValueTask InitializeAsync()
    {
        await postgres.ResetAsync();
        var opts = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(postgres.ConnectionString, npg => npg.UseNodaTime().UseVector())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TimestampInterceptor(new FakeClock(Instant.FromUtc(2026, 5, 18, 0, 0))))
            .Options;
        db = new CloudDbContext(opts);
        auth = new PluginTokenAuthenticator(db);
    }

    public async ValueTask DisposeAsync() => await db.DisposeAsync();

    [Fact]
    public async Task Valid_token_returns_principal()
    {
        var ct = TestContext.Current.CancellationToken;
        var (raw, hash) = MakeToken("plain-token");
        db.PluginTokens.Add(new PluginToken
        {
            TokenHash = hash,
            Label = "test",
            CreatedAt = Instant.FromUtc(2026, 5, 18, 0, 0),
        });
        await db.SaveChangesAsync(ct);

        var principal = await auth.AuthenticateAsync($"Bearer {raw}", ct);

        principal.ShouldNotBeNull();
        principal.Label.ShouldBe("test");
    }

    [Fact]
    public async Task Wrong_token_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, hash) = MakeToken("plain-token");
        db.PluginTokens.Add(new PluginToken
        {
            TokenHash = hash,
            Label = "test",
            CreatedAt = Instant.FromUtc(2026, 5, 18, 0, 0),
        });
        await db.SaveChangesAsync(ct);

        var principal = await auth.AuthenticateAsync("Bearer different-token", ct);

        principal.ShouldBeNull();
    }

    [Fact]
    public async Task Revoked_token_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var (raw, hash) = MakeToken("revoked-token");
        db.PluginTokens.Add(new PluginToken
        {
            TokenHash = hash,
            Label = "test",
            CreatedAt = Instant.FromUtc(2026, 5, 17, 0, 0),
            RevokedAt = Instant.FromUtc(2026, 5, 18, 0, 0),
        });
        await db.SaveChangesAsync(ct);

        var principal = await auth.AuthenticateAsync($"Bearer {raw}", ct);

        principal.ShouldBeNull();
    }

    [Fact]
    public async Task Missing_authorization_header_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        (await auth.AuthenticateAsync(null, ct)).ShouldBeNull();
        (await auth.AuthenticateAsync("", ct)).ShouldBeNull();
        (await auth.AuthenticateAsync("Basic abc", ct)).ShouldBeNull();
        (await auth.AuthenticateAsync("Bearer ", ct)).ShouldBeNull();
    }

    private static (string raw, byte[] hash) MakeToken(string raw) =>
        (raw, SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
