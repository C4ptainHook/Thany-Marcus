using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;

namespace ThanyMarcus.Portal.Tests.Infrastructure;

public sealed class TimestampInterceptorTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task UpdatedAt_is_bumped_on_modify()
    {
        var ct = TestContext.Current.CancellationToken;
        var t0 = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g", Email = "u@x.com", Name = "U", LastSeenAt = t0, CreatedAt = t0, UpdatedAt = t0 };
        Db.Users.Add(user);
        await Db.SaveChangesAsync(ct);

        Clock.AdvanceMinutes(5);
        var t1 = Clock.GetCurrentInstant();

        user.Name = "Renamed";
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var fetched = await Db.Users.SingleAsync(u => u.Id == user.Id, ct);
        fetched.UpdatedAt.ShouldBe(t1);
        fetched.CreatedAt.ShouldBe(t0);
        _ = (Duration)(t1 - t0);
    }
}
