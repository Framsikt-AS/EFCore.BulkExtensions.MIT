using EFCore.BulkExtensions.SqlAdapters;
using EFCore.BulkExtensions.Tests.BulkInsertOrUpdate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace EFCore.BulkExtensions.Tests.InsertPerformance;

/// <summary>
/// The SQL Server MERGE output on a table with a rowversion column. The output table gets the rowversion column appended after it is created,
/// so the OUTPUT ... INTO has to name its columns. These tests read the output table right after the MERGE and check what the entities get back.
/// </summary>
public class FullMergeOutputTests : IClassFixture<FullMergeOutputTests.DatabaseFixture>, IAssemblyFixture<DbAssemblyFixture>
{
    private const string Index = "EF_BULK_EXTENSIONS_MIT_ORIGINAL_INDEX";
    private const string Action = "EF_BULK_EXTENSIONS_MIT_MERGE_ACTION_IUD";

    private readonly DatabaseFixture _dbFixture;

    public FullMergeOutputTests(DatabaseFixture dbFixture)
    {
        _dbFixture = dbFixture;
    }

    public class DatabaseFixture : BulkDbTestsFixture<FullMergeOutputContext>
    {
        protected override string DbName => nameof(FullMergeOutputTests);
    }

    public enum OutputConfig
    {
        /// <summary>SetOutputIdentity = false, CalculateStats = true: the full output, read only for the stats.</summary>
        StatsOnly,

        /// <summary>SetOutputIdentity and SetOutputNonIdentityColumns: the full output, read back into the entities.</summary>
        NonIdentityColumns,

        /// <summary>As <see cref="NonIdentityColumns"/>, with CalculateStats.</summary>
        NonIdentityColumnsWithStats,

        /// <summary>SetOutputIdentity alone: the output with the original index column, read for the generated ids.</summary>
        IdentityOnly,

        /// <summary>As <see cref="IdentityOnly"/>, with CalculateStats.</summary>
        IdentityOnlyWithStats,
    }

    public static TheoryData<OperationType, OutputConfig, bool> Cases()
    {
        var data = new TheoryData<OperationType, OutputConfig, bool>();
        foreach (var operation in new[] { OperationType.Insert, OperationType.InsertOrUpdate, OperationType.InsertOrUpdateOrDelete, OperationType.Update, OperationType.Delete })
        foreach (var config in Enum.GetValues<OutputConfig>())
        foreach (var useTempDb in new[] { false, true })
        {
            if (operation == OperationType.Insert && config == OutputConfig.StatsOnly)
            {
                continue; // BulkInsert without SetOutputIdentity is a plain bulk copy, with no MERGE
            }

            data.Add(operation, config, useTempDb);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RowVersionTable_OutputPutsEveryValueInItsColumn(OperationType operation, OutputConfig config, bool useTempDb)
    {
        using var db = CreateContext(out var probe);
        await db.FullOutputVersionedItems.BatchDeleteAsync();

        // seeded through EF Core, so the existing rows' ids and rowversions do not depend on the code under test
        var existing = Enumerable.Range(0, 4).Select(i => new FullOutputVersionedItem { Name = "existing " + i, Value = i }).ToList();
        db.FullOutputVersionedItems.AddRange(existing);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await db.FullOutputVersionedItems.AsNoTracking().ToDictionaryAsync(a => a.Id);
        probe.Clear();

        var newItems = operation is OperationType.Insert or OperationType.InsertOrUpdate or OperationType.InsertOrUpdateOrDelete
            ? Enumerable.Range(0, 5).Select(i => new FullOutputVersionedItem { Name = "new " + i, Value = 100 + i }).ToList()
            : new List<FullOutputVersionedItem>();
        var touched = operation == OperationType.Insert ? new List<FullOutputVersionedItem>() : existing.Take(2).ToList();
        if (operation != OperationType.Delete)
        {
            touched.ForEach(a => a.Value += 1000); // every existing row in the list changes, so each one is output
        }

        var items = newItems.Concat(touched).ToList();
        var touchedIds = touched.Select(a => a.Id).ToList();
        var bulkConfig = CreateBulkConfig(config, useTempDb);
        await RunAsync(db, operation, items, bulkConfig);

        int expectedInserted = newItems.Count;
        int expectedUpdated = operation is OperationType.Delete ? 0 : touched.Count;
        int expectedDeleted = operation switch
        {
            OperationType.Delete => touched.Count,
            OperationType.InsertOrUpdateOrDelete => existing.Count - touched.Count,
            _ => 0,
        };

        // the database
        var inDb = await db.FullOutputVersionedItems.AsNoTracking().ToDictionaryAsync(a => a.Name);
        var expectedNames = operation switch
        {
            OperationType.Delete => existing.Skip(2).Select(a => a.Name),
            OperationType.InsertOrUpdateOrDelete => items.Select(a => a.Name),
            _ => existing.Concat(newItems).Select(a => a.Name),
        };
        Assert.Equal(expectedNames.OrderBy(a => a), inDb.Keys.OrderBy(a => a));
        if (operation != OperationType.Delete)
        {
            Assert.All(touched, a => Assert.Equal(a.Value, inDb[a.Name].Value));
        }

        // the output table: each value in its own column
        var output = probe.Single();
        bool hasActionColumn = bulkConfig.CalculateStats || operation is OperationType.InsertOrUpdateOrDelete or OperationType.Delete;
        bool idOnly = config is OutputConfig.IdentityOnly or OutputConfig.IdentityOnlyWithStats;
        var expectedColumns = ExpectedColumns(hasActionColumn, hasIndexColumn: idOnly);
        if (idOnly)
        {
            // the id-only MERGE output (IdOnlyMergeOutputTests) leaves out the other entity columns; the full output has them
            string[] optional = ["Computed", "Name", "Value"];
            Assert.Equal(expectedColumns.Except(optional), output.Columns.Except(optional));
        }
        else
        {
            Assert.Equal(expectedColumns, output.Columns);
        }

        var inDbById = inDb.Values.ToDictionary(a => a.Id);
        foreach (var row in output.Rows.Where(a => a["Id"] is not DBNull))
        {
            var action = hasActionColumn ? (string)row[Action] : null;
            var expected = action == "D" ? before[(int)row["Id"]] : inDbById[(int)row["Id"]];
            Assert.Equal(expected.RowVersion, (byte[])row["RowVersion"]);
            if (row.TryGetValue("Name", out var name))
            {
                Assert.Equal(expected.Name, (string)name);
                Assert.Equal(expected.Value, (int)row["Value"]);
                Assert.Equal(expected.Computed, (int)row["Computed"]);
            }
        }

        if (hasActionColumn)
        {
            Assert.Equal(expectedInserted, output.Rows.Count(a => (string)a[Action] == "I"));
            Assert.Equal(expectedUpdated, output.Rows.Count(a => (string)a[Action] == "U"));
            Assert.Equal(expectedDeleted, output.Rows.Count(a => (string)a[Action] == "D"));
        }

        if (bulkConfig.CalculateStats)
        {
            Assert.Equal(expectedInserted, bulkConfig.StatsInfo!.StatsNumberInserted);
            Assert.Equal(expectedUpdated, bulkConfig.StatsInfo.StatsNumberUpdated);
            Assert.Equal(expectedDeleted, bulkConfig.StatsInfo.StatsNumberDeleted);
        }

        // the entities
        if (operation == OperationType.Delete)
        {
            Assert.Equal(touchedIds, touched.Select(a => a.Id));
        }
        else if (bulkConfig.SetOutputIdentity)
        {
            Assert.All(items, a => Assert.Equal(inDb[a.Name].Id, a.Id));
            if (bulkConfig.SetOutputNonIdentityColumns)
            {
                Assert.All(items, a => Assert.Equal(inDb[a.Name].RowVersion, a.RowVersion));
                Assert.All(items, a => Assert.Equal(inDb[a.Name].Computed, a.Computed));
            }
        }
    }

    [Fact]
    public void RowVersionTable_Sync_CalculateStats()
    {
        using var db = CreateContext(out var probe);
        db.FullOutputVersionedItems.BatchDelete();

        var items = Enumerable.Range(0, 5).Select(i => new FullOutputVersionedItem { Name = "new " + i, Value = i }).ToList();
        var bulkConfig = new BulkConfig { CalculateStats = true };
        db.BulkInsertOrUpdate(items, bulkConfig);

        Assert.Equal(5, bulkConfig.StatsInfo!.StatsNumberInserted);
        var inDb = db.FullOutputVersionedItems.AsNoTracking().ToDictionary(a => a.Id);
        Assert.All(probe.Single().Rows, a => Assert.Equal(inDb[(int)a["Id"]].RowVersion, (byte[])a["RowVersion"]));
    }

    [Fact]
    public void RowVersionTable_Sync_SetOutputNonIdentityColumns()
    {
        using var db = CreateContext(out _);
        db.FullOutputVersionedItems.BatchDelete();

        var items = Enumerable.Range(0, 5).Select(i => new FullOutputVersionedItem { Name = "new " + i, Value = i }).ToList();
        var bulkConfig = new BulkConfig { SetOutputIdentity = true, SetOutputNonIdentityColumns = true, CalculateStats = true };
        db.BulkInsertOrUpdateOrDelete(items, bulkConfig);

        Assert.Equal(5, bulkConfig.StatsInfo!.StatsNumberInserted);
        var inDb = db.FullOutputVersionedItems.AsNoTracking().ToDictionary(a => a.Name);
        Assert.All(items, a => Assert.Equal((inDb[a.Name].Id, inDb[a.Name].Computed), (a.Id, a.Computed)));
        Assert.All(items, a => Assert.Equal(inDb[a.Name].RowVersion, a.RowVersion));
    }

    [Theory]
    [InlineData(OutputConfig.StatsOnly)]
    [InlineData(OutputConfig.NonIdentityColumnsWithStats)]
    public async Task TableWithoutRowVersion_KeepsThePositionalOutput(OutputConfig config)
    {
        using var db = CreateContext(out var probe);
        await db.FullOutputPlainItems.BatchDeleteAsync();

        var items = Enumerable.Range(0, 5).Select(i => new FullOutputPlainItem { Name = "new " + i, Value = i }).ToList();
        var bulkConfig = CreateBulkConfig(config, useTempDb: false);
        await RunAsync(db, OperationType.InsertOrUpdate, items, bulkConfig);

        Assert.DoesNotMatch(@" INTO [^ ;]+ \(", probe.Single().CommandText); // the SQL is unchanged for tables without a rowversion column
        Assert.Equal(5, bulkConfig.StatsInfo!.StatsNumberInserted);
        var inDb = await db.FullOutputPlainItems.AsNoTracking().ToDictionaryAsync(a => a.Name);
        Assert.All(probe.Single().Rows, a => Assert.Equal(inDb[(string)a["Name"]].Id, (int)a["Id"]));
        if (bulkConfig.SetOutputIdentity)
        {
            Assert.All(items, a => Assert.Equal(inDb[a.Name].Id, a.Id));
        }
    }

    private static BulkConfig CreateBulkConfig(OutputConfig config, bool useTempDb) => config switch
    {
        OutputConfig.StatsOnly => new BulkConfig { CalculateStats = true, UseTempDB = useTempDb },
        OutputConfig.NonIdentityColumns => new BulkConfig { SetOutputIdentity = true, SetOutputNonIdentityColumns = true, UseTempDB = useTempDb },
        OutputConfig.NonIdentityColumnsWithStats => new BulkConfig { SetOutputIdentity = true, SetOutputNonIdentityColumns = true, CalculateStats = true, UseTempDB = useTempDb },
        OutputConfig.IdentityOnly => new BulkConfig { SetOutputIdentity = true, UseTempDB = useTempDb },
        OutputConfig.IdentityOnlyWithStats => new BulkConfig { SetOutputIdentity = true, CalculateStats = true, UseTempDB = useTempDb },
        _ => throw new ArgumentOutOfRangeException(nameof(config)),
    };

    private static List<string> ExpectedColumns(bool hasActionColumn, bool hasIndexColumn)
    {
        var columns = new List<string> { "Id", "Computed", "Name", "Value" };
        if (hasActionColumn)
        {
            columns.Add(Action);
        }

        if (hasIndexColumn)
        {
            columns.Add(Index);
        }

        columns.Add("RowVersion"); // appended after the table is created
        return columns;
    }

    private static async Task RunAsync<T>(FullMergeOutputContext db, OperationType operation, List<T> items, BulkConfig bulkConfig) where T : class
    {
        // UseTempDB needs a transaction
        await using var transaction = await db.Database.BeginTransactionAsync();
        switch (operation)
        {
            case OperationType.Insert:
                await db.BulkInsertAsync(items, bulkConfig);
                break;
            case OperationType.InsertOrUpdate:
                await db.BulkInsertOrUpdateAsync(items, bulkConfig);
                break;
            case OperationType.InsertOrUpdateOrDelete:
                await db.BulkInsertOrUpdateOrDeleteAsync(items, bulkConfig);
                break;
            case OperationType.Update:
                await db.BulkUpdateAsync(items, bulkConfig);
                break;
            case OperationType.Delete:
                await db.BulkDeleteAsync(items, bulkConfig);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        await transaction.CommitAsync();
    }

    private FullMergeOutputContext CreateContext(out OutputTableProbe probe)
    {
        probe = new OutputTableProbe();
        var options = ContextUtil.GetOptions<FullMergeOutputContext>(DbServerType.SQLServer, [probe], nameof(FullMergeOutputTests));
        _dbFixture.GetDb(DbServerType.SQLServer).Dispose(); // creates the database once
        return new FullMergeOutputContext(options);
    }

    /// <summary>
    /// After each MERGE with an OUTPUT clause, reads the output table's columns and rows on the same connection and transaction.
    /// </summary>
    private sealed class OutputTableProbe : DbCommandInterceptor
    {
        private static readonly Regex OutputTable = new(@" OUTPUT .* INTO (?<table>\[[^\]]+\]\.\[[^\]]+\])", RegexOptions.Compiled);

        private readonly List<OutputTable> _tables = new();

        public OutputTable Single() => Assert.Single(_tables);

        public void Clear() => _tables.Clear();

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            Read(command);
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Read(command);
            return ValueTask.FromResult(result);
        }

        private void Read(DbCommand command)
        {
            if (!command.CommandText.Contains("MERGE ", StringComparison.Ordinal))
            {
                return;
            }

            var match = OutputTable.Match(command.CommandText);
            if (!match.Success)
            {
                return;
            }

            var table = match.Groups["table"].Value;
            var isTemp = table.Contains("[#", StringComparison.Ordinal);
            var objectName = isTemp ? "tempdb.." + table[(table.IndexOf("[#", StringComparison.Ordinal) + 1)..^1] : table;

            using var columnsCommand = command.Connection!.CreateCommand();
            columnsCommand.Transaction = command.Transaction;
            columnsCommand.CommandText = $"SELECT name FROM {(isTemp ? "tempdb." : "")}sys.columns WHERE object_id = OBJECT_ID(N'{objectName}') ORDER BY column_id";
            var columns = new List<string>();
            using (var reader = columnsCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    columns.Add(reader.GetString(0));
                }
            }

            using var rowsCommand = command.Connection.CreateCommand();
            rowsCommand.Transaction = command.Transaction;
            rowsCommand.CommandText = $"SELECT * FROM {table}";
            var rows = new List<Dictionary<string, object>>();
            using (var reader = rowsCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName, reader.GetValue));
                }
            }

            _tables.Add(new OutputTable(command.CommandText, columns, rows));
        }
    }

    private sealed record OutputTable(string CommandText, List<string> Columns, List<Dictionary<string, object>> Rows);
}

public class FullMergeOutputContext : DbContext
{
    public FullMergeOutputContext(DbContextOptions options) : base(options)
    {
    }

    public DbSet<FullOutputVersionedItem> FullOutputVersionedItems { get; set; } = null!;
    public DbSet<FullOutputPlainItem> FullOutputPlainItems { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FullOutputVersionedItem>().Property(a => a.Computed).HasComputedColumnSql("[Value] * 2");
    }
}

public class FullOutputVersionedItem
{
    public int Id { get; set; }

    [MaxLength(50)]
    public string Name { get; set; } = "";

    public int Value { get; set; }

    /// <summary>Not in the bulk copy; read back only from the output table.</summary>
    public int Computed { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = null!;
}

public class FullOutputPlainItem
{
    public int Id { get; set; }

    [MaxLength(50)]
    public string Name { get; set; } = "";

    public int Value { get; set; }
}
