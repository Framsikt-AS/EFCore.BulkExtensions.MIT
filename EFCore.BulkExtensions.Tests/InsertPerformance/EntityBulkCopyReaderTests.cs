using EFCore.BulkExtensions.SqlAdapters;
using EFCore.BulkExtensions.SqlAdapters.SqlServer;
using EFCore.BulkExtensions.Tests.BulkInsertOrUpdate;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace EFCore.BulkExtensions.Tests.InsertPerformance;

/// <summary>
/// The reader that streams entities to SqlBulkCopy must give it what the DataTable gives it. These tests build both for the same
/// entities and compare every value SqlBulkCopy would read, then insert with each and compare the rows in the database column by column.
/// </summary>
public class EntityBulkCopyReaderTests : IClassFixture<EntityBulkCopyReaderTests.DatabaseFixture>, IAssemblyFixture<DbAssemblyFixture>
{
    private readonly DatabaseFixture _dbFixture;

    public EntityBulkCopyReaderTests(DatabaseFixture dbFixture)
    {
        _dbFixture = dbFixture;
    }

    public class DatabaseFixture : BulkDbTestsFixture<InsertPerformanceContext>
    {
        protected override string DbName => nameof(EntityBulkCopyReaderTests);
    }

    /// <summary>Mirrors the library's internal <c>EntityBulkCopyReader.SourceMode</c>, which is set through reflection.</summary>
    public enum SourceMode
    {
        Auto,
        DataTable,
        Reader,
    }

    private const string ReaderTypeName = "EFCore.BulkExtensions.SqlAdapters.SqlServer.EntityBulkCopyReader";
    private static readonly System.Type ReaderSettingsType = typeof(SqlOperationsServerAdapter).Assembly.GetType(ReaderTypeName, throwOnError: true)!;
    private static readonly System.Type ReaderGenericType = typeof(SqlOperationsServerAdapter).Assembly.GetType(ReaderTypeName + "`1", throwOnError: true)!;

    public enum Scenario
    {
        Default,
        DateTime2PrecisionForceRound,
        EnableShadowProperties,
        ShadowPropertyValue,
        AllDefaultValues,
        PropertiesToInclude,
        PropertiesToExclude,
        SetOutputIdentity,
    }

    public static TheoryData<Scenario> Scenarios() => new(Enum.GetValues<Scenario>());

    // Leaving out NOT NULL columns fails the insert either way
    public static TheoryData<Scenario> InsertableScenarios() => new(Enum.GetValues<Scenario>().Where(a => a != Scenario.PropertiesToInclude));

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task WideEntity_ReaderGivesSqlBulkCopyTheSameValues(Scenario scenario)
    {
        using var db = await CreateContextAsync();
        var parents = await SeedParentsAsync(db);
        var items = CreateWideItems(parents, scenario);

        AssertSameValues(db, items, CreateConfig(scenario), OperationType.Insert);
    }

    [Theory]
    [MemberData(nameof(InsertableScenarios))]
    public async Task WideEntity_ReaderInsertsTheSameRows(Scenario scenario)
    {
        using var db = await CreateContextAsync();
        var parents = await SeedParentsAsync(db);

        var rows = new Dictionary<SourceMode, List<object?[]>>();
        foreach (var mode in new[] { SourceMode.DataTable, SourceMode.Reader })
        {
            await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE [ReaderWideItems]");
            var items = CreateWideItems(parents, scenario);
            var usedReader = await WithSourceModeAsync(mode, async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                if (scenario == Scenario.SetOutputIdentity)
                {
                    await db.BulkInsertOrUpdateAsync(items, CreateConfig(scenario));
                }
                else
                {
                    await db.BulkInsertAsync(items, CreateConfig(scenario));
                }

                await transaction.CommitAsync();
            });

            Assert.Equal(mode == SourceMode.Reader, Assert.Single(usedReader));
            rows[mode] = await ReadTableAsync(db, "ReaderWideItems");
            Assert.Equal(items.Count, rows[mode].Count);
            if (scenario == Scenario.SetOutputIdentity)
            {
                Assert.Equal(Enumerable.Range(1, items.Count), items.Select(a => a.Id));
            }
        }

        AssertSameRows(rows[SourceMode.DataTable], rows[SourceMode.Reader]);
    }

    [Fact]
    public async Task Discriminator_ReaderGivesTheSameValuesAndRows()
    {
        using var db = await CreateContextAsync();
        var students = Enumerable.Range(0, 20).Select(i => new ReaderStudent { Name = "student " + i, School = i % 3 == 0 ? null : "school " + i }).ToList();

        AssertSameValues(db, students, new BulkConfig(), OperationType.Insert);
        await AssertSameRowsAsync(db, "ReaderPeople", () => db.BulkInsertAsync(CloneStudents(students)));
    }

    [Fact]
    public async Task AbstractList_ReaderGivesTheSameValuesAndRows()
    {
        using var db = await CreateContextAsync();
        var students = Enumerable.Range(0, 20).Select(i => new ReaderStudent { Name = "student " + i, School = "school " + i }).ToList();

        // A list whose element type is not an entity type is read by the type of its first entity
        AssertSameValues(db, students.Cast<object>().ToList(), new BulkConfig(), OperationType.Insert);
        await AssertSameRowsAsync(db, "ReaderPeople", () => db.BulkInsertAsync(CloneStudents(students).Cast<object>().ToList()));
    }

    [Fact]
    public async Task BulkRead_ReaderGivesTheSameValues()
    {
        // Not ReaderWideItem: BulkRead of an entity with a shadow string property fails before copying, with or without the reader
        using var db = await CreateContextAsync();
        var items = Enumerable.Range(0, 20).Select(i => new OutputItem { Id = i % 3 == 0 ? 0 : i, Name = "item " + i, Value = i }).ToList();

        AssertSameValues(db, items, new BulkConfig(), OperationType.Read);
        AssertSameValues(db, items, new BulkConfig { UpdateByProperties = [nameof(OutputItem.Name)] }, OperationType.Read);
    }

    [Fact]
    public async Task BulkDelete_ReaderGivesTheSameValues()
    {
        using var db = await CreateContextAsync();
        var parents = await SeedParentsAsync(db);
        var items = CreateWideItems(parents, Scenario.Default);

        AssertSameValues(db, items, new BulkConfig(), OperationType.Delete);
    }

    [Fact]
    public async Task BulkReadAndDelete_WithTheReader_FindTheSameRows()
    {
        using var db = await CreateContextAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE [OutputItems]");
        var items = Enumerable.Range(0, 30).Select(i => new OutputItem { Name = "item " + i, Value = i, Description = "d" + i }).ToList();
        await db.BulkInsertAsync(items, new BulkConfig { SetOutputIdentity = true });

        var toRead = items.Select(a => new OutputItem { Name = a.Name }).ToList();
        var usedReader = await WithSourceModeAsync(SourceMode.Auto, () =>
            db.BulkReadAsync(toRead, new BulkConfig { UpdateByProperties = [nameof(OutputItem.Name)] }));
        Assert.True(Assert.Single(usedReader));
        Assert.Equal(items.Select(a => (a.Id, a.Value, a.Description)), toRead.Select(a => (a.Id, a.Value, a.Description)));

        var parents = await SeedParentsAsync(db);
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE [ReaderWideItems]");
        var wideItems = CreateWideItems(parents, Scenario.Default);
        await db.BulkInsertAsync(wideItems, new BulkConfig { SetOutputIdentity = true });
        usedReader = await WithSourceModeAsync(SourceMode.Auto, () => db.BulkDeleteAsync(wideItems.Take(10).ToList()));
        Assert.True(Assert.Single(usedReader));
        Assert.Equal(wideItems.Skip(10).Select(a => a.Id), await db.ReaderWideItems.OrderBy(a => a.Id).Select(a => a.Id).ToListAsync());
    }

    [Theory]
    [InlineData(false, 2000, false)] // into the table: too few to stream
    [InlineData(false, 0, false)]
    [InlineData(false, 10, false)]
    [InlineData(true, 10, true)] // into the temp table, which is discarded
    [InlineData(true, 0, true)]
    public async Task Auto_UsesTheReaderOnlyWhereAFailureLeavesNoRows(bool setOutputIdentity, int batchSize, bool expectReader)
    {
        using var db = await CreateContextAsync();
        var parents = await SeedParentsAsync(db);
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE [ReaderWideItems]");
        var items = CreateWideItems(parents, Scenario.Default);

        var usedReader = await WithSourceModeAsync(SourceMode.Auto, async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.BulkInsertAsync(items, new BulkConfig { SetOutputIdentity = setOutputIdentity, BatchSize = batchSize });
            await transaction.CommitAsync();
        });

        Assert.Equal(expectReader, Assert.Single(usedReader));
        Assert.Equal(items.Count, await db.ReaderWideItems.CountAsync());
    }

    [Theory]
    [InlineData(0, false, false, true)] // one batch, no transaction
    [InlineData(0, true, false, true)] // one batch in a transaction
    [InlineData(0, true, true, false)] // XACT_ABORT ON: cancelling the copy would roll back the transaction
    [InlineData(0, false, true, true)] // XACT_ABORT ON without a transaction: nothing else to roll back
    [InlineData(2000, false, false, false)] // several batches
    public async Task Auto_DirectInsert_StreamsOnlyLargeSingleBatchesThatCannotRollBackTheTransaction(int batchSize, bool inTransaction, bool xactAbort, bool expectReader)
    {
        using var db = await CreateContextAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE [OutputItems]");
        var items = Enumerable.Range(0, 10_000).Select(i => new OutputItem { Name = "item " + i, Value = i }).ToList();

        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync($"SET XACT_ABORT {(xactAbort ? "ON" : "OFF")};");
        var usedReader = await WithSourceModeAsync(SourceMode.Auto, async () =>
        {
            var transaction = inTransaction ? await db.Database.BeginTransactionAsync() : null;
            await db.BulkInsertAsync(items, new BulkConfig { BatchSize = batchSize });
            if (transaction != null)
            {
                await transaction.CommitAsync();
                await transaction.DisposeAsync();
            }
        });
        await db.Database.ExecuteSqlRawAsync("SET XACT_ABORT OFF;");
        await db.Database.CloseConnectionAsync();

        Assert.Equal(expectReader, Assert.Single(usedReader));
        Assert.Equal(items.Count, await db.OutputItems.CountAsync());
    }

    [Theory]
    [InlineData(SourceMode.DataTable, false)]
    [InlineData(SourceMode.Auto, false)]
    [InlineData(SourceMode.DataTable, true)]
    [InlineData(SourceMode.Auto, true)]
    public async Task FailingEntity_IntoTheTempTable_LeavesTheTransactionUsable(SourceMode mode, bool xactAbort)
    {
        using var db = await CreateContextAsync();
        var parents = await SeedParentsAsync(db);
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE [ReaderWideItems]; TRUNCATE TABLE [OutputItems];");
        var items = CreateWideItems(parents, Scenario.Default);
        items[^5].Code = new ReaderCode(ReaderCode.Failing);

        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync($"SET XACT_ABORT {(xactAbort ? "ON" : "OFF")};");
        var usedReader = await WithSourceModeAsync(mode, async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.BulkInsertAsync([new OutputItem { Name = "before" }]);

            var exception = await Assert.ThrowsAnyAsync<Exception>(() => db.BulkInsertOrUpdateAsync(items, new BulkConfig { SetOutputIdentity = true, UseTempDB = true }));
            Assert.Contains("Cannot convert", exception.ToString());

            // the caller's transaction is still there, with its earlier work
            await db.BulkInsertAsync([new OutputItem { Name = "after" }]);
            await transaction.CommitAsync();
        });
        await db.Database.ExecuteSqlRawAsync("SET XACT_ABORT OFF;");
        await db.Database.CloseConnectionAsync();

        Assert.Equal(mode == SourceMode.Auto, usedReader[1]);
        Assert.Equal(new[] { "after", "before" }, await db.OutputItems.OrderBy(a => a.Name).Select(a => a.Name).ToListAsync());
        Assert.Equal(0, await db.ReaderWideItems.CountAsync());
    }

    [Fact]
    public async Task FloatAndDoubleColumns_KeepTheDataTable()
    {
        using var db = await CreateContextAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE [ReaderFloatItems]");
        var items = Enumerable.Range(0, 20).Select(i => new ReaderFloatItem { Real = 0.1 * i, Single = 0.1f * i, AsDecimal = 2.675 + i / 3.0 }).ToList();

        // Even when asked for the reader: SqlBulkCopy converts these into decimal columns differently from a DataTable
        var usedReader = await WithSourceModeAsync(SourceMode.Reader, () => db.BulkInsertAsync(items, new BulkConfig { SetOutputIdentity = true, UseTempDB = false }));

        Assert.False(Assert.Single(usedReader));
        Assert.Equal(items.Count, await db.ReaderFloatItems.CountAsync());
    }

    [Fact]
    public async Task Auto_KeepsTheDataTableForEnableStreamingAndShadowPropertyValue()
    {
        using var db = await CreateContextAsync();
        var parents = await SeedParentsAsync(db);
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE [ReaderWideItems]");

        var usedReader = await WithSourceModeAsync(SourceMode.Auto, async () =>
        {
            // through the temp table (SetOutputIdentity), where the reader is otherwise used
            await db.BulkInsertAsync(CreateWideItems(parents, Scenario.Default), new BulkConfig { SetOutputIdentity = true, EnableStreaming = true });
            var shadowPropertyValue = CreateConfig(Scenario.ShadowPropertyValue);
            shadowPropertyValue.SetOutputIdentity = true;
            await db.BulkInsertAsync(CreateWideItems(parents, Scenario.ShadowPropertyValue), shadowPropertyValue);
            var enableShadowProperties = CreateConfig(Scenario.EnableShadowProperties);
            enableShadowProperties.SetOutputIdentity = true;
            await db.BulkInsertAsync(CreateWideItems(parents, Scenario.EnableShadowProperties), enableShadowProperties);
        });

        Assert.Equal(new[] { false, false, true }, usedReader);
    }

    [Fact]
    public async Task Auto_KeepsTheDataTableWhenANavigationCouldLazyLoad()
    {
        using var db = await CreateContextAsync();
        var parents = await SeedParentsAsync(db);
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE [ReaderLazyItems]");
        var items = Enumerable.Range(0, 10).Select(i => new ReaderLazyItem { Name = "lazy " + i, Parent = parents[i % parents.Count] }).ToList();

        // The shadow foreign key is read through the navigation's getter, which could query the database while SqlBulkCopy holds the connection
        var usedReader = await WithSourceModeAsync(SourceMode.Auto, () => db.BulkInsertAsync(items));

        Assert.False(Assert.Single(usedReader));
        Assert.Equal(items.Select(a => a.Parent!.Id), await db.ReaderLazyItems.OrderBy(a => a.Id).Select(a => EF.Property<int?>(a, "ParentId")!.Value).ToListAsync());
        AssertSameValues(db, items, new BulkConfig(), OperationType.Insert);
    }

    [Theory]
    [InlineData(SourceMode.DataTable, 0)]
    [InlineData(SourceMode.Reader, 0)]
    [InlineData(SourceMode.Auto, 10)] // keeps the DataTable
    public async Task FailingEntity_InsertsNoRows(SourceMode mode, int batchSize)
    {
        using var db = await CreateContextAsync();
        var parents = await SeedParentsAsync(db);
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE [ReaderWideItems]");
        var items = CreateWideItems(parents, Scenario.Default);
        items[^5].Code = new ReaderCode(ReaderCode.Failing);

        var usedReader = await WithSourceModeAsync(mode, async () =>
        {
            var exception = await Assert.ThrowsAnyAsync<Exception>(() => db.BulkInsertAsync(items, new BulkConfig { BatchSize = batchSize }));
            Assert.Contains("Cannot convert", exception.ToString());
        });

        Assert.Equal(mode == SourceMode.Reader, Assert.Single(usedReader));
        Assert.Equal(0, await db.ReaderWideItems.CountAsync());
    }

    [Fact]
    public async Task DateTimeKinds_AreStoredAsUnspecifiedLikeTheDataTable()
    {
        using var db = await CreateContextAsync();
        var parents = await SeedParentsAsync(db);
        var items = CreateWideItems(parents, Scenario.Default);
        items[0].When = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        items[1].When = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local);

        var (dataTable, readerRows, _, _) = BuildBoth(db, items, new BulkConfig(), OperationType.Insert);
        var column = dataTable.Columns[nameof(ReaderWideItem.When)]!.Ordinal;
        Assert.Equal(DateTimeKind.Unspecified, ((DateTime)dataTable.Rows[0][column]).Kind);
        Assert.Equal(DateTimeKind.Unspecified, ((DateTime)readerRows[0][column]).Kind);
        Assert.Equal(DateTimeKind.Unspecified, ((DateTime)readerRows[1][column]).Kind);
    }

    private static BulkConfig CreateConfig(Scenario scenario) => scenario switch
    {
        Scenario.DateTime2PrecisionForceRound => new BulkConfig { DateTime2PrecisionForceRound = true },
        Scenario.EnableShadowProperties => new BulkConfig { EnableShadowProperties = true },
        Scenario.ShadowPropertyValue => new BulkConfig
        {
            EnableShadowProperties = true,
            ShadowPropertyValue = (entity, name) => name == ReaderWideItem.NoteShadowProperty ? "note for " + ((ReaderWideItem)entity).Name : null,
        },
        Scenario.PropertiesToInclude => new BulkConfig { PropertiesToInclude = [nameof(ReaderWideItem.Name), nameof(ReaderWideItem.Geometry), nameof(ReaderWideItem.Code), "Address.Street"] },
        Scenario.PropertiesToExclude => new BulkConfig { PropertiesToExclude = [nameof(ReaderWideItem.Geography), nameof(ReaderWideItem.StatusOrNull), nameof(ReaderWideItem.CodeOrNull)] },
        Scenario.SetOutputIdentity => new BulkConfig { SetOutputIdentity = true, UseTempDB = true },
        _ => new BulkConfig(),
    };

    private static List<ReaderWideItem> CreateWideItems(List<ReaderParent> parents, Scenario scenario)
    {
        var items = new List<ReaderWideItem>();
        for (int i = 0; i < 40; i++)
        {
            bool odd = i % 2 == 1;
            items.Add(new ReaderWideItem
            {
                Name = "item " + i,
                Text = odd ? null : "text with ünïcode " + i,
                Ansi = "ansi " + i,
                Flag = odd,
                Tiny = (byte)(i * 7),
                Small = (short)(-i * 100),
                Big = 5_000_000_000L + i,
                BigOrNull = i % 3 == 0 ? null : i,
                Money = 1234.5678m + i,
                Key = new Guid(i, 1, 2, [3, 4, 5, 6, 7, 8, 9, 10]),
                KeyOrNull = odd ? null : Guid.Empty,
                When = new DateTime(2026, 11, 1, 8, 30, 0).AddTicks(1_234_567 * i),
                WhenOrNull = odd ? null : new DateTime(2000, 1, 1),
                Rounded = new DateTime(2026, 11, 1, 8, 30, 0).AddTicks(9_999 * i + 5_000), // rounding differs from truncating to datetime2(3)
                Offset = new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.FromHours(i % 5)),
                Duration = TimeSpan.FromSeconds(i * 61),
                Day = new DateOnly(2026, 1, 1).AddDays(i),
                Time = new TimeOnly(1, 2, 3).Add(TimeSpan.FromMinutes(i)),
                Bytes = i % 4 == 0 ? null : [(byte)i, 0, 255],
                Status = (ReaderStatus)(i % 3 == 2 ? 200 : i % 2),
                StatusOrNull = odd ? null : ReaderStatus.Closed,
                StatusText = odd ? ReaderStatus.Active : ReaderStatus.Closed,
                Code = new ReaderCode("code " + i),
                CodeOrNull = i % 3 == 0 ? null : new ReaderCode("maybe " + i),
                WithDefault = scenario == Scenario.AllDefaultValues ? 0 : i,
                Geography = i % 5 == 0 ? null : new Point(13 + i / 100.0, 52) { SRID = 4326 },
                Geometry = i % 6 == 0 ? null : new Point(i, -i),
                Hierarchy = i % 7 == 0 ? null : HierarchyId.Parse($"/{i}/{i % 3}/"),
                Address = i % 8 == 0 ? null : new ReaderAddress { Street = "street " + i, Kind = odd ? ReaderStatus.Active : ReaderStatus.None, Location = i % 3 == 0 ? null : new Point(10, 20 + i) },
                Parent = i % 4 == 3 ? null : parents[i % parents.Count],
            });
        }

        return items;
    }

    private static List<ReaderStudent> CloneStudents(List<ReaderStudent> students) =>
        students.Select(a => new ReaderStudent { Name = a.Name, School = a.School }).ToList();

    private async Task<InsertPerformanceContext> CreateContextAsync()
    {
        _dbFixture.GetDb(DbServerType.SQLServer).Dispose(); // creates the database once
        var db = new InsertPerformanceContext(ContextUtil.GetOptions<InsertPerformanceContext>(DbServerType.SQLServer, databaseName: nameof(EntityBulkCopyReaderTests)));
        await Task.CompletedTask;
        return db;
    }

    private static async Task<List<ReaderParent>> SeedParentsAsync(InsertPerformanceContext db)
    {
        var parents = await db.ReaderParents.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
        if (parents.Count == 0)
        {
            parents = Enumerable.Range(0, 3).Select(i => new ReaderParent { Name = "parent " + i }).ToList();
            await db.BulkInsertAsync(parents, new BulkConfig { SetOutputIdentity = true });
        }

        return parents;
    }

    private static async Task<List<bool>> WithSourceModeAsync(SourceMode mode, Func<Task> action)
    {
        var used = new List<bool>();
        var modeField = ReaderSettingsType.GetField("Mode", BindingFlags.NonPublic | BindingFlags.Static)!;
        var observerField = ReaderSettingsType.GetField("SourceChosen", BindingFlags.NonPublic | BindingFlags.Static)!;
        modeField.SetValue(null, Enum.Parse(modeField.FieldType, mode.ToString()));
        observerField.SetValue(null, new Action<bool>(used.Add));
        try
        {
            await action();
        }
        finally
        {
            modeField.SetValue(null, Enum.Parse(modeField.FieldType, nameof(SourceMode.Auto)));
            observerField.SetValue(null, null);
        }

        return used;
    }

    private async Task AssertSameRowsAsync(InsertPerformanceContext db, string table, Func<Task> insert)
    {
        var rows = new List<List<object?[]>>();
        foreach (var mode in new[] { SourceMode.DataTable, SourceMode.Reader })
        {
            await db.Database.ExecuteSqlRawAsync($"TRUNCATE TABLE [{table}]");
            var usedReader = await WithSourceModeAsync(mode, insert);
            Assert.Equal(mode == SourceMode.Reader, Assert.Single(usedReader));
            rows.Add(await ReadTableAsync(db, table));
        }

        Assert.NotEmpty(rows[0]);
        AssertSameRows(rows[0], rows[1]);
    }

    private static void AssertSameRows(List<object?[]> expected, List<object?[]> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int row = 0; row < expected.Count; row++)
        {
            Assert.Equal(expected[row].Length, actual[row].Length);
            for (int column = 0; column < expected[row].Length; column++)
            {
                AssertSameValue(expected[row][column], actual[row][column], $"row {row}, column {column}");
            }
        }
    }

    private static void AssertSameValue(object? expected, object? actual, string where)
    {
        if (expected is byte[] expectedBytes && actual is byte[] actualBytes)
        {
            Assert.True(expectedBytes.SequenceEqual(actualBytes), $"{where}: bytes differ");
            return;
        }

        Assert.True(Equals(expected, actual), $"{where}: expected <{expected}> ({expected?.GetType().Name}), actual <{actual}> ({actual?.GetType().Name})");
        Assert.Equal(expected?.GetType(), actual?.GetType());
        if (expected is DateTime expectedDateTime)
        {
            Assert.Equal(expectedDateTime.Kind, ((DateTime)actual!).Kind);
        }
    }

    /// <summary>
    /// Every row of the table ordered by its first column, with spatial and hierarchy values as their bytes.
    /// </summary>
    private static async Task<List<object?[]>> ReadTableAsync(InsertPerformanceContext db, string table)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            var columns = new List<string>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT c.name, t.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id " +
                                      $"WHERE c.object_id = OBJECT_ID(N'[{table}]') ORDER BY c.column_id";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var name = reader.GetString(0);
                    columns.Add(reader.GetString(1) is "geography" or "geometry" or "hierarchyid" ? $"CAST([{name}] AS varbinary(max)) AS [{name}]" : $"[{name}]");
                }
            }

            var rows = new List<object?[]>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT {string.Join(", ", columns)} FROM [{table}] ORDER BY 1";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var values = new object?[reader.FieldCount];
                    reader.GetValues(values!);
                    rows.Add(values);
                }
            }

            return rows;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static void AssertSameValues<T>(InsertPerformanceContext db, IList<T> entities, BulkConfig bulkConfig, OperationType operationType)
    {
        db.Database.OpenConnection(); // UseTempDB needs an open connection
        DataTable dataTable;
        List<object[]> readerRows;
        string[] readerNames;
        System.Type[] readerTypes;
        try
        {
            (dataTable, readerRows, readerNames, readerTypes) = BuildBoth(db, entities, bulkConfig, operationType);
        }
        finally
        {
            db.Database.CloseConnection();
        }

        Assert.Equal(dataTable.Columns.Cast<DataColumn>().Select(a => a.ColumnName), readerNames);
        Assert.Equal(
            dataTable.Columns.Cast<DataColumn>().Select(a => a.DataType.IsEnum ? Enum.GetUnderlyingType(a.DataType) : a.DataType),
            readerTypes);
        Assert.Equal(dataTable.Rows.Count, readerRows.Count);
        Assert.Equal(entities.Count, readerRows.Count);
        for (int row = 0; row < readerRows.Count; row++)
        {
            for (int column = 0; column < readerNames.Length; column++)
            {
                AssertSameValue(dataTable.Rows[row][column], readerRows[row][column], $"row {row}, column {readerNames[column]}");
            }
        }
    }

    /// <summary>
    /// Builds the DataTable and runs the reader for the same entities, as the SQL Server adapter does before SqlBulkCopy.
    /// </summary>
    private static (DataTable DataTable, List<object[]> ReaderRows, string[] ReaderNames, System.Type[] ReaderTypes) BuildBoth<T>(
        InsertPerformanceContext db, IList<T> entities, BulkConfig bulkConfig, OperationType operationType)
    {
        var tableInfo = TableInfo.CreateInstance(db, typeof(T), entities, operationType, bulkConfig);
        if (operationType == OperationType.Read)
        {
            tableInfo.ConfigureBulkReadTableInfo();
        }
        else if (operationType != OperationType.Insert || bulkConfig.SetOutputIdentity)
        {
            tableInfo.InsertToTempTable = true;
        }

        tableInfo.CheckToSetIdentityForPreserveOrder(tableInfo, entities);
        try
        {
            var adapter = typeof(SqlOperationsServerAdapter);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
            var createSchema = adapter.GetMethod("CreateBulkCopySchema", flags)!.MakeGenericMethod(typeof(T));
            var fillDataTable = adapter.GetMethod("FillDataTable", flags)!.MakeGenericMethod(typeof(T));

            var dataTableSchema = createSchema.Invoke(null, [db, typeof(T), entities, tableInfo]);
            var dataTable = (DataTable)fillDataTable.Invoke(null, [db, entities, tableInfo, dataTableSchema])!;

            var readerSchema = createSchema.Invoke(null, [db, typeof(T), entities, tableInfo]);
            var create = ReaderGenericType.MakeGenericType(typeof(T)).GetMethod("Create", flags)!;
            using var reader = (DbDataReader?)create.Invoke(null, [db, entities, tableInfo, readerSchema]);
            Assert.NotNull(reader);

            var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            var types = Enumerable.Range(0, reader.FieldCount).Select(reader.GetFieldType).ToArray();
            var rows = new List<object[]>();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(values);
            }

            return (dataTable, rows, names, types);
        }
        finally
        {
            tableInfo.CheckToSetIdentityForPreserveOrder(tableInfo, entities, reset: true);
        }
    }
}
