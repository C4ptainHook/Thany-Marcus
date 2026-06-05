using System.Buffers;
using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;

namespace ThanyMarcus.Cloud.Api.Features.EntitySuggestions;

// Materialises an accepted entity as an alias-stub markdown Note so Obsidian's native resolver
// folds drifting surface forms ([[Mike]], [[MJ]]) onto one graph node. The cloud owns the
// frontmatter; the user may keep their own body below the closing fence (preserved on alias edits).
// Add/mutate only — the caller owns SaveChanges (matches HubMaterializer).
public sealed class EntityStubWriter
{
    // Obsidian-illegal filename characters. Spaces and case are preserved so the file basename
    // equals the canonical name (so `[[Michael Jackson]]` resolves by basename, aliases handle drift).
    private static readonly SearchValues<char> IllegalChars =
        SearchValues.Create(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '#', '^', '[', ']', '\n', '\r', '\t']);

    private readonly CloudDbContext db;
    private readonly IOptionsMonitor<LlmIntelligenceOptions> opts;
    private readonly IClock clock;

    public EntityStubWriter(
        CloudDbContext db,
        IOptionsMonitor<LlmIntelligenceOptions> opts,
        IClock clock)
    {
        this.db = db;
        this.opts = opts;
        this.clock = clock;
    }

    public string ComputeStubRelativePath(string canonical)
    {
        var folder = opts.CurrentValue.EntitySuggestions.StubsFolder.Trim().Trim('/');
        if (folder.Length == 0) folder = "_Entities/Stubs";
        var name = SanitizeFilename(canonical);
        return $"{folder}/{name}.md";
    }

    public static string BuildStubMarkdown(string canonical, string kind, IReadOnlyList<string> aliases, Guid entityId) =>
        BuildFrontmatter(kind, aliases, entityId) + "\n";

    public async Task<Note> CreateAsync(Entity entity, IReadOnlyList<string> aliases, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(aliases);

        var path = ComputeStubRelativePath(entity.CanonicalName);
        var clash = await db.Notes
            .Where(n => n.RelativePath == path && n.DeletedAt == null)
            .Select(n => new { n.Id, n.Kind })
            .FirstOrDefaultAsync(ct);
        if (clash is not null)
        {
            throw new StubPathConflictException(path, clash.Id, clash.Kind);
        }

        var now = clock.GetCurrentInstant();
        var note = new Note
        {
            Id = Guid.CreateVersion7(),
            CapturedAt = now,
            Status = NoteStatus.Ready,
            Kind = NoteKind.EntityStub,
            BodyInput = string.Empty,
            BodyOutput = BuildStubMarkdown(entity.CanonicalName, entity.Kind, aliases, entity.Id),
            RelativePath = path,
            Tags = [],
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Notes.Add(note);
        entity.StubNoteId = note.Id;
        entity.UpdatedAt = now;
        return note;
    }

    // True when another live entity already owns this surface form (as its canonical name or an
    // alias) or a literal user note carries it as a basename — cases where Obsidian would resolve
    // the alias ambiguously. Cross-language aliasing widens this surface, so the guard is
    // load-bearing: an ambiguous alias resolves to an arbitrary target.
    public async Task<bool> IsAliasClaimedElsewhereAsync(Guid entityId, string alias, CancellationToken ct)
    {
        var trimmed = alias?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return false;

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
            opened = true;
        }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT EXISTS (
                    SELECT 1 FROM entities e
                     WHERE e.deleted_at IS NULL
                       AND e.id <> @id
                       AND (lower(e.canonical_name) = lower(@alias)
                            OR EXISTS (SELECT 1 FROM unnest(e.aliases) a WHERE lower(a) = lower(@alias)))
                    UNION ALL
                    SELECT 1 FROM notes n
                     WHERE n.deleted_at IS NULL
                       AND n.is_hub = false
                       AND n.kind <> 'entity_stub'
                       AND lower(regexp_replace(coalesce(n.relative_path, ''), '^.*/', '')) = lower(@alias) || '.md'
                )
                """;
            cmd.Parameters.AddWithValue("id", entityId);
            cmd.Parameters.AddWithValue("alias", trimmed);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is true;
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }

    public async Task<Note?> UpdateAliasesAsync(Entity entity, IReadOnlyList<string> newAliases, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(newAliases);

        if (entity.StubNoteId is not { } stubId) return null;
        var note = await db.Notes.SingleOrDefaultAsync(n => n.Id == stubId && n.DeletedAt == null, ct);
        if (note is null) return null;

        var body = ExtractBodyAfterFrontmatter(note.BodyOutput ?? string.Empty);
        var now = clock.GetCurrentInstant();
        note.BodyOutput = BuildFrontmatter(entity.Kind, newAliases, entity.Id) + body;
        note.UpdatedAt = now;
        return note;
    }

    internal static string BuildFrontmatter(string kind, IReadOnlyList<string> aliases, Guid entityId)
    {
        var clean = aliases
            .Select(a => a?.Trim() ?? string.Empty)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.Append("---\n");
        if (clean.Count == 0)
        {
            sb.Append("aliases: []\n");
        }
        else
        {
            sb.Append("aliases:\n");
            foreach (var a in clean)
            {
                sb.Append("  - ").Append(a).Append('\n');
            }
        }
        sb.Append("thany:entity_id: ").Append(entityId).Append('\n');
        sb.Append("thany:kind: ").Append(kind).Append('\n');
        sb.Append("---\n");
        return sb.ToString();
    }

    // Returns everything after the first frontmatter block's closing fence, or the whole content
    // when there is no recognisable frontmatter. Used to preserve user-authored body on alias edits.
    internal static string ExtractBodyAfterFrontmatter(string content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        var lines = content.Split('\n');
        if (lines[0].TrimEnd('\r') != "---") return content;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd('\r') == "---")
            {
                return string.Join('\n', lines.Skip(i + 1));
            }
        }
        return content;
    }

    internal static string SanitizeFilename(string canonical)
    {
        var trimmed = (canonical ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "entity";
        var sb = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            sb.Append(IllegalChars.Contains(c) ? ' ' : c);
        }
        var collapsed = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? "entity" : collapsed;
    }
}

public sealed class StubPathConflictException : Exception
{
    public string Path { get; }
    public Guid ExistingNoteId { get; }
    public string ExistingKind { get; }

    public StubPathConflictException(string path, Guid existingNoteId, string existingKind)
        : base($"vault path '{path}' is already occupied by note {existingNoteId} (kind={existingKind})")
    {
        Path = path;
        ExistingNoteId = existingNoteId;
        ExistingKind = existingKind;
    }
}
