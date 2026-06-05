namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public sealed record CalibrationOutcome(double Auc, double Threshold);

public static class RelatedNotesAutoCalibration
{
    public static double ResolveEffectiveMaxDistance(double? requested, double? auto, RelatedNotesOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        var chosen = requested ?? auto ?? o.MaxDistance;
        return Math.Clamp(chosen, o.MaxDistanceFloor, o.MaxDistanceCeiling);
    }

    public static bool ShouldRecompute(
        double? auto, int? lastNotes, int? lastEntities,
        int currentNotes, int currentEntities, RelatedNotesOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (currentNotes < o.AutoMinNotes) return false;
        if (lastNotes is null) return true;
        var grewNotes = currentNotes - lastNotes.Value >= o.AutoStaleNoteDelta;
        var grewEntities = currentEntities - (lastEntities ?? 0) >= o.AutoStaleEntityDelta;
        return grewNotes || grewEntities;
    }

    public static double ApplyHysteresis(double? oldValue, double newValue, double margin) =>
        oldValue is { } prev && Math.Abs(newValue - prev) < margin ? prev : newValue;

    // Related = smaller pgvector distance. AUC = P(a positive pair is closer than a negative pair),
    // ties counted as 0.5; the chosen threshold is the cutoff maximizing Youden's J (TPR - FPR).
    public static CalibrationOutcome RocAndYoudenThreshold(
        IReadOnlyList<double> positives, IReadOnlyList<double> negatives)
    {
        ArgumentNullException.ThrowIfNull(positives);
        ArgumentNullException.ThrowIfNull(negatives);
        if (positives.Count == 0 || negatives.Count == 0)
        {
            throw new ArgumentException("Both classes must be non-empty to calibrate.");
        }

        var sortedNeg = negatives.OrderBy(d => d).ToArray();
        double wins = 0;
        foreach (var p in positives)
        {
            var (lower, equal) = CountLowerAndEqual(sortedNeg, p);
            var greater = sortedNeg.Length - lower - equal;
            wins += greater + 0.5 * equal;
        }
        var auc = wins / ((double)positives.Count * negatives.Count);

        var sortedPos = positives.OrderBy(d => d).ToArray();
        var thresholds = sortedPos.Concat(sortedNeg).Distinct().OrderBy(d => d).ToArray();
        double bestJ = double.NegativeInfinity;
        var bestT = thresholds[0];
        foreach (var t in thresholds)
        {
            var tpr = CountLeq(sortedPos, t) / (double)sortedPos.Length;
            var fpr = CountLeq(sortedNeg, t) / (double)sortedNeg.Length;
            var j = tpr - fpr;
            if (j > bestJ)
            {
                bestJ = j;
                bestT = t;
            }
        }
        return new CalibrationOutcome(auc, bestT);
    }

    private static (int Lower, int Equal) CountLowerAndEqual(double[] sortedAsc, double value)
    {
        var lo = LowerBound(sortedAsc, value);
        var hi = UpperBound(sortedAsc, value);
        return (lo, hi - lo);
    }

    private static int CountLeq(double[] sortedAsc, double value) => UpperBound(sortedAsc, value);

    // First index with value >= target.
    private static int LowerBound(double[] sortedAsc, double target)
    {
        int lo = 0, hi = sortedAsc.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sortedAsc[mid] < target) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // First index with value > target (== count of elements <= target).
    private static int UpperBound(double[] sortedAsc, double target)
    {
        int lo = 0, hi = sortedAsc.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sortedAsc[mid] <= target) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
