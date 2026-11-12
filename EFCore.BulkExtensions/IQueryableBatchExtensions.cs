using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace EFCore.BulkExtensions;

/// <summary>
/// Contains a list of Batch IQuerable extensions
/// </summary>
public static class IQueryableBatchExtensions
{
    // Delete methods
    #region BatchDelete
    /// <summary>
    /// Extension method to batch delete data
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    public static int BatchDelete(this IQueryable query)
    {
        var (context, sql, sqlParameters) = GetBatchDeleteArguments(query);
        return context.Database.ExecuteSqlRaw(sql, sqlParameters);
    }

    /// <summary>
    /// Extension method to batch delete data
    /// </summary>
    /// <param name="query"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task<int> BatchDeleteAsync(this IQueryable query, CancellationToken cancellationToken = default)
    {
        var (context, sql, sqlParameters) = GetBatchDeleteArguments(query);
        return await context.Database.ExecuteSqlRawAsync(sql, sqlParameters, cancellationToken).ConfigureAwait(false);
    }

    private static (DbContext, string, List<object>) GetBatchDeleteArguments(IQueryable query)
    {
        var context = BatchUtil.GetDbContext(query);
        if (context is null)
        {
            throw new ArgumentException("Unable to determine context");
        }
        var (sql, sqlParameters) = BatchUtil.GetSqlDelete(query, context);
        return (context, sql, sqlParameters);
    }
    #endregion

    // Update methods
    #region BatchUpdate
    /// <summary>
    /// Extension method to batch update data
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="query"></param>
    /// <param name="updateValues"></param>
    /// <param name="updateColumns"></param>
    /// <returns></returns>
    public static int BatchUpdate<T>(this IQueryable<T> query, object updateValues, List<string> ?updateColumns = null) where T : class
    {
        var (context, sql, sqlParameters) = GetBatchUpdateArguments(query, updateValues, updateColumns);
        return context.Database.ExecuteSqlRaw(sql, sqlParameters);
    }

    /// <summary>
    /// Extension method to batch update data
    /// </summary>
    /// <param name="query"></param>
    /// <param name="updateValues"></param>
    /// <param name="updateColumns"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task<int> BatchUpdateAsync(this IQueryable query, object updateValues, List<string>? updateColumns = null, CancellationToken cancellationToken = default)
    {
        var (context, sql, sqlParameters) = GetBatchUpdateArguments((IQueryable<object>)query, updateValues, updateColumns);
        return await context.Database.ExecuteSqlRawAsync(sql, sqlParameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extension method to batch update data
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="query"></param>
    /// <param name="updateExpression"></param>
    /// <param name="type"></param>
    /// <returns></returns>
    public static int BatchUpdate<T>(this IQueryable<T> query, Expression<Func<T, T>> updateExpression, Type? type = null) where T : class
    {
        var (context, sql, sqlParameters) = GetBatchUpdateArguments(query, updateExpression: updateExpression, type: type);
        return context.Database.ExecuteSqlRaw(sql, sqlParameters);
    }

    /// <summary>
    /// Extension method to batch update data
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="query"></param>
    /// <param name="updateExpression"></param>
    /// <param name="type"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static async Task<int> BatchUpdateAsync<T>(this IQueryable<T> query, Expression<Func<T, T>> updateExpression, Type? type = null, CancellationToken cancellationToken = default) where T : class
    {
        var (context, sql, sqlParameters) = GetBatchUpdateArguments(query, updateExpression: updateExpression, type: type);
        return await context.Database.ExecuteSqlRawAsync(sql, sqlParameters, cancellationToken).ConfigureAwait(false);
    }

    private static (DbContext, string, List<object>) GetBatchUpdateArguments<T>(IQueryable<T> query, object? updateValues = null, List<string>? updateColumns = null, Expression<Func<T, T>>? updateExpression = null, Type? type = null) where T : class
    {
        type ??= typeof(T);
        var context = BatchUtil.GetDbContext(query);
        if (context is null)
        {
            throw new ArgumentException("Unable to determine context");
        }
        var (sql, sqlParameters) = updateExpression == null ? BatchUtil.GetSqlUpdate(query, context, type, updateValues, updateColumns)
                                                            : BatchUtil.GetSqlUpdate(query, context, type, updateExpression);
        return (context, sql, sqlParameters);
    }
    #endregion

    // Insert methods
    #region InsertFromQuery
    /// <summary>
    /// Inserts a row into <typeparamref name="TTarget"/>'s table for every row of <paramref name="source"/>, on the server, with one
    /// <c>INSERT INTO ... SELECT</c> statement. SQL Server only.
    /// </summary>
    /// <param name="source">The query whose rows are inserted.</param>
    /// <param name="selector">
    /// Maps a row of <paramref name="source"/> to the inserted row with an object initializer, like
    /// <c>g => new Total { TenantId = tenantId, Amount = g.Sum(x => x.Amount) }</c>. Only the assigned properties are inserted;
    /// the others get their column's default. Values that do not depend on the row are sent once as parameters; the others must translate to SQL.
    /// </param>
    /// <returns>The number of rows inserted.</returns>
    /// <remarks>
    /// The target table and column names come from <typeparamref name="TTarget"/>'s mapping. The identity column is generated by the database,
    /// so it cannot be assigned, nor can computed or row version columns. The statement runs in the context's current transaction, if any,
    /// with the context's command timeout, and the query's own ordering is not kept. Properties with a value converter (enums too) can only be
    /// assigned values that do not depend on the row, and a value read from a property with a value converter (other than an enum's default)
    /// is refused, since SQL Server would copy it as stored. Owned and inherited entity types are not supported.
    /// </remarks>
    /// <exception cref="NotSupportedException">The provider is not SQL Server, or the selector or target type is not supported.</exception>
    public static int InsertFromQuery<TSource, TTarget>(this IQueryable<TSource> source, Expression<Func<TSource, TTarget>> selector) where TTarget : class
    {
        var (context, sql, parameters, rowCount) = InsertFromQueryUtil.GetSqlInsert(source, selector);
        context.Database.ExecuteSqlRaw(sql, parameters);
        return (int)rowCount.Value!;
    }

    /// <summary>
    /// Inserts a row into <typeparamref name="TTarget"/>'s table for every row of <paramref name="source"/>, on the server, with one
    /// <c>INSERT INTO ... SELECT</c> statement. SQL Server only. See <see cref="InsertFromQuery{TSource, TTarget}"/>.
    /// </summary>
    /// <returns>The number of rows inserted.</returns>
    public static async Task<int> InsertFromQueryAsync<TSource, TTarget>(
        this IQueryable<TSource> source,
        Expression<Func<TSource, TTarget>> selector,
        CancellationToken cancellationToken = default
    )
        where TTarget : class
    {
        var (context, sql, parameters, rowCount) = InsertFromQueryUtil.GetSqlInsert(source, selector);
        await context.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        return (int)rowCount.Value!;
    }
    #endregion
}
