using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kariyer.Seo.Worker.Common.Persistence.Configurations;

/// <summary>Maps the rebuild audit log.</summary>
public sealed class SeoRebuildLogConfiguration : IEntityTypeConfiguration<SeoRebuildLog>
{
    public void Configure(EntityTypeBuilder<SeoRebuildLog> builder)
    {
        builder.ToTable("seo_rebuild_log");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(l => l.File).HasColumnName("file").HasMaxLength(128).IsRequired();
        builder.Property(l => l.SitemapType).HasColumnName("sitemap_type").HasMaxLength(32).IsRequired();
        builder.Property(l => l.UrlCount).HasColumnName("url_count");
        builder.Property(l => l.Checksum).HasColumnName("checksum").HasMaxLength(64).IsRequired();
        builder.Property(l => l.UncompressedBytes).HasColumnName("uncompressed_bytes");
        builder.Property(l => l.Uploaded).HasColumnName("uploaded");
        builder.Property(l => l.GeneratedAt).HasColumnName("generated_at").IsRequired();

        // The short-circuit's query: newest row for one file. Descending on generated_at so
        // "the last checksum for sitemap-jobs-1.xml" is the index's first entry rather than
        // a sort over every rebuild this service has ever done.
        builder.HasIndex(l => new { l.File, l.GeneratedAt })
            .HasDatabaseName("ix_seo_rebuild_log_file_generated_at")
            .IsDescending(false, true);
    }
}
