using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.Auth.Lockout;

public sealed class AuthLockoutService(PortalDbContext db, IClock clock, IOptions<LockoutOptions> options)
{
    public async Task<LockoutState> IsLockedAsync(Guid userId, string kind, CancellationToken ct)
    {
        var row = await db.AuthLockouts
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.UserId == userId && a.Kind == kind, ct);
        if (row is null || row.LockedUntil is null) return LockoutState.NotLocked;
        var now = clock.GetCurrentInstant();
        if (row.LockedUntil <= now) return LockoutState.NotLocked;
        var remaining = (row.LockedUntil.Value - now).TotalSeconds;
        return new LockoutState(IsLocked: true, LockedUntil: row.LockedUntil.Value, RemainingSeconds: (int)Math.Ceiling(remaining));
    }

    public async Task RecordFailureAsync(Guid userId, string kind, CancellationToken ct)
    {
        var cfg = ConfigFor(kind);
        var now = clock.GetCurrentInstant();
        var row = await db.AuthLockouts.SingleOrDefaultAsync(a => a.UserId == userId && a.Kind == kind, ct);
        if (row is null)
        {
            db.AuthLockouts.Add(new AuthLockout
            {
                UserId        = userId,
                Kind          = kind,
                FailedCount   = 1,
                LastAttemptAt = now,
            });
            await db.SaveChangesAsync(ct);
            return;
        }

        var windowStart = now - Duration.FromSeconds(cfg.WindowSeconds);
        row.FailedCount = row.LastAttemptAt < windowStart
            ? (short)1
            : (short)Math.Min(row.FailedCount + 1, short.MaxValue);

        row.LastAttemptAt = now;
        if (row.FailedCount >= cfg.MaxFailures)
            row.LockedUntil = now + Duration.FromSeconds(cfg.LockoutSeconds);

        await db.SaveChangesAsync(ct);
    }

    public async Task ClearAsync(Guid userId, string kind, CancellationToken ct)
    {
        var row = await db.AuthLockouts.SingleOrDefaultAsync(a => a.UserId == userId && a.Kind == kind, ct);
        if (row is null) return;
        row.FailedCount = 0;
        row.LockedUntil = null;
        row.LastAttemptAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    private LockoutOptions.KindOptions ConfigFor(string kind) => kind switch
    {
        AuthLockoutKinds.Totp   => options.Value.Totp,
        AuthLockoutKinds.Unlock => options.Value.Unlock,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown lockout kind"),
    };
}

public readonly record struct LockoutState(bool IsLocked, Instant? LockedUntil, int RemainingSeconds)
{
    public static readonly LockoutState NotLocked = new(false, null, 0);
}
