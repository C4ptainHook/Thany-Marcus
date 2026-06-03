using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.EntitySuggestions;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Sync;

public enum TombstoneOutcome { Tombstoned, AlreadyTombstoned, NotFound }

public enum ReviveOutcome { Revived, AlreadyLive, NotFound }

// Soft-delete with a 14-day cheap-revive window. The expensive derived artifacts (extracted
// text, embedding, synthesized body) stay on the row so revive needs no re-ingest; only the
// mentions that feed entity counts / hub generation are cleaned, because a tombstoned note must
// stop contributing to any active set. Mentions are re-derived on reprocess, not on revive.
public sealed class NoteTombstoneService
{
    private readonly CloudDbContext db;
    private readonly IClock clock;

    public NoteTombstoneService(CloudDbContext db, IClock clock)
    {
        this.db = db;
        this.clock = clock;
    }

    public async Task<TombstoneOutcome> TombstoneAsync(Guid noteId, CancellationToken ct)
    {
        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null) return TombstoneOutcome.NotFound;
        if (note.DeletedAt is not null) return TombstoneOutcome.AlreadyTombstoned;

        var now = clock.GetCurrentInstant();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        note.DeletedAt = now;
        note.TransitionVersion += 1;
        note.UpdatedAt = now;

        await CleanMentionsAsync(noteId, now, ct);
        await CleanEntitySuggestionOccurrencesAsync(noteId, now, ct);
        await SuppressHubOnTombstoneAsync(note, now, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return TombstoneOutcome.Tombstoned;
    }

    public async Task<ReviveOutcome> ReviveAsync(Guid noteId, CancellationToken ct)
    {
        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null) return ReviveOutcome.NotFound;
        if (note.DeletedAt is null) return ReviveOutcome.AlreadyLive;

        var now = clock.GetCurrentInstant();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        note.DeletedAt = null;
        note.TransitionVersion += 1;
        note.UpdatedAt = now;

        if (note.IsHub && note.HubEntityId is { } entityId)
        {
            var entity = await db.Entities.FirstOrDefaultAsync(e => e.Id == entityId, ct);
            if (entity is not null)
            {
                entity.HubSuppressed = false;
                entity.HubNoteId = note.Id;
                entity.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ReviveOutcome.Revived;
    }

    private async Task CleanMentionsAsync(Guid noteId, Instant now, CancellationToken ct)
    {
        var perEntity = await db.Mentions
            .Where(m => m.NoteId == noteId)
            .GroupBy(m => m.EntityId)
            .Select(g => new { EntityId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        if (perEntity.Count == 0) return;

        await db.Mentions.Where(m => m.NoteId == noteId).ExecuteDeleteAsync(ct);

        foreach (var grp in perEntity)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE entities SET
                    mention_count = GREATEST(0, mention_count - {grp.Count}),
                    updated_at    = {now}
                  WHERE id = {grp.EntityId}
                """, ct);
        }
    }

    private async Task CleanEntitySuggestionOccurrencesAsync(Guid noteId, Instant now, CancellationToken ct)
    {
        var noteIdStr = noteId.ToString();
        var affectedIds = await db.Database
            .SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM entity_suggestions
                 WHERE occurrences @> jsonb_build_array(jsonb_build_object('note_id', {noteIdStr}))
                """)
            .ToListAsync(ct);
        if (affectedIds.Count == 0) return;

        var suggestions = await db.EntitySuggestions
            .Where(s => affectedIds.Contains(s.Id))
            .ToListAsync(ct);
        foreach (var s in suggestions)
        {
            var kept = EntitySuggestionOccurrences.Parse(s.Occurrences)
                .Where(o => o.NoteId != noteId)
                .ToList();
            if (kept.Count == 0)
            {
                db.EntitySuggestions.Remove(s);
                continue;
            }
            s.Occurrences = EntitySuggestionOccurrences.Serialize(kept);
            s.OccurrenceCount = kept.Count;
            s.DistinctNoteCount = kept.Select(o => o.NoteId).Distinct().Count();
            s.LastSeenAt = now;
        }
    }

    private async Task SuppressHubOnTombstoneAsync(Note note, Instant now, CancellationToken ct)
    {
        if (!note.IsHub || note.HubEntityId is not { } entityId) return;
        var entity = await db.Entities.FirstOrDefaultAsync(e => e.Id == entityId, ct);
        if (entity is null) return;
        entity.HubSuppressed = true;
        if (entity.HubNoteId == note.Id) entity.HubNoteId = null;
        entity.UpdatedAt = now;
    }
}
