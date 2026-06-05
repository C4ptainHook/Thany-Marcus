namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public sealed class RelatedNotesOptions
{
    public double MaxDistance { get; set; } = 0.15;
    public double MaxDistanceFloor { get; set; } = 0.10;
    public double MaxDistanceCeiling { get; set; } = 0.28;
    public double QueryDocOffset { get; set; } = 0.04;
    public int DefaultK { get; set; } = 5;
    public int MaxK { get; set; } = 20;
    public int MinBodyChars { get; set; } = 30;
    public int Fanout { get; set; } = 30;
    public double Lambda { get; set; } = 0.6;

    public bool AutoCalibrationEnabled { get; set; } = true;
    public int AutoMinNotes { get; set; } = 40;
    public int AutoMinPositivePairs { get; set; } = 30;
    public int AutoMinNegativePairs { get; set; } = 30;
    public int AutoStaleNoteDelta { get; set; } = 25;
    public int AutoStaleEntityDelta { get; set; } = 15;
    public double AutoHysteresisMargin { get; set; } = 0.03;
    public double AutoMinAuc { get; set; } = 0.6;
    public int AutoRandomSampleSize { get; set; } = 2000;
    public int AutoCheckCooldownSeconds { get; set; } = 300;
}
