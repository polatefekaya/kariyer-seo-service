using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Kariyer.Seo.Worker.Common.Persistence.Configurations;

/// <summary>
/// Maps the read-only projection over the Node application's <c>company_job</c>.
///
/// Keyless and mapped with <c>ToView(null)</c>, exactly as the freshness service does it, so
/// EF cannot track an instance and no <c>SaveChanges</c> can ever reach a table this service
/// does not own. It also keeps the table out of our migrations: without this, EF would see a
/// mapped entity in the default schema and generate DDL to CREATE <c>company_job</c> inside
/// <c>seo</c> — a second, empty table with the right name and none of the data.
/// </summary>
public sealed class CompanyJobReadModelConfiguration : IEntityTypeConfiguration<CompanyJobReadModel>
{
    public void Configure(EntityTypeBuilder<CompanyJobReadModel> builder)
    {
        builder.HasNoKey();
        builder.ToView(null);

        builder.Property(j => j.Uid).HasColumnName("uid");
        builder.Property(j => j.SlugUrl).HasColumnName("slug_url");
        builder.Property(j => j.Status).HasColumnName("status");
        builder.Property(j => j.IsActive).HasColumnName("is_active");
        builder.Property(j => j.IsDeleted).HasColumnName("is_deleted");
        builder.Property(j => j.ModifiedOn).HasColumnName("modified_on");

        builder.Property(j => j.Province).HasColumnName("province");
        builder.Property(j => j.Town).HasColumnName("town");
        builder.Property(j => j.Department).HasColumnName("department");
        builder.Property(j => j.Position).HasColumnName("position");

        // text[] columns. Mapped as string[] so the facet aggregate can group on them
        // directly in SQL rather than pulling every row back to unnest them in memory.
        builder.Property(j => j.WorkingType).HasColumnName("working_type");
        builder.Property(j => j.WorkingPrefs).HasColumnName("working_prefs");
    }
}
