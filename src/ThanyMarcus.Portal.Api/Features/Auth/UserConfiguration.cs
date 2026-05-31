using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Portal.Api.Features.Auth;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.GoogleSubject);
        builder.Property(u => u.Email);
        builder.Property(u => u.Username).IsRequired();
        builder.Property(u => u.Name).IsRequired();
        builder.Property(u => u.ProfilePictureUrl);
        builder.Property(u => u.SessionsInvalidatedAt);
        builder.Property(u => u.LastSeenAt).IsRequired();

        builder.Property(u => u.PassphraseArgon2Params).HasColumnType("jsonb");

        builder.Property(u => u.CreatedAt).IsRequired();
        builder.Property(u => u.UpdatedAt).IsRequired();

        // Postgres treats NULLs as distinct, so these stay unique while allowing many
        // null-google_sub / null-email passkey-only accounts to coexist.
        builder.HasIndex(u => u.GoogleSubject).IsUnique();
        builder.HasIndex(u => u.Email).IsUnique();
        // The case-insensitive unique index on LOWER(username) is a functional index that
        // the fluent API can't express; it is created via raw SQL in the migration.
    }
}
