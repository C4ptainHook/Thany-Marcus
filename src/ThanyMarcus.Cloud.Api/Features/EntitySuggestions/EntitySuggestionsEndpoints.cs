using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Shared.PluginApi;
using IClock = NodaTime.IClock;

namespace ThanyMarcus.Cloud.Api.Features.EntitySuggestions;

public static class EntitySuggestionsEndpoints
{
    public static void MapEntitySuggestionsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/entity-suggestions").AddEndpointFilter<RequirePluginAuthFilter>();

        group.MapGet("", ListAsync)
             .WithName("ListEntitySuggestions")
             .Produces<ListEntitySuggestionsResponse>(StatusCodes.Status200OK)
             .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("/{id:guid}/accept", AcceptAsync)
             .WithName("AcceptEntitySuggestion")
             .Produces<AcceptEntitySuggestionResponse>(StatusCodes.Status200OK)
             .Produces<EntitySuggestionPathConflict>(StatusCodes.Status409Conflict)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/dismiss", DismissAsync)
             .WithName("DismissEntitySuggestion")
             .Produces(StatusCodes.Status204NoContent)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/edit", EditAsync)
             .WithName("EditEntitySuggestion")
             .Produces(StatusCodes.Status204NoContent)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status404NotFound)
             .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> ListAsync(
        EntitySuggestionRepository repo,
        IOptionsMonitor<LlmIntelligenceOptions> opts,
        CancellationToken ct)
    {
        var o = opts.CurrentValue.EntitySuggestions;
        var items = await repo.ListSurfaceableAsync(
            o.OccurrenceThreshold, o.DistinctNoteThreshold, EntitySuggestionRepository.ListLimit, ct);
        return Results.Ok(new ListEntitySuggestionsResponse(items));
    }

    private static async Task<IResult> AcceptAsync(
        Guid id,
        CloudDbContext db,
        EntityStubWriter stubWriter,
        IEmbeddingClient embeddings,
        IClock clock,
        CancellationToken ct)
    {
        var s = await db.EntitySuggestions.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (s is null || s.DismissedAt is not null) return Results.NotFound();

        // Idempotent: a second accept returns the entity created the first time.
        if (s.AcceptedAt is not null && s.AcceptedEntityId is { } existingId)
        {
            return Results.Ok(new AcceptEntitySuggestionResponse(existingId));
        }

        var now = clock.GetCurrentInstant();
        var embedding = s.Embedding ?? new Vector(await embeddings.EmbedAsync(s.CanonicalText, ct));
        var entity = new Entity
        {
            Id = Guid.CreateVersion7(),
            Kind = s.Kind,
            CanonicalName = s.CanonicalText,
            Aliases = s.Aliases.ToArray(),
            Source = EntitySource.User,
            Embedding = embedding,
            MentionCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Entities.Add(entity);

        try
        {
            await stubWriter.CreateAsync(entity, s.Aliases, ct);
        }
        catch (StubPathConflictException ex)
        {
            return Results.Json(
                new EntitySuggestionPathConflict("path", ex.ExistingNoteId, ex.ExistingKind),
                statusCode: StatusCodes.Status409Conflict);
        }

        s.AcceptedAt = now;
        s.AcceptedEntityId = entity.Id;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new AcceptEntitySuggestionResponse(entity.Id));
    }

    private static async Task<IResult> DismissAsync(
        Guid id,
        CloudDbContext db,
        IClock clock,
        CancellationToken ct)
    {
        var s = await db.EntitySuggestions.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return Results.NotFound();

        if (s.DismissedAt is null && s.AcceptedAt is null)
        {
            s.DismissedAt = clock.GetCurrentInstant();
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> EditAsync(
        Guid id,
        EditEntitySuggestionRequest req,
        CloudDbContext db,
        EntityStubWriter stubWriter,
        IClock clock,
        CancellationToken ct)
    {
        var s = await db.EntitySuggestions.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (s is null || s.DismissedAt is not null) return Results.NotFound();

        var now = clock.GetCurrentInstant();

        if (req.CanonicalText is not null)
        {
            var canonical = req.CanonicalText.Trim();
            if (canonical.Length == 0)
            {
                return Results.Problem("canonicalText cannot be empty", statusCode: StatusCodes.Status400BadRequest);
            }
            // Renaming a canonical after Accept means moving the stub file, which breaks every
            // existing [[X]] link in Obsidian. Refuse; the user renames in Obsidian, then re-syncs.
            if (s.AcceptedAt is not null &&
                !string.Equals(canonical, s.CanonicalText, StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(
                    new EntitySuggestionPathConflict("rename", Guid.Empty, "entity_stub"),
                    statusCode: StatusCodes.Status409Conflict);
            }
            s.CanonicalText = canonical;
        }

        if (req.Aliases is not null)
        {
            s.Aliases = req.Aliases
                .Select(a => a?.Trim() ?? string.Empty)
                .Where(a => a.Length > 0 && !string.Equals(a, s.CanonicalText, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (s.AcceptedAt is not null && s.AcceptedEntityId is { } eid)
        {
            var entity = await db.Entities.SingleOrDefaultAsync(e => e.Id == eid && e.DeletedAt == null, ct);
            if (entity is not null)
            {
                entity.Aliases = s.Aliases.ToArray();
                entity.UpdatedAt = now;
                await stubWriter.UpdateAliasesAsync(entity, s.Aliases, ct);
            }
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            return Results.Problem(
                $"a suggestion named '{s.CanonicalText}' already exists for kind '{s.Kind}'",
                statusCode: StatusCodes.Status409Conflict);
        }
        return Results.NoContent();
    }
}
