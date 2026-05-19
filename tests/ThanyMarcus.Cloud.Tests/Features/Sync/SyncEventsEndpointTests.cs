using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Sync;

[Collection(PostgresCollection.Name)]
public sealed class SyncEventsEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Connecting_without_auth_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/sync/events");
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        resp.StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Notify_is_translated_and_streamed_as_sse()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var rawToken = await SeedPluginTokenAsync(postgres);

        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/sync/events");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.IsSuccessStatusCode.ShouldBeTrue();
        resp.Content.Headers.ContentType?.MediaType.ShouldBe("text/event-stream");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Wait briefly for LISTEN to be active on the server side.
        await Task.Delay(200, ct);

        var noteId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        await FireNotifyAsync(postgres, noteId, jobId, ct);

        var read = ReadUntilDataLineAsync(reader, ct);
        var winner = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(8), ct));
        winner.ShouldBe(read);
        var dataLine = await read;
        dataLine.ShouldNotBeNull();
        dataLine!.ShouldContain(noteId.ToString());
        dataLine.ShouldContain(jobId.ToString());
    }

    private static async Task<string?> ReadUntilDataLineAsync(StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) return null;
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                return line["data:".Length..].TrimStart();
            }
        }
        return null;
    }

    private static async Task FireNotifyAsync(PostgresFixture postgres, Guid noteId, Guid jobId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(postgres.ConnectionString);
        await conn.OpenAsync(ct);
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = IngestEventKinds.NotePhaseChanged,
            noteId,
            jobId,
            from = "queued",
            to = "extracting_attachments",
        });
        await using var cmd = new NpgsqlCommand("SELECT pg_notify(@chan, @payload)", conn);
        cmd.Parameters.AddWithValue("chan", IngestEventKinds.Channel);
        cmd.Parameters.AddWithValue("payload", json);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> SeedPluginTokenAsync(PostgresFixture postgres)
    {
        var raw = "sse-test-" + Guid.NewGuid().ToString("N");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var opts = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(postgres.ConnectionString, npg => npg.UseNodaTime().UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new CloudDbContext(opts);
        db.PluginTokens.Add(new PluginToken
        {
            TokenHash = hash,
            Label = "test",
            CreatedAt = Instant.FromUtc(2026, 5, 18, 0, 0),
        });
        await db.SaveChangesAsync();
        return raw;
    }
}
