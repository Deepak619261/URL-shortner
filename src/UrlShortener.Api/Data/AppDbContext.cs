using Microsoft.EntityFrameworkCore;
using UrlShortener.Api.Models;

namespace UrlShortener.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ShortCode> ShortCodes => Set<ShortCode>();
    public DbSet<Click> Clicks => Set<Click>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShortCode>(entity =>
        {
            entity.ToTable("short_codes");
            entity.HasIndex(s => s.Code).IsUnique();
            entity.HasIndex(s => s.UserId);  // index for "list all my codes" queries
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

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasIndex(u => u.Username).IsUnique();
            entity.HasIndex(u => u.Email).IsUnique();
            entity.Property(u => u.Username).HasMaxLength(50).IsRequired();
            entity.Property(u => u.Email).HasMaxLength(255).IsRequired();
            entity.Property(u => u.PasswordHash).IsRequired();
        });
    }
}
