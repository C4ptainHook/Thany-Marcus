namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public sealed class RelatedNotesOptions
{
    public double MaxDistance { get; set; } = 0.7;
    public int DefaultK { get; set; } = 5;
    public int MaxK { get; set; } = 20;
    public int ExcludeRecentHours { get; set; } = 1;
    public int MinBodyChars { get; set; } = 30;
}
