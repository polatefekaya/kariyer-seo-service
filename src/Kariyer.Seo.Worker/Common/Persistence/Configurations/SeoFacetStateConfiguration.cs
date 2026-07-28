using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kariyer.Seo.Worker.Common.Persistence.Configurations;

/// <summary>Maps the last computed facet indexability.</summary>
public sealed class SeoFacetStateConfiguration : IEntityTypeConfiguration<SeoFacetState>
{
    public void Configure(EntityTypeBuilder<SeoFacetState> builder)
    {
        builder.ToTable("seo_facet_state");

        builder.HasKey(f => f.FacetPath);

        // Generous, because facet paths are Turkish slugs of a city plus a role and the
        // manifest is generated, not hand-written — a truncating column here would silently
        // collide two different facets onto one primary key.
        builder.Property(f => f.FacetPath).HasColumnName("facet_path").HasMaxLength(512).IsRequired();

        builder.Property(f => f.Axes).HasColumnName("axes");
        builder.Property(f => f.JobCount).HasColumnName("job_count");
        builder.Property(f => f.Indexable).HasColumnName("indexable");
        builder.Property(f => f.LastModified).HasColumnName("last_modified").IsRequired();

        // The projection's query: indexable facets only, in path order. Partial, because the
        // indexable set is the minority — most of the ~3,000 candidates never clear their
        // threshold, which is exactly what the count gate is for.
        builder.HasIndex(f => f.Indexable)
            .HasDatabaseName("ix_seo_facet_state_indexable")
            .HasFilter("indexable = true");
    }
}
