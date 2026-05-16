using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.CloudManagement.ProviderTokens;

public sealed class ProviderTokenVaultTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    private async Task<User> InsertUserAsync(string suffix = "")
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = $"sub-{Guid.NewGuid()}{suffix}",
            Email = $"alice{suffix}@example.com",
            Name = "Alice",
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return user;
    }

    private ProviderTokenVault NewVault() => new(Db, Clock);

    private static byte[] RandomDek() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public async Task AddAsync_round_trip_decrypts_to_original_plaintext()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var vault = NewVault();
        var dek = RandomDek();
        const string plaintext = "dop_v1_round_trip_secret_xyz";

        await vault.AddAsync(user.Id, KnownProviders.DigitalOcean, plaintext, dek, ct);

        var row = await Db.EncryptedProviderTokens
            .AsNoTracking()
            .SingleAsync(t => t.UserId == user.Id && t.Provider == KnownProviders.DigitalOcean, ct);
        row.Ciphertext.ShouldNotBeEmpty();
        row.Nonce.Length.ShouldBe(12);
        row.Tag.Length.ShouldBe(16);
        Encoding.UTF8.GetString(row.Ciphertext).ShouldNotBe(plaintext);

        var decrypted = await vault.DecryptAsync(user.Id, KnownProviders.DigitalOcean, dek, ct);
        decrypted.ShouldNotBeNull();
        Encoding.UTF8.GetString(decrypted).ShouldBe(plaintext);
    }

    [Fact]
    public async Task AddAsync_throws_when_token_already_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var vault = NewVault();
        var dek = RandomDek();

        await vault.AddAsync(user.Id, KnownProviders.Azure, "first", dek, ct);
        Db.ChangeTracker.Clear();

        var ex = await Should.ThrowAsync<ProviderTokenAlreadyExistsException>(
            async () => await vault.AddAsync(user.Id, KnownProviders.Azure, "second", dek, ct));
        ex.UserId.ShouldBe(user.Id);
        ex.Provider.ShouldBe(KnownProviders.Azure);
    }

    [Fact]
    public async Task ReplaceAsync_inserts_when_no_row_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var vault = NewVault();
        var dek = RandomDek();

        await vault.ReplaceAsync(user.Id, KnownProviders.Cloudflare, "initial", dek, ct);

        var decrypted = await vault.DecryptAsync(user.Id, KnownProviders.Cloudflare, dek, ct);
        decrypted.ShouldNotBeNull();
        Encoding.UTF8.GetString(decrypted).ShouldBe("initial");
    }

    [Fact]
    public async Task ReplaceAsync_updates_existing_row_and_bumps_UpdatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var vault = NewVault();
        var dek = RandomDek();

        await vault.AddAsync(user.Id, KnownProviders.DigitalOcean, "first", dek, ct);
        Db.ChangeTracker.Clear();
        var before = await Db.EncryptedProviderTokens.AsNoTracking()
            .SingleAsync(t => t.UserId == user.Id && t.Provider == KnownProviders.DigitalOcean, ct);

        Clock.Advance(Duration.FromMinutes(5));
        await vault.ReplaceAsync(user.Id, KnownProviders.DigitalOcean, "rotated", dek, ct);
        Db.ChangeTracker.Clear();

        var after = await Db.EncryptedProviderTokens.AsNoTracking()
            .SingleAsync(t => t.UserId == user.Id && t.Provider == KnownProviders.DigitalOcean, ct);
        after.Id.ShouldBe(before.Id);
        after.Ciphertext.ShouldNotBe(before.Ciphertext);
        after.Nonce.ShouldNotBe(before.Nonce);
        after.Tag.ShouldNotBe(before.Tag);
        after.UpdatedAt.ShouldBeGreaterThan(before.UpdatedAt);
        after.CreatedAt.ShouldBe(before.CreatedAt);

        var decrypted = await vault.DecryptAsync(user.Id, KnownProviders.DigitalOcean, dek, ct);
        Encoding.UTF8.GetString(decrypted!).ShouldBe("rotated");
    }

    [Fact]
    public async Task DecryptAsync_returns_null_when_no_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var vault = NewVault();

        var result = await vault.DecryptAsync(user.Id, KnownProviders.Azure, RandomDek(), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task DecryptAsync_with_wrong_dek_throws_AuthenticationTagMismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var vault = NewVault();
        var realDek  = RandomDek();
        var wrongDek = RandomDek();

        await vault.AddAsync(user.Id, KnownProviders.Cloudflare, "secret", realDek, ct);

        await Should.ThrowAsync<AuthenticationTagMismatchException>(
            async () => await vault.DecryptAsync(user.Id, KnownProviders.Cloudflare, wrongDek, ct));
    }

    [Fact]
    public async Task RemoveAsync_deletes_row_and_subsequent_DecryptAsync_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var vault = NewVault();
        var dek = RandomDek();
        await vault.AddAsync(user.Id, KnownProviders.DigitalOcean, "secret", dek, ct);
        Db.ChangeTracker.Clear();

        await vault.RemoveAsync(user.Id, KnownProviders.DigitalOcean, ct);

        (await Db.EncryptedProviderTokens.AsNoTracking()
            .AnyAsync(t => t.UserId == user.Id && t.Provider == KnownProviders.DigitalOcean, ct))
            .ShouldBeFalse();
        (await vault.DecryptAsync(user.Id, KnownProviders.DigitalOcean, dek, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_returns_summaries_for_all_providers_without_crypto_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var vault = NewVault();
        var dek = RandomDek();

        await vault.AddAsync(user.Id, KnownProviders.DigitalOcean, "do-token", dek, ct);
        await vault.AddAsync(user.Id, KnownProviders.Cloudflare, "cf-token", dek, ct);

        var summaries = await vault.ListAsync(user.Id, ct);

        summaries.Count.ShouldBe(2);
        summaries.Select(s => s.Provider).ShouldBe([KnownProviders.Cloudflare, KnownProviders.DigitalOcean]);
        foreach (var s in summaries)
        {
            s.CreatedAt.ShouldBeGreaterThan(Instant.MinValue);
            s.UpdatedAt.ShouldBeGreaterThan(Instant.MinValue);
        }
    }

    [Fact]
    public async Task ListAsync_only_returns_rows_for_specified_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = await InsertUserAsync("a");
        var bob   = await InsertUserAsync("b");
        var vault = NewVault();
        var dek   = RandomDek();

        await vault.AddAsync(alice.Id, KnownProviders.DigitalOcean, "alice", dek, ct);
        await vault.AddAsync(bob.Id,   KnownProviders.DigitalOcean, "bob",   dek, ct);

        var aliceList = await vault.ListAsync(alice.Id, ct);

        aliceList.Count.ShouldBe(1);
        aliceList[0].Provider.ShouldBe(KnownProviders.DigitalOcean);
    }

    [Fact]
    public async Task Encrypt_uses_distinct_nonce_per_write()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var vault = NewVault();
        var dek = RandomDek();

        await vault.AddAsync(user.Id, KnownProviders.DigitalOcean, "same", dek, ct);
        Db.ChangeTracker.Clear();
        var first = await Db.EncryptedProviderTokens.AsNoTracking()
            .SingleAsync(t => t.UserId == user.Id, ct);

        await vault.ReplaceAsync(user.Id, KnownProviders.DigitalOcean, "same", dek, ct);
        Db.ChangeTracker.Clear();
        var second = await Db.EncryptedProviderTokens.AsNoTracking()
            .SingleAsync(t => t.UserId == user.Id, ct);

        second.Nonce.ShouldNotBe(first.Nonce);
        second.Ciphertext.ShouldNotBe(first.Ciphertext);
    }

    [Fact]
    public async Task Ciphertext_does_not_decode_to_plaintext_under_utf8()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var vault = NewVault();
        var dek = RandomDek();
        const string plaintext = "dop_v1_should_not_appear_anywhere";

        await vault.AddAsync(user.Id, KnownProviders.DigitalOcean, plaintext, dek, ct);

        var row = await Db.EncryptedProviderTokens.AsNoTracking()
            .SingleAsync(t => t.UserId == user.Id, ct);
        var asString = Encoding.UTF8.GetString(row.Ciphertext);
        asString.ShouldNotContain("dop_v1");
        row.Ciphertext.Length.ShouldBe(Encoding.UTF8.GetByteCount(plaintext));
    }
}
