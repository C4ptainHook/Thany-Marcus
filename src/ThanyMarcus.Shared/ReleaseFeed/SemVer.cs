using System.Globalization;

namespace ThanyMarcus.Shared.ReleaseFeed;

public readonly record struct SemVer(int Major, int Minor, int Patch, string? PreRelease) : IComparable<SemVer>
{
    public static bool TryParse(string? value, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var s = value.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];

        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        string? pre = null;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0) return false;
        }

        var parts = s.Split('.');
        if (parts.Length != 3) return false;
        if (!TryCore(parts[0], out var major) || !TryCore(parts[1], out var minor) || !TryCore(parts[2], out var patch))
            return false;

        version = new SemVer(major, minor, patch, pre);
        return true;
    }

    private static bool TryCore(string part, out int value) =>
        int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string a, string b)
    {
        var aIds = a.Split('.');
        var bIds = b.Split('.');
        var shared = Math.Min(aIds.Length, bIds.Length);
        for (var i = 0; i < shared; i++)
        {
            var aNum = TryCore(aIds[i], out var an);
            var bNum = TryCore(bIds[i], out var bn);
            int c;
            if (aNum && bNum) c = an.CompareTo(bn);
            else if (aNum) c = -1;
            else if (bNum) c = 1;
            else c = string.CompareOrdinal(aIds[i], bIds[i]);
            if (c != 0) return c;
        }
        return aIds.Length.CompareTo(bIds.Length);
    }

    public static bool operator <(SemVer left, SemVer right) => left.CompareTo(right) < 0;
    public static bool operator <=(SemVer left, SemVer right) => left.CompareTo(right) <= 0;
    public static bool operator >(SemVer left, SemVer right) => left.CompareTo(right) > 0;
    public static bool operator >=(SemVer left, SemVer right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}
