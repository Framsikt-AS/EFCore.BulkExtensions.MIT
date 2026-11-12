using EFCore.BulkExtensions.SqlAdapters;
using EFCore.BulkExtensions.Tests.BulkInsertOrUpdate;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace EFCore.BulkExtensions.Tests.InsertPerformance;

/// <summary>
/// When only generated ids are read back (SetOutputIdentity without SetOutputNonIdentityColumns) the MERGE outputs only the id,
/// the action, the original index and the timestamp. These tests read the output table right after the MERGE and check the ids it gives the entities.
/// </summary>
public class IdOnlyMergeOutputTests : IClassFixture<IdOnlyMergeOutputTests.DatabaseFixture>, IAssemblyFixture<DbAssemblyFixture>
{
    private const string Index = "EF_BULK_EXTENSIONS_MIT_ORIGINAL_INDEX";
    private const string Action = "EF_BULK_EXTENSIONS_MIT_MERGE_ACTION_IUD";

    private readonly DatabaseFixture _dbFixture;

    public IdOnlyMergeOutputTests(DatabaseFixture dbFixture)
    {
        _dbFixture = dbFixture;
    }

    public class DatabaseFixture : BulkDbTestsFixture<InsertPerformanceContext>
    {
        protected override string DbName => nameof(IdOnlyMergeOutputTests);
    }

    public static TheoryData<OperationType, bool, bool> Operations()
    {
        var data = new TheoryData<OperationType, bool, bool>();
        foreach (var operation in new[] { OperationType.Insert, OperationType.InsertOrUpdate, OperationType.InsertOrUpdateOrDelete })
        foreach (var calculateStats in new[] { false, true })
        foreach (var useTempDb in new[] { false, true })
        {
            data.Add(operation, calculateStats, useTempDb);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task IdentityTable_OutputsOnlyTheIdAndSetsIds(OperationType operation, bool calculateStats, bool useTempDb)
    {
        using var db = CreateContext(out var probe);
        await db.OutputItems.BatchDeleteAsync();
        var existing = await SeedAsync(db, operation, i => new OutputItem { Name = "existing " + i, Value = i });
        probe.Clear();

        var items = Enumerable.Range(0, 5).Select(i => new OutputItem { Name = "new " + i, Value = 100 + i, Description = "d" }).ToList();
        if (operation != OperationType.Insert)
        {
            existing[0].Value = 999; // updated
            items.Add(existing[0]);
            items.Add(existing[1]); // unchanged
        }

        var bulkConfig = new BulkConfig { SetOutputIdentity = true, CalculateStats = calculateStats, UseTempDB = useTempDb };
        await RunAsync(db, operation, items, bulkConfig);

        var output = probe.Single();
        Assert.Equal(ExpectedColumns(["Id"], operation, calculateStats), output.Columns);
        int unchanged = operation == OperationType.Insert ? 0 : 1; // the MERGE does not update (or output) a row that has not changed
        Assert.Equal(items.Count - unchanged, output.Rows.Count(a => a[Index] is not DBNull));

        var inDb = await db.OutputItems.AsNoTracking().ToDictionaryAsync(a => a.Name);
        Assert.All(items, a => Assert.Equal(inDb[a.Name].Id, a.Id));
        Assert.All(existing.Take(1), a => Assert.Equal(999, inDb[a.Name].Value));
        Assert.Equal(operation == OperationType.InsertOrUpdateOrDelete ? 7 : 5 + existing.Count, inDb.Count);

        // the index column maps each output row to its entity
        foreach (var row in output.Rows.Where(a => a[Index] is not DBNull))
        {
            Assert.Equal(items[(int)row[Index]].Id, (int)row["Id"]);
        }

        if (calculateStats)
        {
            Assert.Equal(5, bulkConfig.StatsInfo!.StatsNumberInserted);
            Assert.Equal(operation == OperationType.Insert ? 0 : 1, bulkConfig.StatsInfo.StatsNumberUpdated);
            Assert.Equal(operation == OperationType.InsertOrUpdateOrDelete ? existing.Count - 2 : 0, bulkConfig.StatsInfo.StatsNumberDeleted);
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task RowVersionTable_OutputsIdAndRowVersionAndSetsIds(OperationType operation, bool calculateStats, bool useTempDb)
    {
        using var db = CreateContext(out var probe);
        await db.OutputVersionedItems.BatchDeleteAsync();
        var existing = await SeedAsync(db, operation, i => new OutputVersionedItem { Name = "existing " + i, Value = i });
        probe.Clear();

        // more entities than the rowversion's low bytes, so a misplaced rowversion would map to real indexes
        var items = Enumerable.Range(0, 3000).Select(i => new OutputVersionedItem { Name = "new " + i, Value = i }).ToList();
        if (operation != OperationType.Insert)
        {
            existing[0].Value = 999;
            items.Add(existing[0]);
        }

        var bulkConfig = new BulkConfig { SetOutputIdentity = true, CalculateStats = calculateStats, UseTempDB = useTempDb };
        await RunAsync(db, operation, items, bulkConfig);

        var output = probe.Single();
        Assert.Equal(ExpectedColumns(["Id", "RowVersion"], operation, calculateStats), output.Columns);

        var inDb = await db.OutputVersionedItems.AsNoTracking().ToDictionaryAsync(a => a.Name);
        Assert.All(items, a => Assert.Equal(inDb[a.Name].Id, a.Id));
        Assert.All(existing.Take(1), a => Assert.Equal(999, inDb[a.Name].Value));

        foreach (var row in output.Rows.Where(a => a[Index] is not DBNull))
        {
            var item = items[(int)row[Index]];
            Assert.Equal(item.Id, (int)row["Id"]);
            Assert.Equal(inDb[item.Name].RowVersion, (byte[])row["RowVersion"]);
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task CompositeKeyTable_OutputsNoIdColumn(OperationType operation, bool calculateStats, bool useTempDb)
    {
        using var db = CreateContext(out var probe);
        await db.OutputCompositeItems.BatchDeleteAsync();
        var existing = await SeedAsync(db, operation, i => new OutputCompositeItem { KeyA = 1, KeyB = i, Name = "existing " + i, Value = i });
        probe.Clear();

        var items = Enumerable.Range(0, 5).Select(i => new OutputCompositeItem { KeyA = 2, KeyB = i, Name = "new " + i, Value = i }).ToList();
        if (operation != OperationType.Insert)
        {
            existing[0].Value = 999;
            items.Add(existing[0]);
        }

        var bulkConfig = new BulkConfig { SetOutputIdentity = true, CalculateStats = calculateStats, UseTempDB = useTempDb };
        await RunAsync(db, operation, items, bulkConfig);

        var output = probe.Single();
        Assert.Equal(ExpectedColumns([], operation, calculateStats), output.Columns);
        Assert.Equal(items.Count, output.Rows.Count(a => a[Index] is not DBNull));

        var inDb = await db.OutputCompositeItems.AsNoTracking().ToDictionaryAsync(a => a.Name);
        Assert.All(items, a => Assert.Equal((inDb[a.Name].KeyA, inDb[a.Name].KeyB, inDb[a.Name].Value), (a.KeyA, a.KeyB, a.Value)));
        if (calculateStats)
        {
            Assert.Equal(5, bulkConfig.StatsInfo!.StatsNumberInserted);
            Assert.Equal(operation == OperationType.Insert ? 0 : 1, bulkConfig.StatsInfo.StatsNumberUpdated);
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task DatabaseDefaultKeyTable_OutputsTheKeyAndSetsIt(OperationType operation, bool calculateStats, bool useTempDb)
    {
        using var db = CreateContext(out var probe);
        await db.OutputGuidItems.BatchDeleteAsync();
        var existing = await SeedAsync(db, operation, i => new OutputGuidItem { Name = "existing " + i, Value = i });
        probe.Clear();

        var items = Enumerable.Range(0, 5).Select(i => new OutputGuidItem { Name = "new " + i, Value = i }).ToList();
        if (operation != OperationType.Insert)
        {
            existing[0].Value = 999;
            items.Add(existing[0]);
        }

        var bulkConfig = new BulkConfig { SetOutputIdentity = true, CalculateStats = calculateStats, UseTempDB = useTempDb };
        await RunAsync(db, operation, items, bulkConfig);

        var output = probe.Single();
        Assert.Equal(ExpectedColumns(["Id"], operation, calculateStats), output.Columns);

        var inDb = await db.OutputGuidItems.AsNoTracking().ToDictionaryAsync(a => a.Name);
        Assert.All(items, a => Assert.NotEqual(Guid.Empty, a.Id));
        Assert.All(items, a => Assert.Equal(inDb[a.Name].Id, a.Id));
        Assert.All(existing.Take(1), a => Assert.Equal(999, inDb[a.Name].Value));
    }

    [Theory]
    [InlineData(OperationType.Insert)]
    [InlineData(OperationType.InsertOrUpdate)]
    [InlineData(OperationType.InsertOrUpdateOrDelete)]
    public async Task SetOutputNonIdentityColumns_KeepsTheFullOutput(OperationType operation)
    {
        using var db = CreateContext(out var probe);
        await db.OutputItems.BatchDeleteAsync();

        var items = Enumerable.Range(0, 5).Select(i => new OutputItem { Name = "new " + i, Value = i, Description = "d" + i }).ToList();
        var bulkConfig = new BulkConfig { SetOutputIdentity = true, SetOutputNonIdentityColumns = true };
        await RunAsync(db, operation, items, bulkConfig);

        var output = probe.Single();
        var expected = new List<string> { "Id", "Description", "Name", "Value" }; // every column, in the model's order
        if (operation == OperationType.InsertOrUpdateOrDelete)
        {
            expected.Add(Action);
        }

        Assert.Equal(expected, output.Columns);
        Assert.All(output.Rows, a => Assert.Equal(items.Single(b => b.Name == (string)a["Name"]).Description, a["Description"]));
        var inDb = await db.OutputItems.AsNoTracking().ToDictionaryAsync(a => a.Name);
        Assert.All(items, a => Assert.Equal(inDb[a.Name].Id, a.Id));
    }

    [Fact]
    public async Task CalculateStatsWithoutOutputIdentity_KeepsTheFullOutput()
    {
        using var db = CreateContext(out var probe);
        await db.OutputItems.BatchDeleteAsync();

        var items = Enumerable.Range(0, 5).Select(i => new OutputItem { Name = "new " + i, Value = i }).ToList();
        var bulkConfig = new BulkConfig { CalculateStats = true };
        await db.BulkInsertOrUpdateAsync(items, bulkConfig);

        Assert.Equal(new[] { "Id", "Description", "Name", "Value", Action }, probe.Single().Columns);
        Assert.Equal(5, bulkConfig.StatsInfo!.StatsNumberInserted);
    }

    [Fact]
    public void Delete_WithOutputIdentity_OutputsTheDeletedIds()
    {
        using var db = CreateContext(out var probe);
        db.OutputItems.BatchDelete();
        var items = Enumerable.Range(0, 5).Select(i => new OutputItem { Name = "new " + i, Value = i }).ToList();
        db.BulkInsert(items, new BulkConfig { SetOutputIdentity = true });
        probe.Clear();

        var ids = items.Select(a => a.Id).ToList();
        var bulkConfig = new BulkConfig { SetOutputIdentity = true, CalculateStats = true };
        db.BulkDelete(items, bulkConfig);

        var output = probe.Single();
        Assert.Equal(ExpectedColumns(["Id"], OperationType.Delete, calculateStats: true), output.Columns);
        Assert.Equal(ids.OrderBy(a => a), output.Rows.Select(a => (int)a["Id"]).OrderBy(a => a));
        Assert.Equal(ids, items.Select(a => a.Id));
        Assert.Equal(5, bulkConfig.StatsInfo!.StatsNumberDeleted);
        Assert.Empty(db.OutputItems);
    }

    private static List<string> ExpectedColumns(IEnumerable<string> idColumns, OperationType operation, bool calculateStats)
    {
        var columns = idColumns.Where(a => a != "RowVersion").ToList();
        if (calculateStats || operation is OperationType.InsertOrUpdateOrDelete or OperationType.Delete)
        {
            columns.Add(Action);
        }

        columns.Add(Index);
        if (idColumns.Contains("RowVersion"))
        {
            columns.Add("RowVersion"); // added after the table is created, as before
        }

        return columns;
    }

    private static async Task<List<T>> SeedAsync<T>(InsertPerformanceContext db, OperationType operation, Func<int, T> create) where T : class
    {
        var existing = Enumerable.Range(0, operation == OperationType.Insert ? 0 : 4).Select(create).ToList();
        if (existing.Count > 0)
        {
            await db.BulkInsertAsync(existing, new BulkConfig { SetOutputIdentity = true });
        }

        return existing;
    }

    private static async Task RunAsync<T>(InsertPerformanceContext db, OperationType operation, List<T> items, BulkConfig bulkConfig) where T : class
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
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        await transaction.CommitAsync();
    }

    private InsertPerformanceContext CreateContext(out OutputTableProbe probe)
    {
        probe = new OutputTableProbe();
        var options = ContextUtil.GetOptions<InsertPerformanceContext>(DbServerType.SQLServer, [probe], nameof(IdOnlyMergeOutputTests));
        _dbFixture.GetDb(DbServerType.SQLServer).Dispose(); // creates the database once
        return new InsertPerformanceContext(options);
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

            _tables.Add(new OutputTable(columns, rows));
        }
    }

    private sealed record OutputTable(List<string> Columns, List<Dictionary<string, object>> Rows);
}
