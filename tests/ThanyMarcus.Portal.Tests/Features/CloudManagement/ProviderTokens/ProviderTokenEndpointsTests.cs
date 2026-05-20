using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.CloudManagement.ProviderTokens;

public sealed class ProviderTokenEndpointsTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    private static readonly Uri Root = new("/api/clouds/provider-tokens", UriKind.Relative);
    private static readonly JsonSerializerOptions NodaJson =
        new JsonSerializerOptions(JsonSerializerDefaults.Web).ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

    private async Task<User> InsertUserAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = $"sub-{Guid.NewGuid()}",
            Email = "alice@example.com",
            Name = "Alice",
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return user;
    }

    private static async Task SeedStepUpAsync(IServiceProvider services, Guid userId, byte[] dek, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var cache = scope.ServiceProvider.GetRequiredService<IInfraOpUnlockCache>();
        await cache.SetAsync(userId, dek, ct);
    }

    [Fact]
    public async Task Post_without_auth_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.WithUnauthenticated().WithClock(Clock).CreateClient();

        var res = await client.PostAsJsonAsync(Root,
            new RegisterProviderTokenRequest(KnownProviders.DigitalOcean, "dop_v1_x"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Post_without_step_up_returns_401_step_up_required()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        using var client = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock)
            .CreateClient();

        var res = await client.PostAsJsonAsync(Root,
            new RegisterProviderTokenRequest(KnownProviders.DigitalOcean, "dop_v1_x"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>(ct);
        body!.Error.ShouldBe("step_up_required");
    }

    [Fact]
    public async Task Post_with_step_up_returns_204_and_persists_ciphertext()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock);
        using var client = factory.CreateClient();

        var dek = RandomNumberGenerator.GetBytes(32);
        await SeedStepUpAsync(factory.Services, user.Id, dek, ct);

        var res = await client.PostAsJsonAsync(Root,
            new RegisterProviderTokenRequest(KnownProviders.Azure, "az-secret"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var row = await Db.EncryptedProviderTokens.AsNoTracking()
            .SingleAsync(t => t.UserId == user.Id && t.Provider == KnownProviders.Azure, ct);
        row.Ciphertext.ShouldNotBeEmpty();
        row.Nonce.Length.ShouldBe(12);
        row.Tag.Length.ShouldBe(16);
    }

    [Fact]
    public async Task Post_digitalocean_token_returns_400_use_oauth_flow()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, RandomNumberGenerator.GetBytes(32), ct);

        var res = await client.PostAsJsonAsync(Root,
            new RegisterProviderTokenRequest(KnownProviders.DigitalOcean, "dop_v1_x"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>(ct);
        body!.Error.ShouldBe("use_oauth_flow");

        (await Db.EncryptedProviderTokens.AsNoTracking()
            .AnyAsync(t => t.UserId == user.Id && t.Provider == KnownProviders.DigitalOcean, ct))
            .ShouldBeFalse();
    }

    [Fact]
    public async Task Post_second_time_same_provider_returns_409()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, RandomNumberGenerator.GetBytes(32), ct);

        (await client.PostAsJsonAsync(Root,
            new RegisterProviderTokenRequest(KnownProviders.Azure, "first"), ct))
            .EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync(Root,
            new RegisterProviderTokenRequest(KnownProviders.Azure, "second"), ct);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await second.Content.ReadFromJsonAsync<ConflictBody>(ct);
        body!.Error.ShouldBe("provider_token_already_set");
        body.Provider.ShouldBe(KnownProviders.Azure);
    }

    [Fact]
    public async Task Post_with_replace_true_returns_204_and_ciphertext_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, RandomNumberGenerator.GetBytes(32), ct);

        (await client.PostAsJsonAsync(Root,
            new RegisterProviderTokenRequest(KnownProviders.Cloudflare, "first"), ct))
            .EnsureSuccessStatusCode();
        var before = await Db.EncryptedProviderTokens.AsNoTracking()
            .SingleAsync(t => t.UserId == user.Id && t.Provider == KnownProviders.Cloudflare, ct);

        var replace = await client.PostAsJsonAsync(Root,
            new RegisterProviderTokenRequest(KnownProviders.Cloudflare, "rotated", Replace: true), ct);

        replace.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Db.ChangeTracker.Clear();
        var after = await Db.EncryptedProviderTokens.AsNoTracking()
            .SingleAsync(t => t.UserId == user.Id && t.Provider == KnownProviders.Cloudflare, ct);
        after.Id.ShouldBe(before.Id);
        after.Ciphertext.ShouldNotBe(before.Ciphertext);
    }

    [Fact]
    public async Task Post_with_unknown_provider_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, RandomNumberGenerator.GetBytes(32), ct);

        var res = await client.PostAsJsonAsync(Root,
            new RegisterProviderTokenRequest("hetzner", "token"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>(ct);
        body!.Error.ShouldBe("unknown_provider");
    }

    [Fact]
    public async Task Post_with_empty_token_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, RandomNumberGenerator.GetBytes(32), ct);

        var res = await client.PostAsJsonAsync(Root,
            new RegisterProviderTokenRequest(KnownProviders.Azure, "   "), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>(ct);
        body!.Error.ShouldBe("token_required");
    }

    [Fact]
    public async Task Get_lists_providers_without_exposing_ciphertext()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, RandomNumberGenerator.GetBytes(32), ct);

        (await client.PostAsJsonAsync(Root,
            new RegisterProviderTokenRequest(KnownProviders.Azure, "az-secret-aaa"), ct))
            .EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(Root,
            new RegisterProviderTokenRequest(KnownProviders.Cloudflare, "cf-secret-bbb"), ct))
            .EnsureSuccessStatusCode();

        var res = await client.GetAsync(Root, ct);
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rawBody = await res.Content.ReadAsStringAsync(ct);
        rawBody.ShouldNotContain("az-secret-aaa");
        rawBody.ShouldNotContain("cf-secret-bbb");
        rawBody.ShouldNotContain("ciphertext");
        rawBody.ShouldNotContain("nonce");
        rawBody.ShouldNotContain("tag");

        var summaries = await res.Content.ReadFromJsonAsync<List<ProviderTokenSummary>>(NodaJson, ct);
        summaries!.Select(s => s.Provider).ShouldBe([KnownProviders.Azure, KnownProviders.Cloudflare]);
    }

    [Fact]
    public async Task Get_returns_only_current_users_tokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = await InsertUserAsync();
        var bobNow = Clock.GetCurrentInstant();
        var bob = new User
        {
            GoogleSubject = "bob",
            Email = "bob@example.com",
            Name = "Bob",
            LastSeenAt = bobNow,
            CreatedAt = bobNow,
            UpdatedAt = bobNow,
        };
        Db.Users.Add(bob);
        Db.EncryptedProviderTokens.Add(new EncryptedProviderToken
        {
            UserId = bob.Id,
            Provider = KnownProviders.DigitalOcean,
            Ciphertext = [1, 2, 3],
            Nonce = new byte[12],
            Tag = new byte[16],
            CreatedAt = bobNow,
            UpdatedAt = bobNow,
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        using var client = Factory
            .WithTestAuth(alice.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock)
            .CreateClient();

        var res = await client.GetAsync(Root, ct);
        var summaries = await res.Content.ReadFromJsonAsync<List<ProviderTokenSummary>>(ct);

        summaries!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_removes_row_and_subsequent_Get_omits_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, RandomNumberGenerator.GetBytes(32), ct);

        (await client.PostAsJsonAsync(Root,
            new RegisterProviderTokenRequest(KnownProviders.Azure, "secret"), ct))
            .EnsureSuccessStatusCode();

        var del = await client.DeleteAsync(
            new Uri($"/api/clouds/provider-tokens/{KnownProviders.Azure}", UriKind.Relative), ct);
        del.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await Db.EncryptedProviderTokens.AsNoTracking()
            .AnyAsync(t => t.UserId == user.Id && t.Provider == KnownProviders.Azure, ct))
            .ShouldBeFalse();

        var list = await client.GetAsync(Root, ct);
        var summaries = await list.Content.ReadFromJsonAsync<List<ProviderTokenSummary>>(ct);
        summaries!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_without_step_up_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        using var client = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock)
            .CreateClient();

        var res = await client.DeleteAsync(
            new Uri($"/api/clouds/provider-tokens/{KnownProviders.DigitalOcean}", UriKind.Relative), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Delete_with_unknown_provider_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock);
        using var client = factory.CreateClient();
        await SeedStepUpAsync(factory.Services, user.Id, RandomNumberGenerator.GetBytes(32), ct);

        var res = await client.DeleteAsync(
            new Uri("/api/clouds/provider-tokens/hetzner", UriKind.Relative), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private sealed record ErrorBody(string Error);
    private sealed record ConflictBody(string Error, string Provider);
}
