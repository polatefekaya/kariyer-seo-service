using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kariyer.Seo.Worker.Common.Persistence.Configurations;

/// <summary>Maps the incremental job-URL projection.</summary>
public sealed class SeoUrlStateConfiguration : IEntityTypeConfiguration<SeoUrlState>
{
    public void Configure(EntityTypeBuilder<SeoUrlState> builder)
    {
        builder.ToTable("seo_url_state");

        builder.HasKey(s => s.JobUid);

        builder.Property(s => s.JobUid).HasColumnName("job_uid").HasMaxLength(64).IsRequired();

        // Matches company_job.slug_url's own varchar(500). Shorter would silently truncate
        // a long slug into a URL that 404s; that a sitemap entry must be a working URL is
        // the one thing this table exists to guarantee.
        builder.Property(s => s.Slug).HasColumnName("slug").HasMaxLength(500).IsRequired();

        builder.Property(s => s.Status).HasColumnName("status").HasConversion<int>();
        builder.Property(s => s.LastModified).HasColumnName("last_modified");
        builder.Property(s => s.Dirty).HasColumnName("dirty");
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // The flush's only query: every live URL, in a stable order. Ordering by the primary
        // key would work but this covering index lets the projection be an index-only scan
        // and, more importantly, pins the ORDER BY — an unordered read would shuffle URLs
        // between chunk files on every run, changing every checksum and defeating the
        // conditional-write short-circuit entirely.
        builder.HasIndex(s => new { s.Status, s.JobUid })
            .HasDatabaseName("ix_seo_url_state_status_job_uid");

        // Partial: the dirty set is tiny and almost always empty, so a full index would be
        // mostly dead weight on a table with one row per job.
        builder.HasIndex(s => s.Dirty)
            .HasDatabaseName("ix_seo_url_state_dirty")
            .HasFilter("dirty = true");
    }
}
