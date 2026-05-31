using System.Reflection;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public static class CommonPasswords
{
    private static IReadOnlyCollection<string>? cache;

    public static IReadOnlyCollection<string> Load() =>
        cache ??= EmbeddedLines.Read(typeof(CommonPasswords).Assembly, "common-passwords.txt");
}

internal static class EmbeddedLines
{
    public static string[] Read(Assembly assembly, string resourceSuffix)
    {
        var name = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(resourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);

        var lines = new List<string>(capacity: 10_000);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) lines.Add(trimmed);
        }
        return [.. lines];
    }
}
