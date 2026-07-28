using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kariyer.Seo.Worker.Common.Persistence.Configurations;

/// <summary>
/// Maps the read-only projection over <c>cms.seo_page</c>.
///
/// Keyless and <c>ToView(null)</c>, exactly like <see cref="CompanyJobReadModelConfiguration"/>
/// and for the same two reasons: EF cannot track an instance, so no <c>SaveChanges</c> can
/// reach a table this service does not own; and the entity stays out of our migrations, so we
/// never generate DDL that creates a second, empty <c>seo_page</c> inside the <c>seo</c> schema.
/// </summary>
public sealed class CmsPageReadModelConfiguration : IEntityTypeConfiguration<CmsPageReadModel>
{
    public void Configure(EntityTypeBuilder<CmsPageReadModel> builder)
    {
        builder.HasNoKey();
        builder.ToView(null);

        builder.Property(p => p.Path).HasColumnName("path");
        builder.Property(p => p.Status).HasColumnName("status");
        builder.Property(p => p.Noindex).HasColumnName("noindex");

        // Projected in SQL as `published_layout IS NOT NULL`; see SeoStore.
        builder.Property(p => p.HasPublishedLayout).HasColumnName("has_published_layout");

        builder.Property(p => p.PublishedAt).HasColumnName("published_at");
    }
}
