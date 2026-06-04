using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ThanyMarcus.Cloud.Api.Features.Folders;

public sealed class FolderConfiguration : IEntityTypeConfiguration<Folder>
{
    public void Configure(EntityTypeBuilder<Folder> builder)
    {
        builder.ToTable("folders");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Path).IsRequired();
        builder.Property(f => f.DeletedAt);
        builder.Property(f => f.CreatedAt).IsRequired();
        builder.Property(f => f.UpdatedAt).IsRequired();

        builder.HasIndex(f => f.Path)
            .IsUnique()
            .HasDatabaseName("ix_folders_path");
    }
}
