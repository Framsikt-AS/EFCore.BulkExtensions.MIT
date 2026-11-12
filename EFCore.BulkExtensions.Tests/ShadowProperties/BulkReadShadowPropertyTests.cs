using EFCore.BulkExtensions.SqlAdapters;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace EFCore.BulkExtensions.Tests.ShadowProperties;

// BulkRead of entities with a non-foreign-key shadow property: a TPH discriminator, plain shadow string properties
// and a non-string shadow property. BulkRead used to throw (KeyNotFoundException / ArgumentOutOfRangeException)
// while building the DataTable, because the first shadow string column was taken to be a discriminator and
// added to the copy even though BulkRead copies only the key / UpdateByProperties columns.
public class BulkReadShadowPropertyTests : IDisposable, IAssemblyFixture<DbAssemblyFixture>
{
    private const string DatabaseName = $"{nameof(EFCoreBulkTest)}_BulkReadShadow";

    private static BrsDbContext CreateContext() => new(ContextUtil.GetOptions<BrsDbContext>(databaseName: DatabaseName));

    [Theory]
    [InlineData(DbServerType.SQLServer)]
    public void BulkRead_TphEntity_ByKey(DbServerType dbServer)
    {
        ContextUtil.DbServer = dbServer;
        var (rex, fido, tom) = SeedAnimals();

        using var db = CreateContext();
        var entities = new List<BrsDog>
        {
            new() { Id = rex.Id },
            new() { Id = fido.Id },
            new() { Id = tom.Id }, // a cat's key: not a dog, so nothing to read
            new() { Id = int.MaxValue }, // no such row
        };

        db.BulkRead(entities);

        Assert.Equal("Rex", entities[0].Name);
        Assert.Equal("Labrador", entities[0].Breed);
        Assert.Equal("Fido", entities[1].Name);
        Assert.Equal("Beagle", entities[1].Breed);
        Assert.Null(entities[2].Name);
        Assert.Null(entities[2].Breed);
        Assert.Null(entities[3].Name);
    }

    [Theory]
    [InlineData(DbServerType.SQLServer)]
    public async Task BulkReadAsync_TphEntity_ByUpdateByProperties(DbServerType dbServer)
    {
        ContextUtil.DbServer = dbServer;
        var (rex, _, tom) = SeedAnimals();

        using var db = CreateContext();
        var entities = new List<BrsCat>
        {
            new() { Name = "Tom" },
            new() { Name = "Rex" }, // a dog's name: not a cat, so nothing to read
            new() { Name = "Nobody" },
        };

        await db.BulkReadAsync(entities, new BulkConfig { UpdateByProperties = new List<string> { nameof(BrsCat.Name) } });

        Assert.Equal(tom.Id, entities[0].Id);
        Assert.Equal(9, entities[0].Lives);
        Assert.NotEqual(rex.Id, entities[1].Id);
        Assert.Equal(0, entities[1].Id);
        Assert.Equal(0, entities[2].Id);
    }

    [Theory]
    [InlineData(DbServerType.SQLServer)]
    public void BulkRead_TphEntity_ByUpdateByPropertiesIncludingDiscriminator(DbServerType dbServer)
    {
        // The discriminator as one of the UpdateByProperties already worked (with ReplaceReadEntities, since the
        // shadow property has no CLR property to match the read entities on) and must keep working.
        ContextUtil.DbServer = dbServer;
        var (rex, fido, _) = SeedAnimals();

        using var db = CreateContext();
        var entities = new List<BrsDog> { new() { Name = "Rex" }, new() { Name = "Fido" }, new() { Name = "Tom" } };

        db.BulkRead(entities, new BulkConfig
        {
            UpdateByProperties = new List<string> { nameof(BrsDog.Name), "Discriminator" },
            ReplaceReadEntities = true,
        });

        var read = entities.ToDictionary(a => a.Name, a => (a.Id, a.Breed));
        Assert.Equal(2, read.Count);
        Assert.Equal((rex.Id, "Labrador"), read["Rex"]);
        Assert.Equal((fido.Id, "Beagle"), read["Fido"]);
    }

    [Theory]
    [InlineData(DbServerType.SQLServer)]
    public void BulkRead_TphEntityWithRenamedDiscriminatorColumn_ByUpdateByPropertiesIncludingDiscriminator(DbServerType dbServer)
    {
        // The discriminator property 'Kind' is mapped to the column 'shape_kind'. As one of the UpdateByProperties it has to be copied
        // (matched by column name), otherwise its temp-table column stays NULL and the join silently finds nothing.
        ContextUtil.DbServer = dbServer;
        BrsCircle circleA, circleB;
        using (var seed = CreateContext())
        {
            circleA = new BrsCircle { Name = "a", Radius = 1 };
            circleB = new BrsCircle { Name = "b", Radius = 3 };
            seed.Shapes.AddRange(circleA, new BrsSquare { Name = "a", Side = 2 }, circleB); // a square with the same Name as a circle
            seed.SaveChanges();
        }

        using var db = CreateContext();
        var entities = new List<BrsCircle> { new() { Name = "a" }, new() { Name = "b" }, new() { Name = "c" } };

        db.BulkRead(entities, new BulkConfig
        {
            UpdateByProperties = new List<string> { nameof(BrsCircle.Name), BrsShape.Kind },
            ReplaceReadEntities = true,
        });

        var read = entities.ToDictionary(a => a.Name, a => (a.Id, a.Radius));
        Assert.Equal(2, read.Count);
        Assert.Equal((circleA.Id, 1), read["a"]);
        Assert.Equal((circleB.Id, 3), read["b"]);
    }

    [Theory]
    [InlineData(DbServerType.SQLServer)]
    public void BulkRead_EntityWithShadowStringProperties_ByKey(DbServerType dbServer)
    {
        ContextUtil.DbServer = dbServer;
        var (first, second) = SeedNotes();

        using var db = CreateContext();
        var entities = new List<BrsNote> { new() { Id = second.Id }, new() { Id = first.Id }, new() { Id = int.MaxValue } };

        db.BulkRead(entities);

        Assert.Equal("Second", entities[0].Title);
        Assert.Equal("First", entities[1].Title);
        Assert.Null(entities[2].Title);
        AssertNoteShadowValuesUnchanged();
    }

    [Theory]
    [InlineData(DbServerType.SQLServer)]
    public async Task BulkReadAsync_EntityWithShadowStringProperties_ByUpdateByProperties(DbServerType dbServer)
    {
        ContextUtil.DbServer = dbServer;
        var (first, second) = SeedNotes();

        using var db = CreateContext();
        var entities = new List<BrsNote> { new() { Title = "Second" }, new() { Title = "First" }, new() { Title = "Third" } };

        await db.BulkReadAsync(entities, new BulkConfig { UpdateByProperties = new List<string> { nameof(BrsNote.Title) } });

        Assert.Equal(second.Id, entities[0].Id);
        Assert.Equal(first.Id, entities[1].Id);
        Assert.Equal(0, entities[2].Id);
        AssertNoteShadowValuesUnchanged();
    }

    [Theory]
    [InlineData(DbServerType.SQLServer)]
    public void BulkRead_EntityWithNonStringShadowProperty_ByKeyAndByUpdateByProperties(DbServerType dbServer)
    {
        ContextUtil.DbServer = dbServer;
        BrsStamp stamp;
        using (var seed = CreateContext())
        {
            stamp = new BrsStamp { Code = "A1" };
            seed.Stamps.Add(stamp);
            seed.Entry(stamp).Property(BrsStamp.StampedAt).CurrentValue = new DateTime(2026, 11, 12);
            seed.SaveChanges();
        }

        using var db = CreateContext();
        var byKey = new List<BrsStamp> { new() { Id = stamp.Id } };
        db.BulkRead(byKey);
        Assert.Equal("A1", byKey[0].Code);

        var byCode = new List<BrsStamp> { new() { Code = "A1" } };
        db.BulkRead(byCode, new BulkConfig { UpdateByProperties = new List<string> { nameof(BrsStamp.Code) } });
        Assert.Equal(stamp.Id, byCode[0].Id);
    }

    private static (BrsDog rex, BrsDog fido, BrsCat tom) SeedAnimals()
    {
        using var seed = CreateContext();
        var rex = new BrsDog { Name = "Rex", Breed = "Labrador" };
        var fido = new BrsDog { Name = "Fido", Breed = "Beagle" };
        var tom = new BrsCat { Name = "Tom", Lives = 9 };
        seed.AddRange(rex, tom, fido); // the cat in between, so the dogs' keys are not one contiguous range
        seed.SaveChanges();
        return (rex, fido, tom);
    }

    private static (BrsNote first, BrsNote second) SeedNotes()
    {
        using var seed = CreateContext();
        var first = new BrsNote { Title = "First" };
        var second = new BrsNote { Title = "Second" };
        seed.Notes.AddRange(first, second);
        seed.Entry(first).Property(BrsNote.Tag).CurrentValue = "tag-1";
        seed.Entry(first).Property(BrsNote.Comment).CurrentValue = "comment-1";
        seed.Entry(second).Property(BrsNote.Tag).CurrentValue = "tag-2";
        seed.Entry(second).Property(BrsNote.Comment).CurrentValue = "comment-2";
        seed.SaveChanges();
        return (first, second);
    }

    private static void AssertNoteShadowValuesUnchanged()
    {
        using var db = CreateContext();
        var rows = db.Notes
            .OrderBy(a => a.Title)
            .Select(a => new { a.Title, Tag = EF.Property<string>(a, BrsNote.Tag), Comment = EF.Property<string>(a, BrsNote.Comment) })
            .ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(("First", "tag-1", "comment-1"), (rows[0].Title, rows[0].Tag, rows[0].Comment));
        Assert.Equal(("Second", "tag-2", "comment-2"), (rows[1].Title, rows[1].Tag, rows[1].Comment));
    }

    public void Dispose()
    {
        using var db = CreateContext();
        db.Database.EnsureDeleted();
    }
}
