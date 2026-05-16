namespace ThanyMarcus.Portal.Api.Features.Auth.Lockout;

public sealed record LockoutOptions
{
    public KindOptions Totp { get; init; } = new(MaxFailures: 20, WindowSeconds: 3600, LockoutSeconds: 1800);
    public KindOptions Unlock { get; init; } = new(MaxFailures: 20, WindowSeconds: 3600, LockoutSeconds: 1800);
    public SweepOptions Sweep { get; init; } = new(IntervalSeconds: 21600, RetentionDays: 30);

    public sealed record KindOptions(int MaxFailures, int WindowSeconds, int LockoutSeconds);
    public sealed record SweepOptions(int IntervalSeconds, int RetentionDays);
}
