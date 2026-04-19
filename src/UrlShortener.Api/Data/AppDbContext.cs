using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Models;

namespace UrlShortener.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ShortCode> ShortCodes => Set<ShortCode>();
    public DbSet<Click> Clicks => Set<Click>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShortCode>(entity =>
        {
            entity.ToTable("short_codes");
            entity.HasIndex(s => s.Code).IsUnique();
            entity.Property(s => s.Code).HasMaxLength(10).IsRequired();
            entity.Property(s => s.LongUrl).IsRequired();
            entity.Property(s => s.CreatedByIp).HasMaxLength(45);
        });

        modelBuilder.Entity<Click>(entity =>
        {
            entity.ToTable("clicks");
            entity.HasIndex(c => c.Code);
            entity.Property(c => c.Code).HasMaxLength(10).IsRequired();
            entity.Property(c => c.Ip).HasMaxLength(45);
        });
    }
}
