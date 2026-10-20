using MetadataApi.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace MetadataApi.Persistence;

public sealed class MetadataDbContext(DbContextOptions<MetadataDbContext> options) : DbContext(options)
{
    public DbSet<Application> Applications => Set<Application>();
    public DbSet<Maintainer> Maintainers => Set<Maintainer>();
    public DbSet<ApplicationMaintainer> ApplicationMaintainers => Set<ApplicationMaintainer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var application = modelBuilder.Entity<Application>();
        application.HasKey(entity => entity.Id);
        application.Property(entity => entity.Id).ValueGeneratedNever();
        application.Property(entity => entity.Title).IsRequired().HasMaxLength(200);
        application.Property(entity => entity.Version).HasMaxLength(100);
        application.Property(entity => entity.Company).HasMaxLength(200);
        application.Property(entity => entity.Website).HasMaxLength(2048);
        application.Property(entity => entity.Source).HasMaxLength(2048);
        application.Property(entity => entity.License).HasMaxLength(100);
        application.Property(entity => entity.Description).HasMaxLength(4000);

        var maintainer = modelBuilder.Entity<Maintainer>();
        maintainer.HasKey(entity => entity.Id);
        maintainer.Property(entity => entity.Id).ValueGeneratedNever();
        maintainer.Property(entity => entity.Name).IsRequired().HasMaxLength(200);
        maintainer.Property(entity => entity.Email).IsRequired().HasMaxLength(254);

        var association = modelBuilder.Entity<ApplicationMaintainer>();
        association.HasKey(entity => new { entity.ApplicationId, entity.MaintainerId });
        association.HasOne<Application>().WithMany()
            .HasForeignKey(entity => entity.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        association.HasOne<Maintainer>().WithMany()
            .HasForeignKey(entity => entity.MaintainerId).OnDelete(DeleteBehavior.Cascade);
        association.HasIndex(entity => entity.MaintainerId);
    }
}
