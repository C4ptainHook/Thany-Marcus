namespace ThanyMarcus.Shared.ReleaseFeed;

public static class ReleaseStrategies
{
    public const string InPlace = "in-place";
    public const string BlueGreen = "blue-green";

    public static bool IsValid(string? value) => value is InPlace or BlueGreen;
}
