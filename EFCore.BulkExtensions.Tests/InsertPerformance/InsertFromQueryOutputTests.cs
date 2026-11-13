using EFCore.BulkExtensions.SqlAdapters;
using EFCore.BulkExtensions.Tests.BulkInsertOrUpdate;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Xunit;

namespace EFCore.BulkExtensions.Tests.InsertPerformance;

/// <summary>
/// InsertFromQuery with an output table: every inserted row also writes a row into a second table, from the inserted row and the query's row,
/// shaped like an import that inserts details and logs which staged row each came from.
/// </summary>
public class InsertFromQueryOutputTests : IClassFixture<InsertFromQueryOutputTests.DatabaseFixture>, IAssemblyFixture<DbAssemblyFixture>
{
    private const int TenantId = 100;

    private readonly DatabaseFixture _dbFixture;

    public InsertFromQueryOutputTests(DatabaseFixture dbFixture)
    {
        _dbFixture = dbFixture;
    }

    public class DatabaseFixture : BulkDbTestsFixture<InsertPerformanceContext>
    {
        protected override string DbName => nameof(InsertFromQueryOutputTests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryInsertedRow_GetsAnOutputRow_WithItsIdentityAndTheQueryRowsValues(bool isAsync)
    {
        using var db = await CreateContextAsync();
        var details = await SeedDetailsAsync(db);
        var copied = details.Where(a => a.TenantId == TenantId && a.Period == 2).ToList();
        var note = "copied";

        var query = db.QueryDetails.Where(d => d.TenantId == TenantId && d.Period == 2);
        int inserted;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            inserted = isAsync
                ? await query.InsertFromQueryAsync(ToTotal(), (d, total) => new QueryTotalSource { TotalId = total.Id, DetailId = d.Id, TotalUpdated = total.Updated, Note = note })
                : query.InsertFromQuery(ToTotal(), (d, total) => new QueryTotalSource { TotalId = total.Id, DetailId = d.Id, TotalUpdated = total.Updated, Note = note });
            await transaction.CommitAsync();
        }

        Assert.Equal(copied.Count, inserted);
        var totals = await db.QueryTotals.AsNoTracking().ToDictionaryAsync(a => a.Id);
        var sources = await db.QueryTotalSources.AsNoTracking().ToListAsync();
        Assert.Equal(copied.Select(a => a.Id).OrderBy(a => a), sources.Select(a => a.DetailId).OrderBy(a => a));
        Assert.Equal(totals.Keys.OrderBy(a => a), sources.Select(a => a.TotalId).OrderBy(a => a));
        var detailsById = copied.ToDictionary(a => a.Id);
        Assert.All(sources, source =>
        {
            var total = totals[source.TotalId];
            var detail = detailsById[source.DetailId];
            Assert.Equal((detail.AccountCode, detail.Description, detail.Amount, detail.Updated), (total.AccountCode, total.Description, total.Amount, total.Updated));
            Assert.Equal((total.Updated, "copied", ReaderStatus.None), (source.TotalUpdated, source.Note, source.Kind));
        });
    }

    /// <remarks>
    /// The target and the output take the same value from the query row, and the output row's own value under a name the target also has.
    /// </remarks>
    [Fact]
    public async Task ValuesTheTargetAndOutputShare_AreEachCopied()
    {
        using var db = await CreateContextAsync();
        var details = await SeedDetailsAsync(db);
        var copied = details.Where(a => a.TenantId == TenantId && a.Period == 1).ToList();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.QueryDetails.Where(d => d.TenantId == TenantId && d.Period == 1).InsertFromQueryAsync(
                d => new QueryTotal
                {
                    TenantId = d.TenantId, AccountCode = d.AccountCode, DepartmentCode = d.DepartmentCode, Description = d.Description,
                    Year = d.Year, Period = d.Period, Amount = d.Amount, Updated = d.Updated, UpdatedBy = (int)d.Id, Status = ReaderStatus.None,
                },
                (d, total) => new QueryTotalSource { TotalId = total.Id, DetailId = d.Id, TotalUpdated = d.Updated.AddDays(1), Note = d.AccountCode + "/" + d.Period });
            await transaction.CommitAsync();
        }

        var sources = await db.QueryTotalSources.AsNoTracking().ToListAsync();
        var detailsById = copied.ToDictionary(a => a.Id);
        Assert.Equal(copied.Count, sources.Count);
        Assert.All(sources, source =>
        {
            var detail = detailsById[source.DetailId];
            Assert.Equal((detail.Updated.AddDays(1), detail.AccountCode + "/1"), (source.TotalUpdated, source.Note));
        });
        Assert.Equal(sources.Select(a => (int)a.DetailId).OrderBy(a => a), (await db.QueryTotals.Select(a => a.UpdatedBy).ToListAsync()).OrderBy(a => a));
    }

    [Fact]
    public async Task OutputTable_HasItsForeignKeysCheckedAndItsTriggersFired()
    {
        using var db = await CreateContextAsync();
        var details = await SeedDetailsAsync(db);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE OR ALTER TRIGGER [QueryTotalSources_Audit] ON [QueryTotalSources] AFTER INSERT AS INSERT INTO [QueryTotalAudits] ([TotalId]) SELECT [TotalId] FROM inserted;");
        try
        {
            int inserted;
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                inserted = await db.QueryDetails.Where(d => d.TenantId == TenantId).InsertFromQueryAsync(ToTotal(), (d, total) => new QueryTotalSource { TotalId = total.Id, DetailId = d.Id });
                await transaction.CommitAsync();
            }

            Assert.Equal(inserted, await db.QueryTotalAudits.CountAsync());
            var trusted = await db.Database.SqlQueryRaw<bool>(
                "SELECT CAST(MAX(CAST(is_not_trusted AS int)) AS bit) AS [Value] FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID('QueryTotalSources')").SingleAsync();
            Assert.False(trusted);

            // a TotalId that is not a total
            await using var failing = await db.Database.BeginTransactionAsync();
            var exception = await Assert.ThrowsAsync<SqlException>(() => db.QueryDetails.Where(d => d.TenantId == TenantId)
                                                                           .InsertFromQueryAsync(ToTotal(), (d, total) => new QueryTotalSource { TotalId = -(int)d.Id, DetailId = d.Id }));
            Assert.Equal(547, exception.Number); // foreign key violation
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [QueryTotalSources_Audit];");
        }
    }

    [Fact]
    public async Task TargetTriggers_FireAndDoNotChangeTheReturnedRowCount()
    {
        using var db = await CreateContextAsync();
        await SeedDetailsAsync(db);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE OR ALTER TRIGGER [QueryTotals_Audit] ON [QueryTotals] AFTER INSERT AS INSERT INTO [QueryTotalAudits] ([TotalId]) SELECT [Id] FROM inserted UNION ALL SELECT [Id] FROM inserted;");
        try
        {
            int inserted = await db.QueryDetails.InsertFromQueryAsync(ToTotal(), (d, total) => new QueryTotalSource { TotalId = total.Id, DetailId = d.Id });

            Assert.Equal(await db.QueryTotals.CountAsync(), inserted);
            Assert.Equal(inserted, await db.QueryTotalSources.CountAsync());
            Assert.Equal(2 * inserted, await db.QueryTotalAudits.CountAsync());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [QueryTotals_Audit];");
        }
    }

    [Fact]
    public async Task OuterTransaction_RolledBack_LeavesNoRowsInEitherTable()
    {
        using var db = await CreateContextAsync();
        await SeedDetailsAsync(db);

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            int inserted = await db.QueryDetails.InsertFromQueryAsync(ToTotal(), (d, total) => new QueryTotalSource { TotalId = total.Id, DetailId = d.Id });
            Assert.Equal(inserted, await db.QueryTotalSources.CountAsync());
            await transaction.RollbackAsync();
        }

        Assert.Equal((0, 0), (await db.QueryTotals.CountAsync(), await db.QueryTotalSources.CountAsync()));
    }

    /// <remarks>Without a transaction of the caller's, a failing output insert must not leave the target rows behind.</remarks>
    [Fact]
    public async Task WithoutATransaction_BothInsertsAreUndoneTogether()
    {
        using var db = await CreateContextAsync();
        await SeedDetailsAsync(db);

        var exception = await Assert.ThrowsAsync<SqlException>(() => db.QueryDetails.InsertFromQueryAsync(ToTotal(), (d, total) => new QueryTotalSource { TotalId = -(int)d.Id, DetailId = d.Id }));

        Assert.Equal(547, exception.Number);
        Assert.Equal((0, 0), (await db.QueryTotals.CountAsync(), await db.QueryTotalSources.CountAsync()));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT @@TRANCOUNT AS [Value]").SingleAsync());

        int inserted = await db.QueryDetails.InsertFromQueryAsync(ToTotal(), (d, total) => new QueryTotalSource { TotalId = total.Id, DetailId = d.Id });
        Assert.Equal(inserted, await db.QueryTotalSources.CountAsync());
    }

    [Fact]
    public async Task UnsupportedOutputSelectors_AreRejectedBeforeAnythingRuns()
    {
        using var db = await CreateContextAsync();
        await ResetAsync(db);
        var details = db.QueryDetails.Where(d => d.TenantId == TenantId);

        // a value computed from the inserted row
        await Assert.ThrowsAsync<NotSupportedException>(() => details.InsertFromQueryAsync(ToTotal(), (d, total) => new QueryTotalSource { TotalId = total.Id + 1 }));
        // a value from both rows
        await Assert.ThrowsAsync<NotSupportedException>(() => details.InsertFromQueryAsync(ToTotal(), (d, total) => new QueryTotalSource { TotalId = total.Id, DetailId = d.Id + total.Id }));
        // the output table's identity column
        await Assert.ThrowsAsync<NotSupportedException>(() => details.InsertFromQueryAsync(ToTotal(), (d, total) => new QueryTotalSource { Id = total.Id, TotalId = total.Id }));
        // a value converter (here an enum's) on a value from a row
        await Assert.ThrowsAsync<NotSupportedException>(() => details.InsertFromQueryAsync(ToTotal(), (d, total) => new QueryTotalSource { TotalId = total.Id, Kind = d.Status }));
        await Assert.ThrowsAsync<NotSupportedException>(() => details.InsertFromQueryAsync(ToTotal(), (d, total) => new QueryTotalSource { TotalId = total.Id, Kind = total.Status }));
        // nothing from either row
        await Assert.ThrowsAsync<NotSupportedException>(() => details.InsertFromQueryAsync(ToTotal(), (d, total) => new QueryTotalSource { Note = "x" }));
        // a value EF Core would compute on the client
        await Assert.ThrowsAsync<NotSupportedException>(() => details.InsertFromQueryAsync(ToTotal(), (d, total) => new QueryTotalSource { TotalId = total.Id, Note = Normalize(d.AccountCode) }));
        // not an object initializer of the output
        await Assert.ThrowsAsync<NotSupportedException>(() => details.InsertFromQueryAsync(ToTotal(), (d, total) => NewSource(total.Id)));

        Assert.Equal((0, 0), (await db.QueryTotals.CountAsync(), await db.QueryTotalSources.CountAsync()));
    }

    [Fact]
    public void OtherProviders_AreNotSupported()
    {
        var options = ContextUtil.GetOptions<SimpleBulkTestsContext>(DbServerType.SQLite, databaseName: nameof(InsertFromQueryOutputTests));
        using var db = new SimpleBulkTestsContext(options);

        var exception = Assert.Throws<NotSupportedException>(() => db.SimpleItems.InsertFromQuery(
            a => new SimpleItem { Name = a.Name, BulkIdentifier = a.BulkIdentifier },
            (a, item) => new SimpleItem { Name = item.Name, BulkIdentifier = a.BulkIdentifier }));
        Assert.Contains("SQL Server", exception.Message);
    }

    private static Expression<Func<QueryDetail, QueryTotal>> ToTotal() => d => new QueryTotal
    {
        TenantId = d.TenantId,
        AccountCode = d.AccountCode,
        DepartmentCode = d.DepartmentCode,
        ProjectCode = d.ProjectCode,
        Description = d.Description,
        Year = d.Year,
        Period = d.Period,
        Amount = d.Amount,
        Updated = d.Updated,
        UpdatedBy = d.UpdatedBy,
        Status = ReaderStatus.Closed,
    };

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    private static QueryTotalSource NewSource(int totalId) => new() { TotalId = totalId };

    private async Task<InsertPerformanceContext> CreateContextAsync()
    {
        _dbFixture.GetDb(DbServerType.SQLServer).Dispose(); // creates the database once
        var db = new InsertPerformanceContext(ContextUtil.GetOptions<InsertPerformanceContext>(DbServerType.SQLServer, databaseName: nameof(InsertFromQueryOutputTests)));
        await Task.CompletedTask;
        return db;
    }

    private static async Task ResetAsync(InsertPerformanceContext db)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [QueryTotalAudits]; DELETE FROM [QueryTotalSources]; DELETE FROM [QueryTotals]; DELETE FROM [QueryDetails];");
    }

    private static async Task<List<QueryDetail>> SeedDetailsAsync(InsertPerformanceContext db)
    {
        await ResetAsync(db);
        var random = new Random(42);
        var details = new List<QueryDetail>();
        for (int i = 0; i < 600; i++)
        {
            int g = random.Next(1000);
            details.Add(new QueryDetail
            {
                TenantId = i % 10 == 9 ? 1 : TenantId,
                AccountCode = (3000 + g % 4).ToString(),
                DepartmentCode = "D" + g % 3,
                ProjectCode = g % 5 == 0 ? null : "P" + g % 2,
                Description = "invoice " + g % 7,
                Year = 2026,
                Period = 1 + g % 3,
                Amount = Math.Round((decimal)(random.NextDouble() * 4000 - 1000), 2),
                Updated = new DateTime(2026, 11, 1).AddMinutes(i),
                UpdatedBy = 1000 + g % 9,
                Status = (ReaderStatus)(g % 2),
                Price = g % 50,
            });
        }

        await db.BulkInsertAsync(details, new BulkConfig { SetOutputIdentity = true });
        return details;
    }
}
