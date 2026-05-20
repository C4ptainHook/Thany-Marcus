using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.StepUp;

public sealed class RequireInfraOpUnlockFilterTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
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

    [Fact]
    public async Task Returns_step_up_required_when_cache_is_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock);
        using var client = factory.CreateClient();

        var res = await client.GetAsync(new Uri("/api/test/step-up-only", UriKind.Relative), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>(ct);
        body!.Error.ShouldBe("step_up_required");
    }

    [Fact]
    public async Task Returns_200_after_cache_set_and_401_after_ttl_expires()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock);
        using var client = factory.CreateClient();

        var dek = new byte[32];
        Array.Fill(dek, (byte)0xAB);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var cache = scope.ServiceProvider.GetRequiredService<IInfraOpUnlockCache>();
            await cache.SetAsync(user.Id, dek, ct);
        }

        var ok = await client.GetAsync(new Uri("/api/test/step-up-only", UriKind.Relative), ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);

        Clock.Advance(PostgresInfraOpUnlockCache.SlidingTtl + Duration.FromMinutes(1));

        var expired = await client.GetAsync(new Uri("/api/test/step-up-only", UriKind.Relative), ct);
        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await expired.Content.ReadFromJsonAsync<ErrorBody>(ct))!.Error.ShouldBe("step_up_required");
    }

    private sealed record ErrorBody(string Error);
}
