using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Ingest;

[Collection(PostgresCollection.Name)]
public sealed class NoteDeleteEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Returns_204_and_tombstones_note()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);
        var noteId = await SeedNoteAsync(postgres);

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.DeleteAsync(new Uri($"/api/notes/{noteId}", UriKind.Relative), ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var probe = NewDb(postgres.ConnectionString);
        var note = await probe.Notes.SingleAsync(n => n.Id == noteId, ct);
        note.DeletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Returns_404_when_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.DeleteAsync(new Uri($"/api/notes/{Guid.NewGuid()}", UriKind.Relative), ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Idempotent_second_delete_returns_204()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var token = await SeedPluginTokenAsync(postgres);
        var noteId = await SeedNoteAsync(postgres);

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var first = await client.DeleteAsync(new Uri($"/api/notes/{noteId}", UriKind.Relative), ct);
        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var second = await client.DeleteAsync(new Uri($"/api/notes/{noteId}", UriKind.Relative), ct);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Without_auth_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var noteId = await SeedNoteAsync(postgres);

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();

        var resp = await client.DeleteAsync(new Uri($"/api/notes/{noteId}", UriKind.Relative), ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task<Guid> SeedNoteAsync(PostgresFixture postgres)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = NewDb(postgres.ConnectionString);
        var note = new Note
        {
            Id = Guid.CreateVersion7(),
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt = now,
            Status = NoteStatus.Ready,
            BodyInput = "body",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Notes.Add(note);
        await db.SaveChangesAsync();
        return note.Id;
    }

    private static async Task<string> SeedPluginTokenAsync(PostgresFixture postgres)
    {
        var raw = "del-" + Guid.NewGuid().ToString("N");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        using var db = NewDb(postgres.ConnectionString);
        db.PluginTokens.Add(new PluginToken
        {
            TokenHash = hash,
            Label = "test",
            CreatedAt = Instant.FromUtc(2026, 5, 18, 0, 0),
        });
        await db.SaveChangesAsync();
        return raw;
    }

    private static CloudDbContext NewDb(string connStr)
    {
        var opts = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(connStr, npg => npg.UseNodaTime().UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CloudDbContext(opts);
    }
}
