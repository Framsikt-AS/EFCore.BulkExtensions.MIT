using Microsoft.EntityFrameworkCore;
using System;
using System.Diagnostics.CodeAnalysis;

namespace EFCore.BulkExtensions.Tests.ShadowProperties;

public class BrsDbContext : DbContext
{
    public BrsDbContext([NotNull] DbContextOptions options) : base(options)
    {
        Database.EnsureCreated();
    }

    public DbSet<BrsAnimal> Animals { get; set; } = null!;
    public DbSet<BrsDog> Dogs { get; set; } = null!;
    public DbSet<BrsCat> Cats { get; set; } = null!;
    public DbSet<BrsNote> Notes { get; set; } = null!;
    public DbSet<BrsStamp> Stamps { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<BrsAnimal>(cfg =>
        {
            cfg.ToTable("BrsAnimal");
            cfg.HasIndex(a => a.Name).IsUnique();
        });
        modelBuilder.Entity<BrsDog>();
        modelBuilder.Entity<BrsCat>();

        modelBuilder.Entity<BrsNote>(cfg =>
        {
            cfg.HasIndex(a => a.Title).IsUnique();
            cfg.Property<string>(BrsNote.Tag).HasMaxLength(50);
            cfg.Property<string>(BrsNote.Comment).HasColumnName(BrsNote.CommentColumn);
        });

        modelBuilder.Entity<BrsStamp>(cfg =>
        {
            cfg.HasIndex(a => a.Code).IsUnique();
            cfg.Property<DateTime>(BrsStamp.StampedAt);
        });
    }
}
