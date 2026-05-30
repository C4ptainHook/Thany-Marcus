using System.Buffers;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Projects;

public static class ProjectsEndpoints
{
    private const int NameMinLength = 1;
    private const int NameMaxLength = 80;
    private const int DescriptionMaxLength = 500;
    private static readonly SearchValues<char> InvalidNameChars =
        SearchValues.Create(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\n', '\r', '\t']);

    public static void MapProjectsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects").AddEndpointFilter<RequirePluginAuthFilter>();

        group.MapGet("", ListAsync)
             .WithName("ListProjects")
             .Produces<ListProjectsResponse>(StatusCodes.Status200OK)
             .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("", CreateAsync)
             .WithName("CreateProject")
             .Produces<ProjectDto>(StatusCodes.Status201Created)
             .ProducesProblem(StatusCodes.Status400BadRequest)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", DeleteAsync)
             .WithName("DeleteProject")
             .Produces(StatusCodes.Status204NoContent)
             .ProducesProblem(StatusCodes.Status401Unauthorized)
             .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(CloudDbContext db, CancellationToken ct)
    {
        var items = await db.Entities
            .Where(e => e.Kind == EntityKind.Project && e.DeletedAt == null)
            .OrderBy(e => e.CanonicalName)
            .Select(e => new ProjectDto(e.Id, e.CanonicalName, e.Description, e.MentionCount))
            .ToListAsync(ct);
        return Results.Ok(new ListProjectsResponse(items));
    }

    private static async Task<IResult> CreateAsync(
        CreateProjectRequest req,
        CloudDbContext db,
        IClock clock,
        CancellationToken ct)
    {
        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length < NameMinLength || name.Length > NameMaxLength)
        {
            return Results.Problem(
                $"name must be {NameMinLength}–{NameMaxLength} characters",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (name.AsSpan().IndexOfAny(InvalidNameChars) >= 0)
        {
            return Results.Problem(
                "name cannot contain path-invalid characters: / \\ : * ? \" < > | or whitespace",
                statusCode: StatusCodes.Status400BadRequest);
        }
        var description = req.Description?.Trim();
        if (description is { Length: > DescriptionMaxLength })
        {
            return Results.Problem(
                $"description max {DescriptionMaxLength} characters",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var conflict = await db.Entities.AnyAsync(
            e => e.Kind == EntityKind.Project
              && e.DeletedAt == null
              && EF.Functions.ILike(e.CanonicalName, name),
            ct);
        if (conflict)
        {
            return Results.Problem(
                $"project named '{name}' already exists",
                statusCode: StatusCodes.Status409Conflict);
        }

        var now = clock.GetCurrentInstant();
        var entity = new Entities.Entity
        {
            Kind = EntityKind.Project,
            CanonicalName = name,
            Aliases = [],
            Description = string.IsNullOrEmpty(description) ? null : description,
            Source = EntitySource.User,
            VaultFolder = name,
            MentionCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Entities.Add(entity);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/projects/{entity.Id}",
            new ProjectDto(entity.Id, entity.CanonicalName, entity.Description, entity.MentionCount));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        CloudDbContext db,
        IClock clock,
        CancellationToken ct)
    {
        var entity = await db.Entities.SingleOrDefaultAsync(
            e => e.Id == id && e.Kind == EntityKind.Project && e.DeletedAt == null,
            ct);
        if (entity is null) return Results.NotFound();
        var now = clock.GetCurrentInstant();
        entity.DeletedAt = now;
        entity.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}

public sealed record CreateProjectRequest(string Name, string? Description);
public sealed record ProjectDto(Guid Id, string Name, string? Description, int MentionCount);
public sealed record ListProjectsResponse(IReadOnlyList<ProjectDto> Projects);
