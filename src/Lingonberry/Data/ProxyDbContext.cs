using Lingonberry.Models;
using Microsoft.EntityFrameworkCore;

namespace Lingonberry.Data;

public class ProxyDbContext(DbContextOptions<ProxyDbContext> options) : DbContext(options)
{
    public DbSet<App> Apps { get; set; }
    public DbSet<Device> Devices { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<App>()
            .HasIndex(a => a.Token)
            .IsUnique();

        modelBuilder.Entity<App>()
            .HasIndex(a => a.Domain)
            .IsUnique();

        modelBuilder.Entity<Device>()
            .HasOne(d => d.App)
            .WithMany(a => a.Devices)
            .HasForeignKey(d => d.AppId);
    }
}
