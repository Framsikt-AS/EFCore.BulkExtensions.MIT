using EFCore.BulkExtensions.SqlAdapters;
using EFCore.BulkExtensions.Tests.BulkInsertOrUpdate;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace EFCore.BulkExtensions.Tests.InsertPerformance;

/// <summary>
/// InsertFromQuery: <c>INSERT INTO ... SELECT</c> from an EF Core query, shaped like the grouped totals of an accounting import.
/// </summary>
public class InsertFromQueryTests : IClassFixture<InsertFromQueryTests.DatabaseFixture>, IAssemblyFixture<DbAssemblyFixture>
{
    private const int TenantId = 100;

    private readonly DatabaseFixture _dbFixture;

    public InsertFromQueryTests(DatabaseFixture dbFixture)
    {
        _dbFixture = dbFixture;
    }

    public class DatabaseFixture : BulkDbTestsFixture<InsertPerformanceContext>
    {
        protected override string DbName => nameof(InsertFromQueryTests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupedQuery_InsertsTheGroupsTheQueryReturns(bool isAsync)
    {
        using var db = await CreateContextAsync();
        var details = await SeedDetailsAsync(db);
        var detailIds = details.Where(a => a.Period == 2).Select(a => a.Id).ToList();

        var groups = GroupDetails(db, detailIds);
        var expected = await groups.Select(g => new { g.Key.AccountCode, g.Key.DepartmentCode, g.Key.ProjectCode, g.Key.Description, g.Key.Year, g.Key.Period, g.Amount, g.Updated, g.UpdatedBy })
                                   .ToListAsync();
        Assert.True(expected.Count > 1);

        var tenantId = TenantId;
        Func<IQueryable<GroupRow>, int> insert = query => query.InsertFromQuery(ToTotal(tenantId));
        int inserted = isAsync ? await groups.InsertFromQueryAsync(ToTotal(tenantId), CancellationToken.None) : insert(groups);

        Assert.Equal(expected.Count, inserted);
        var totals = await db.QueryTotals.AsNoTracking().ToListAsync();
        Assert.Equal(
            expected.Select(a => (TenantId, a.AccountCode, a.DepartmentCode, a.ProjectCode, a.Description, a.Year, a.Period, a.Amount, a.Updated, a.UpdatedBy, (string?)"none", ReaderStatus.Closed))
                    .OrderBy(a => a.ToString()),
            totals.Select(a => (a.TenantId, a.AccountCode, a.DepartmentCode, a.ProjectCode, a.Description, a.Year, a.Period, a.Amount, a.Updated, a.UpdatedBy, a.Note, a.Status))
                  .OrderBy(a => a.ToString()));
    }

    [Fact]
    public async Task UnmatchedGroups_InsertsOnlyGroupsWithoutATotal()
    {
        using var db = await CreateContextAsync();
        var details = await SeedDetailsAsync(db);
        var detailIds = details.Select(a => a.Id).ToList();
        var tenantId = TenantId;

        // a total for the first group already exists
        var first = await GroupDetails(db, detailIds).Where(g => g.Key.ProjectCode != null) // a NULL key never matches in the join
                                                     .OrderBy(g => g.Key.AccountCode).ThenBy(g => g.Key.DepartmentCode).ThenBy(g => g.Key.Period).FirstAsync();
        db.QueryTotals.Add(new QueryTotal
        {
            TenantId = tenantId, AccountCode = first.Key.AccountCode, DepartmentCode = first.Key.DepartmentCode, ProjectCode = first.Key.ProjectCode,
            Description = first.Key.Description, Year = first.Key.Year, Period = first.Key.Period, Amount = 1,
        });
        await db.SaveChangesAsync();

        var existing = db.QueryTotals.Where(a => a.TenantId == tenantId);
        var groups = GroupDetails(db, detailIds);
        var unmatched = from g in groups
                        join a in existing
                            on new { g.Key.AccountCode, g.Key.DepartmentCode, g.Key.ProjectCode, g.Key.Description, g.Key.Year, g.Key.Period }
                            equals new { a.AccountCode, a.DepartmentCode, a.ProjectCode, a.Description, a.Year, a.Period } into matches
                        from match in matches.DefaultIfEmpty()
                        where match == null
                        select g;

        int groupCount = await groups.CountAsync();
        int inserted = await unmatched.InsertFromQueryAsync(ToTotal(tenantId));

        Assert.Equal(groupCount - 1, inserted);
        Assert.Equal(groupCount, await db.QueryTotals.CountAsync());
        var firstTotals = await db.QueryTotals.Where(a => a.AccountCode == first.Key.AccountCode && a.DepartmentCode == first.Key.DepartmentCode && a.ProjectCode == first.Key.ProjectCode
                                                           && a.Description == first.Key.Description && a.Year == first.Key.Year && a.Period == first.Key.Period)
                                              .Select(a => a.Amount).ToListAsync();
        Assert.Equal(new[] { 1m }, firstTotals);
    }

    [Fact]
    public async Task Parameters_FromTheQueryAndTheSelectorAreSent()
    {
        using var db = await CreateContextAsync();
        var details = await SeedDetailsAsync(db);

        var detailIds = details.Where(a => a.AccountCode == "3000").Select(a => a.Id).ToList(); // an OPENJSON parameter
        var minimumAmount = -1000m;
        var note = "imported";
        var updatedBy = 77;
        var status = ReaderStatus.Active; // an enum (with a value converter) assigned a value that does not depend on the row

        int inserted = await db.QueryDetails
#if V8
                               .Where(d => d.TenantId == TenantId && detailIds.Contains(d.Id) && d.Amount > minimumAmount) // EF Core 8 sends the list as one OPENJSON parameter by default
#else
                               .Where(d => d.TenantId == TenantId && EF.Parameter(detailIds).Contains(d.Id) && d.Amount > minimumAmount)
#endif
                               .InsertFromQueryAsync(d => new QueryTotal
                               {
                                   TenantId = d.TenantId,
                                   AccountCode = d.AccountCode,
                                   DepartmentCode = d.DepartmentCode,
                                   Description = "copy of " + d.Description,
                                   Year = d.Year,
                                   Period = d.Period,
                                   Amount = d.Amount * 2,
                                   Updated = new DateTime(2026, 11, 11),
                                   UpdatedBy = updatedBy,
                                   Note = note,
                                   Status = status,
                               });

        var expected = details.Where(a => a.TenantId == TenantId && a.AccountCode == "3000" && a.Amount > minimumAmount).ToList();
        Assert.Equal(expected.Count, inserted);
        var totals = await db.QueryTotals.AsNoTracking().ToListAsync();
        Assert.Equal(expected.Select(a => ("copy of " + a.Description, a.Amount * 2)).OrderBy(a => a), totals.Select(a => (a.Description, a.Amount)).OrderBy(a => a));
        Assert.All(totals, a => Assert.Equal((new DateTime(2026, 11, 11), 77, "imported", ReaderStatus.Active), (a.Updated, a.UpdatedBy, a.Note, a.Status)));
    }

    [Fact]
    public async Task OuterTransaction_RolledBack_LeavesNoRows()
    {
        using var db = await CreateContextAsync();
        var details = await SeedDetailsAsync(db);
        var detailIds = details.Select(a => a.Id).ToList();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            int inserted = await GroupDetails(db, detailIds).InsertFromQueryAsync(ToTotal(TenantId));
            Assert.True(inserted > 0);
            Assert.Equal(inserted, await db.QueryTotals.CountAsync()); // visible inside the transaction
            await transaction.RollbackAsync();
        }

        Assert.Equal(0, await db.QueryTotals.CountAsync());
    }

    [Fact]
    public async Task CommandTimeout_IsUsed()
    {
        using var db = await CreateContextAsync();
        var details = await SeedDetailsAsync(db);
        var detailIds = details.Select(a => a.Id).ToList();

        // Another connection holds the target table, so the insert waits until the context's command timeout
        await using var blocker = new SqlConnection(db.Database.GetConnectionString());
        await blocker.OpenAsync();
        await using var blockerTransaction = (SqlTransaction)await blocker.BeginTransactionAsync();
        await using (var command = new SqlCommand("SELECT COUNT(*) FROM [QueryTotals] WITH (TABLOCKX, HOLDLOCK)", blocker, blockerTransaction))
        {
            await command.ExecuteScalarAsync();
        }

        db.Database.SetCommandTimeout(2);
        var started = DateTime.UtcNow;
        var exception = await Assert.ThrowsAsync<SqlException>(() => GroupDetails(db, detailIds).InsertFromQueryAsync(ToTotal(TenantId)));
        Assert.Equal(-2, exception.Number); // timeout
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(30));

        await blockerTransaction.RollbackAsync();
        db.Database.SetCommandTimeout(30);
        Assert.True(await GroupDetails(db, detailIds).InsertFromQueryAsync(ToTotal(TenantId)) > 0);
    }

    [Fact]
    public async Task Grouping_UsesTheDatabaseCollation()
    {
        using var db = await CreateContextAsync();
        await ResetAsync(db);
        var details = new[] { "Rent", "RENT", "rent ", "Fees" }.Select(description => new QueryDetail
        {
            TenantId = TenantId, AccountCode = "1000", DepartmentCode = "A", Description = description, Year = 2026, Period = 1, Amount = 10, Updated = new DateTime(2026, 1, 1),
        }).ToList();
        await db.BulkInsertAsync(details, new BulkConfig { SetOutputIdentity = true });

        // SQL_Latin1_General_CP1_CI_AS: case and trailing spaces do not make a group of their own
        int inserted = await GroupDetails(db, details.Select(a => a.Id).ToList()).InsertFromQueryAsync(ToTotal(TenantId));

        Assert.Equal(2, inserted);
        var totals = await db.QueryTotals.AsNoTracking().OrderBy(a => a.Amount).ToListAsync();
        Assert.Equal(new[] { 10m, 30m }, totals.Select(a => a.Amount));
    }

    [Fact]
    public async Task Triggers_DoNotChangeTheReturnedRowCount()
    {
        using var db = await CreateContextAsync();
        var details = await SeedDetailsAsync(db);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE OR ALTER TRIGGER [QueryTotals_Audit] ON [QueryTotals] AFTER INSERT AS INSERT INTO [QueryTotalAudits] ([TotalId]) SELECT [Id] FROM inserted UNION ALL SELECT [Id] FROM inserted;");
        try
        {
            int inserted = await GroupDetails(db, details.Select(a => a.Id).ToList()).InsertFromQueryAsync(ToTotal(TenantId));

            Assert.Equal(await db.QueryTotals.CountAsync(), inserted);
            Assert.Equal(2 * inserted, await db.QueryTotalAudits.CountAsync());
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [QueryTotals_Audit];");
        }
    }

    [Fact]
    public async Task SameValueForTwoColumns_AndServerFunctions_AreInserted()
    {
        using var db = await CreateContextAsync();
        var details = await SeedDetailsAsync(db);

        int inserted = await db.QueryDetails.Where(d => d.Period == 1).InsertFromQueryAsync(d => new QueryTotal
        {
            TenantId = d.TenantId,
            AccountCode = d.AccountCode,
            DepartmentCode = d.AccountCode, // EF Core projects this expression once
            Description = d.Description,
            Year = d.Year,
            Period = d.Period,
            Amount = d.Amount,
            Updated = DateTime.UtcNow, // translated to the server's time, not evaluated once here
            UpdatedBy = d.UpdatedBy,
            Status = ReaderStatus.None,
        });

        Assert.Equal(details.Count(a => a.Period == 1), inserted);
        var totals = await db.QueryTotals.AsNoTracking().ToListAsync();
        Assert.All(totals, a => Assert.Equal(a.AccountCode, a.DepartmentCode));
        Assert.All(totals, a => Assert.InRange(a.Updated, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(10)));
    }

    [Fact]
    public async Task UnsupportedSelectors_AreRejectedBeforeAnythingRuns()
    {
        using var db = await CreateContextAsync();
        await ResetAsync(db);
        var details = db.QueryDetails.Where(d => d.TenantId == TenantId);

        // the identity column
        await Assert.ThrowsAsync<NotSupportedException>(() => details.InsertFromQueryAsync(d => new QueryTotal { Id = (int)d.Id, AccountCode = d.AccountCode }));
        // a value EF Core would compute on the client
        await Assert.ThrowsAsync<NotSupportedException>(() => details.InsertFromQueryAsync(d => new QueryTotal { AccountCode = Normalize(d.AccountCode) }));
        // a value converter (here an enum's) on a value from the query
        await Assert.ThrowsAsync<NotSupportedException>(() => details.InsertFromQueryAsync(d => new QueryTotal { AccountCode = d.AccountCode, Status = d.Status }));
        // not an object initializer of the target
        await Assert.ThrowsAsync<NotSupportedException>(() => details.InsertFromQueryAsync(d => NewTotal(d.AccountCode)));
        // an order the insert would not keep
        await Assert.ThrowsAsync<NotSupportedException>(() => details.OrderBy(d => d.Amount).InsertFromQueryAsync(d => new QueryTotal { AccountCode = d.AccountCode }));
        // nothing from the query
        await Assert.ThrowsAsync<NotSupportedException>(() => details.InsertFromQueryAsync(d => new QueryTotal { AccountCode = "x" }));
        // not an EF Core query
        await Assert.ThrowsAsync<ArgumentException>(() => new List<QueryDetail>().AsQueryable().InsertFromQueryAsync(d => new QueryTotal { AccountCode = d.AccountCode }));

        Assert.Equal(0, await db.QueryTotals.CountAsync());
    }

    [Fact]
    public async Task ConvertedSourceValues_AreRejected_ButMayFilter()
    {
        using var db = await CreateContextAsync();
        var details = await SeedDetailsAsync(db);

        // Price is stored as cents: the SQL value is not the value EF Core reads
        await Assert.ThrowsAsync<NotSupportedException>(() => db.QueryDetails.InsertFromQueryAsync(d => new QueryTotal { AccountCode = d.AccountCode, Amount = d.Price }));
        await Assert.ThrowsAsync<NotSupportedException>(() => db.QueryDetails.GroupBy(d => new { d.AccountCode, d.Price })
                                                                .InsertFromQueryAsync(g => new QueryTotal { AccountCode = g.Key.AccountCode, Amount = g.Count() }));
        await Assert.ThrowsAsync<NotSupportedException>(() => db.QueryDetails.GroupBy(d => d.AccountCode)
                                                                .InsertFromQueryAsync(g => new QueryTotal { AccountCode = g.Key, Amount = g.Sum(d => d.Price) }));
        await Assert.ThrowsAsync<NotSupportedException>(() => db.QueryDetails.Select(d => new { d.AccountCode, Value = d.Price })
                                                                .InsertFromQueryAsync(d => new QueryTotal { AccountCode = d.AccountCode, Amount = d.Value }));
        Assert.Equal(0, await db.QueryTotals.CountAsync());

        // Filtering on it, and an enum's default number, are fine
        int inserted = await db.QueryDetails.Where(d => d.Price > 10 && d.Period == 1).OrderBy(d => d.Price).Take(1000)
                               .InsertFromQueryAsync(d => new QueryTotal
                               {
                                   TenantId = d.TenantId, AccountCode = d.AccountCode, DepartmentCode = d.DepartmentCode, Description = d.Description,
                                   Year = d.Year, Period = d.Period, Amount = d.Amount, Updated = d.Updated, UpdatedBy = (int)d.Status, Status = ReaderStatus.None,
                               });

        Assert.Equal(details.Count(a => a.Price > 10 && a.Period == 1), inserted);
        Assert.Equal(details.Where(a => a.Price > 10 && a.Period == 1).Select(a => (int)a.Status).OrderBy(a => a),
                     (await db.QueryTotals.Select(a => a.UpdatedBy).ToListAsync()).OrderBy(a => a));
    }

    [Fact]
    public void OtherProviders_AreNotSupported()
    {
        var options = ContextUtil.GetOptions<SimpleBulkTestsContext>(DbServerType.SQLite, databaseName: nameof(InsertFromQueryTests));
        using var db = new SimpleBulkTestsContext(options);

        var exception = Assert.Throws<NotSupportedException>(() => db.SimpleItems.InsertFromQuery(a => new SimpleItem { Name = a.Name, BulkIdentifier = a.BulkIdentifier }));
        Assert.Contains("SQL Server", exception.Message);
    }

    public class GroupRow
    {
        public GroupKey Key { get; set; } = null!;
        public decimal Amount { get; set; }
        public DateTime Updated { get; set; }
        public int UpdatedBy { get; set; }
    }

    public class GroupKey
    {
        public string AccountCode { get; set; } = "";
        public string DepartmentCode { get; set; } = "";
        public string? ProjectCode { get; set; }
        public string Description { get; set; } = "";
        public int Year { get; set; }
        public int Period { get; set; }
    }

    private static IQueryable<GroupRow> GroupDetails(InsertPerformanceContext db, List<long> detailIds) =>
        db.QueryDetails
#if V8
          .Where(d => d.TenantId == TenantId && detailIds.Contains(d.Id)) // EF Core 8 sends the list as one OPENJSON parameter by default
#else
          .Where(d => d.TenantId == TenantId && EF.Parameter(detailIds).Contains(d.Id))
#endif
          .GroupBy(d => new GroupKey
          {
              AccountCode = d.AccountCode,
              DepartmentCode = d.DepartmentCode,
              ProjectCode = d.ProjectCode,
              Description = d.Description,
              Year = d.Year,
              Period = d.Period,
          })
          .Select(g => new GroupRow
          {
              Key = g.Key,
              Amount = g.Sum(d => d.Amount),
              Updated = g.Max(d => d.Updated),
              UpdatedBy = g.Max(d => d.UpdatedBy),
          });

    private static System.Linq.Expressions.Expression<Func<GroupRow, QueryTotal>> ToTotal(int tenantId) => g => new QueryTotal
    {
        TenantId = tenantId,
        AccountCode = g.Key.AccountCode,
        DepartmentCode = g.Key.DepartmentCode,
        ProjectCode = g.Key.ProjectCode,
        Description = g.Key.Description,
        Year = g.Key.Year,
        Period = g.Key.Period,
        Amount = g.Amount,
        Updated = g.Updated,
        UpdatedBy = g.UpdatedBy,
        Status = ReaderStatus.Closed,
    };

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    private static QueryTotal NewTotal(string accountCode) => new() { AccountCode = accountCode };

    private async Task<InsertPerformanceContext> CreateContextAsync()
    {
        _dbFixture.GetDb(DbServerType.SQLServer).Dispose(); // creates the database once
        var db = new InsertPerformanceContext(ContextUtil.GetOptions<InsertPerformanceContext>(DbServerType.SQLServer, databaseName: nameof(InsertFromQueryTests)));
        await Task.CompletedTask;
        return db;
    }

    private static async Task ResetAsync(InsertPerformanceContext db)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [QueryTotalAudits]; DELETE FROM [QueryTotals]; DELETE FROM [QueryDetails];");
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
                TenantId = i % 10 == 9 ? 1 : TenantId, // some of another tenant
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
