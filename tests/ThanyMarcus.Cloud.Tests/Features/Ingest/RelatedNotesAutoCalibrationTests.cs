using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Tests.Features.Ingest;

public sealed class RelatedNotesAutoCalibrationTests
{
    private static readonly double[] PosLow = { 0.1, 0.2, 0.3 };
    private static readonly double[] NegHigh = { 0.4, 0.5, 0.6 };
    private static readonly double[] Half = { 0.5, 0.5 };
    private static readonly double[] One = { 0.5 };

    private static RelatedNotesOptions Opts() => new()
    {
        MaxDistance = 0.7,
        MaxDistanceFloor = 0.4,
        MaxDistanceCeiling = 0.9,
        AutoMinNotes = 40,
        AutoStaleNoteDelta = 25,
        AutoStaleEntityDelta = 15,
    };

    private static RelatedNotesOptions RescaledOpts() => new()
    {
        MaxDistance = 0.15,
        MaxDistanceFloor = 0.10,
        MaxDistanceCeiling = 0.28,
        QueryDocOffset = 0.04,
    };

    // ── ResolveEffectiveMaxDistance: override ?? auto ?? fallback, clamped ──

    [Fact]
    public void Resolve_prefers_request_override_over_auto_and_fallback()
    {
        RelatedNotesAutoCalibration.ResolveEffectiveMaxDistance(0.5, 0.62, Opts()).ShouldBe(0.5);
    }

    [Fact]
    public void Resolve_uses_auto_when_no_override()
    {
        RelatedNotesAutoCalibration.ResolveEffectiveMaxDistance(null, 0.62, Opts()).ShouldBe(0.62);
    }

    [Fact]
    public void Resolve_falls_back_to_configured_default_when_no_override_or_auto()
    {
        RelatedNotesAutoCalibration.ResolveEffectiveMaxDistance(null, null, Opts()).ShouldBe(0.7);
    }

    [Fact]
    public void Resolve_clamps_override_above_ceiling()
    {
        RelatedNotesAutoCalibration.ResolveEffectiveMaxDistance(2.0, null, Opts()).ShouldBe(0.9);
    }

    [Fact]
    public void Resolve_clamps_auto_below_floor()
    {
        RelatedNotesAutoCalibration.ResolveEffectiveMaxDistance(null, 0.1, Opts()).ShouldBe(0.4);
    }

    [Fact]
    public void Resolve_clamps_request_above_new_ceiling()
    {
        RelatedNotesAutoCalibration.ResolveEffectiveMaxDistance(0.7, null, RescaledOpts()).ShouldBe(0.28);
    }

    [Fact]
    public void Resolve_clamps_auto_below_new_floor()
    {
        RelatedNotesAutoCalibration.ResolveEffectiveMaxDistance(null, 0.05, RescaledOpts()).ShouldBe(0.10);
    }

    [Fact]
    public void Resolve_passes_dial_values_through_within_new_range()
    {
        var o = RescaledOpts();
        RelatedNotesAutoCalibration.ResolveEffectiveMaxDistance(0.12, null, o).ShouldBe(0.12);
        RelatedNotesAutoCalibration.ResolveEffectiveMaxDistance(0.19, null, o).ShouldBe(0.19);
    }

    [Fact]
    public void Resolve_passes_auto_within_new_range_through()
    {
        RelatedNotesAutoCalibration.ResolveEffectiveMaxDistance(null, 0.15, RescaledOpts()).ShouldBe(0.15);
    }

    [Fact]
    public void Default_threshold_excludes_false_positive_but_keeps_genuine_match()
    {
        var maxDistance = RelatedNotesAutoCalibration.ResolveEffectiveMaxDistance(null, null, RescaledOpts());
        maxDistance.ShouldBe(0.15);
        (0.20 <= maxDistance).ShouldBeFalse();
        (0.10 <= maxDistance).ShouldBeTrue();
    }

    // ── ShouldRecompute: gate + growth-staleness ──

    [Fact]
    public void ShouldRecompute_false_below_min_notes()
    {
        RelatedNotesAutoCalibration.ShouldRecompute(null, null, null, 10, 5, Opts()).ShouldBeFalse();
    }

    [Fact]
    public void ShouldRecompute_true_on_first_calibration_when_gate_open()
    {
        RelatedNotesAutoCalibration.ShouldRecompute(null, null, null, 50, 20, Opts()).ShouldBeTrue();
    }

    [Fact]
    public void ShouldRecompute_false_when_not_grown_enough()
    {
        RelatedNotesAutoCalibration.ShouldRecompute(0.7, 50, 20, 60, 25, Opts()).ShouldBeFalse();
    }

    [Fact]
    public void ShouldRecompute_true_when_notes_grew_past_delta()
    {
        RelatedNotesAutoCalibration.ShouldRecompute(0.7, 50, 20, 80, 20, Opts()).ShouldBeTrue();
    }

    [Fact]
    public void ShouldRecompute_true_when_entities_grew_past_delta()
    {
        RelatedNotesAutoCalibration.ShouldRecompute(0.7, 50, 20, 55, 40, Opts()).ShouldBeTrue();
    }

    [Fact]
    public void ShouldRecompute_false_when_prior_attempt_left_auto_null_and_no_growth()
    {
        // Previously sampled but graph too sparse (auto stayed null, counts recorded). Don't retry until growth.
        RelatedNotesAutoCalibration.ShouldRecompute(null, 50, 20, 60, 25, Opts()).ShouldBeFalse();
    }

    // ── ApplyHysteresis ──

    [Fact]
    public void Hysteresis_keeps_old_within_margin()
    {
        RelatedNotesAutoCalibration.ApplyHysteresis(0.70, 0.71, 0.03).ShouldBe(0.70);
    }

    [Fact]
    public void Hysteresis_takes_new_beyond_margin()
    {
        RelatedNotesAutoCalibration.ApplyHysteresis(0.70, 0.78, 0.03).ShouldBe(0.78);
    }

    [Fact]
    public void Hysteresis_takes_new_when_no_prior_value()
    {
        RelatedNotesAutoCalibration.ApplyHysteresis(null, 0.65, 0.03).ShouldBe(0.65);
    }

    // ── ROC AUC + Youden's J ──

    [Fact]
    public void Roc_perfectly_separable_gives_auc_one_and_tight_threshold()
    {
        var outcome = RelatedNotesAutoCalibration.RocAndYoudenThreshold(PosLow, NegHigh);
        outcome.Auc.ShouldBe(1.0, 1e-9);
        outcome.Threshold.ShouldBe(0.3, 1e-9);
    }

    [Fact]
    public void Roc_identical_distributions_gives_auc_half()
    {
        var outcome = RelatedNotesAutoCalibration.RocAndYoudenThreshold(Half, Half);
        outcome.Auc.ShouldBe(0.5, 1e-9);
    }

    [Fact]
    public void Roc_throws_when_a_class_is_empty()
    {
        Should.Throw<ArgumentException>(() =>
            RelatedNotesAutoCalibration.RocAndYoudenThreshold(Array.Empty<double>(), One));
    }
}
