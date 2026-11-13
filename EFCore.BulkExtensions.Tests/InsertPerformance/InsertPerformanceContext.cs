using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NetTopologySuite.Geometries;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EFCore.BulkExtensions.Tests.InsertPerformance;

/// <summary>
/// SQL Server model for the id-only merge output, the streaming bulk copy reader and InsertFromQuery.
/// </summary>
public class InsertPerformanceContext : DbContext
{
    public InsertPerformanceContext(DbContextOptions options) : base(options)
    {
    }

    public DbSet<OutputItem> OutputItems { get; set; } = null!;
    public DbSet<OutputVersionedItem> OutputVersionedItems { get; set; } = null!;
    public DbSet<OutputCompositeItem> OutputCompositeItems { get; set; } = null!;
    public DbSet<OutputGuidItem> OutputGuidItems { get; set; } = null!;

    public DbSet<ReaderParent> ReaderParents { get; set; } = null!;
    public DbSet<ReaderWideItem> ReaderWideItems { get; set; } = null!;
    public DbSet<ReaderPerson> ReaderPeople { get; set; } = null!;
    public DbSet<ReaderStudent> ReaderStudents { get; set; } = null!;
    public DbSet<ReaderTeacher> ReaderTeachers { get; set; } = null!;
    public DbSet<ReaderLazyItem> ReaderLazyItems { get; set; } = null!;
    public DbSet<ReaderFloatItem> ReaderFloatItems { get; set; } = null!;

    public DbSet<QueryDetail> QueryDetails { get; set; } = null!;
    public DbSet<QueryTotal> QueryTotals { get; set; } = null!;
    public DbSet<QueryTotalAudit> QueryTotalAudits { get; set; } = null!;
    public DbSet<QueryTotalSource> QueryTotalSources { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutputCompositeItem>().HasKey(a => new { a.KeyA, a.KeyB });
        modelBuilder.Entity<OutputGuidItem>().Property(a => a.Id).HasDefaultValueSql("NEWSEQUENTIALID()");

        modelBuilder.Entity<ReaderWideItem>(b =>
        {
            b.Property(a => a.Rounded).HasColumnType("datetime2(3)");
            b.Property(a => a.Ansi).HasColumnType("varchar(50)");
            b.Property(a => a.Money).HasColumnType("decimal(18,4)");
            b.Property(a => a.StatusText).HasConversion<string>().HasMaxLength(20);
            b.Property(a => a.Code).HasConversion(a => ReaderCode.ToProvider(a), a => new ReaderCode(a)).HasMaxLength(30);
            b.Property(a => a.CodeOrNull).HasConversion(new ValueConverter<ReaderCode, string>(a => ReaderCode.ToProvider(a), a => new ReaderCode(a))).HasMaxLength(30);
            b.Property(a => a.WithDefault).HasDefaultValue(42);
            b.Property(a => a.Geography).HasColumnType("geography");
            b.Property(a => a.Geometry).HasColumnType("geometry");
            b.Property(a => a.Hierarchy).HasColumnType("hierarchyid");
            b.Property<string>(ReaderWideItem.NoteShadowProperty).HasMaxLength(100);
            b.HasOne(a => a.Parent).WithMany().HasForeignKey(ReaderWideItem.ParentIdShadowProperty).IsRequired(false);
            b.OwnsOne(a => a.Address, o =>
            {
                o.Property(a => a.Kind).HasConversion<string>().HasMaxLength(20);
                o.Property(a => a.Location).HasColumnType("geography");
            });
        });

        modelBuilder.Entity<ReaderPerson>().HasDiscriminator<string>("Discriminator");
        modelBuilder.Entity<ReaderLazyItem>().HasOne(a => a.Parent).WithMany().HasForeignKey("ParentId").IsRequired(false);

        modelBuilder.Entity<QueryTotal>().Property(a => a.Note).HasDefaultValue("none");
        modelBuilder.Entity<QueryDetail>().Property(a => a.Price).HasConversion(a => (long)(a * 100), a => a / 100m); // stored as cents
        modelBuilder.Entity<QueryTotalSource>().HasOne<QueryTotal>().WithMany().HasForeignKey(a => a.TotalId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<QueryTotalSource>().Property(a => a.Kind).HasConversion<string>().HasMaxLength(20).HasDefaultValue(ReaderStatus.None);
    }
}

public class OutputItem
{
    public int Id { get; set; }

    [MaxLength(50)]
    public string Name { get; set; } = "";

    public int Value { get; set; }

    [MaxLength(200)]
    public string? Description { get; set; }
}

public class OutputVersionedItem
{
    public int Id { get; set; }

    [MaxLength(50)]
    public string Name { get; set; } = "";

    public int Value { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = null!;
}

public class OutputCompositeItem
{
    public int KeyA { get; set; }

    public int KeyB { get; set; }

    [MaxLength(50)]
    public string Name { get; set; } = "";

    public int Value { get; set; }
}

public class OutputGuidItem
{
    public Guid Id { get; set; }

    [MaxLength(50)]
    public string Name { get; set; } = "";

    public int Value { get; set; }
}

public class ReaderParent
{
    public int Id { get; set; }

    [MaxLength(50)]
    public string Name { get; set; } = "";
}

public enum ReaderStatus : byte
{
    None = 0,
    Active = 1,
    Closed = 200,
}

public readonly record struct ReaderCode(string Value)
{
    public const string Failing = "fail";

    /// <summary>A value converter that throws for <see cref="Failing"/>, like a converter rejecting a bad value.</summary>
    public static string ToProvider(ReaderCode code) => code.Value == Failing ? throw new InvalidOperationException("Cannot convert " + Failing) : code.Value;
}

[Owned]
public class ReaderAddress
{
    [MaxLength(50)]
    public string? Street { get; set; }

    public ReaderStatus Kind { get; set; }

    public Point? Location { get; set; }
}

/// <summary>
/// One column of most kinds the bulk copy handles: value converters, rounding, spatial and hierarchy values, an owned type,
/// a shadow foreign key, a shadow property and a database default.
/// </summary>
public class ReaderWideItem
{
    public const string NoteShadowProperty = "Note";
    public const string ParentIdShadowProperty = "ParentId";

    public int Id { get; set; }

    [MaxLength(50)]
    public string Name { get; set; } = "";

    public string? Text { get; set; }
    public string Ansi { get; set; } = "";
    public bool Flag { get; set; }
    public byte Tiny { get; set; }
    public short Small { get; set; }
    public long Big { get; set; }
    public long? BigOrNull { get; set; }
    public decimal Money { get; set; }
    public Guid Key { get; set; }
    public Guid? KeyOrNull { get; set; }
    public DateTime When { get; set; }
    public DateTime? WhenOrNull { get; set; }
    public DateTime Rounded { get; set; }
    public DateTimeOffset Offset { get; set; }
    public TimeSpan Duration { get; set; }
    public DateOnly Day { get; set; }
    public TimeOnly Time { get; set; }
    public byte[]? Bytes { get; set; }
    public ReaderStatus Status { get; set; }
    public ReaderStatus? StatusOrNull { get; set; }
    public ReaderStatus StatusText { get; set; }
    public ReaderCode Code { get; set; }
    public ReaderCode? CodeOrNull { get; set; }
    public int WithDefault { get; set; }
    public Geometry? Geography { get; set; }
    public Geometry? Geometry { get; set; }
    public HierarchyId? Hierarchy { get; set; }
    public ReaderAddress? Address { get; set; }
    public ReaderParent? Parent { get; set; }

    [NotMapped]
    public string Unmapped => throw new InvalidOperationException("Not read by the bulk copy.");
}

/// <summary>
/// Lazy loading without proxies: EF Core injects the loader, and reading <see cref="Parent"/> can query the database.
/// </summary>
public class ReaderLazyItem
{
    private ReaderParent? _parent;

    public int Id { get; set; }

    [MaxLength(50)]
    public string Name { get; set; } = "";

    public ILazyLoader? LazyLoader { get; set; }

    public ReaderParent? Parent
    {
        get => LazyLoader?.Load(this, ref _parent) ?? _parent;
        set => _parent = value;
    }
}

public class ReaderFloatItem
{
    public int Id { get; set; }
    public double Real { get; set; }
    public float Single { get; set; }

    [Column(TypeName = "decimal(18,10)")]
    public double AsDecimal { get; set; }
}

public class ReaderPerson
{
    public int Id { get; set; }

    [MaxLength(50)]
    public string Name { get; set; } = "";
}

public class ReaderStudent : ReaderPerson
{
    [MaxLength(50)]
    public string? School { get; set; }
}

public class ReaderTeacher : ReaderPerson
{
    [MaxLength(50)]
    public string? Subject { get; set; }
}

public class QueryDetail
{
    public long Id { get; set; }
    public int TenantId { get; set; }

    [MaxLength(25)]
    public string AccountCode { get; set; } = "";

    [MaxLength(25)]
    public string DepartmentCode { get; set; } = "";

    [MaxLength(25)]
    public string? ProjectCode { get; set; }

    [MaxLength(255)]
    public string Description { get; set; } = "";

    public int Year { get; set; }
    public int Period { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public DateTime Updated { get; set; }
    public int UpdatedBy { get; set; }
    public ReaderStatus Status { get; set; }
    public decimal Price { get; set; }
}

public class QueryTotal
{
    public int Id { get; set; }
    public int TenantId { get; set; }

    [MaxLength(25)]
    public string AccountCode { get; set; } = "";

    [MaxLength(25)]
    public string DepartmentCode { get; set; } = "";

    [MaxLength(25)]
    public string? ProjectCode { get; set; }

    [MaxLength(100)]
    public string Description { get; set; } = "";

    public int Year { get; set; }
    public int Period { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public DateTime Updated { get; set; }
    public int UpdatedBy { get; set; }

    [MaxLength(25)]
    public string? Note { get; set; }

    public ReaderStatus Status { get; set; }
}

public class QueryTotalAudit
{
    public int Id { get; set; }
    public int TotalId { get; set; }
}

/// <summary>Which detail a total was copied from: the output of InsertFromQuery with an output table, with a foreign key to the total.</summary>
public class QueryTotalSource
{
    public int Id { get; set; }
    public int TotalId { get; set; }
    public long DetailId { get; set; }
    public DateTime? TotalUpdated { get; set; }

    [MaxLength(25)]
    public string? Note { get; set; }

    public ReaderStatus Kind { get; set; }
}
