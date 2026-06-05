using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Api.Features.EntitySuggestions;

public sealed class EntitySuggestionRepository
{
    public const int ListLimit = 50;

    private readonly CloudDbContext db;

    public EntitySuggestionRepository(CloudDbContext db) => this.db = db;

    // Surfaceable = above the occurrence/distinct-note thresholds, neither accepted nor dismissed.
    // "Surfaceable" is a query-time predicate, not a status column — the row exists from first
    // observation but is only returned once it crosses the threshold.
    public async Task<List<EntitySuggestionDto>> ListSurfaceableAsync(
        int occurrenceThreshold, int distinctThreshold, int top, CancellationToken ct)
    {
        var rows = await db.EntitySuggestions
            .AsNoTracking()
            .Where(s => s.AcceptedAt == null
                     && s.DismissedAt == null
                     && s.OccurrenceCount >= occurrenceThreshold
                     && s.DistinctNoteCount >= distinctThreshold)
            .OrderByDescending(s => s.OccurrenceCount)
            .ThenByDescending(s => s.LastSeenAt)
            .Take(top)
            .Select(s => new
            {
                Suggestion = s,
                MergeTarget = db.Entities
                    .Where(e => e.Id == s.SuggestedMergeEntityId && e.DeletedAt == null)
                    .Select(e => new { e.Id, e.DisplayName, e.CanonicalName, e.Kind })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return rows.Select(r => ToDto(r.Suggestion, r.MergeTarget is null
            ? null
            : new EntitySuggestionMergeProposalDto(
                r.MergeTarget.Id,
                r.MergeTarget.DisplayName ?? r.MergeTarget.CanonicalName,
                r.MergeTarget.Kind,
                r.Suggestion.SuggestedMergeDistance ?? 0))).ToList();
    }

    public static EntitySuggestionDto ToDto(EntitySuggestion s, EntitySuggestionMergeProposalDto? mergeProposal = null)
    {
        var occurrences = EntitySuggestionOccurrences.Parse(s.Occurrences);
        var sample = occurrences.Count > 0 ? occurrences[^1] : null;
        return new EntitySuggestionDto(
            Id: s.Id,
            CanonicalText: s.CanonicalText,
            Kind: s.Kind,
            Aliases: s.Aliases,
            OccurrenceCount: s.OccurrenceCount,
            DistinctNoteCount: s.DistinctNoteCount,
            FirstSeenAt: s.FirstSeenAt.ToDateTimeOffset(),
            LastSeenAt: s.LastSeenAt.ToDateTimeOffset(),
            SampleOccurrence: sample is null
                ? null
                : new EntitySuggestionOccurrenceDto(sample.NoteId, sample.AnchorText, sample.SurroundingText),
            SuggestedMerge: mergeProposal);
    }
}
