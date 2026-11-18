using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using EFCore.BulkExtensions.SqlAdapters;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace EFCore.BulkExtensions;

/// <summary>
/// Builds the <c>INSERT INTO ... SELECT</c> of <see cref="IQueryableBatchExtensions.InsertFromQuery{TSource, TTarget}"/>.
/// </summary>
/// <remarks>
/// The selector's member assignments that do not depend on the query's rows (constants, captured variables) are evaluated once
/// and sent as parameters typed by the target property's mapping. The others become a projection of the source query that EF Core
/// translates, which must translate to SQL entirely. EF Core names each of that projection's columns after its member, so the
/// INSERT selects them by name, and a column EF Core did not produce fails the statement rather than shifting values.
/// </remarks>
internal static class InsertFromQueryUtil
{
    private const string SqlServerProviderName = "Microsoft.EntityFrameworkCore.SqlServer";
    private const string ParameterNamePrefix = "@__InsertFromQuery_p";
    private const string RowCountParameterName = "@__InsertFromQuery_rowCount";
    private const string SourceAlias = "__InsertFromQuery_source";

    internal static (DbContext Context, string Sql, List<object> Parameters, DbParameter RowCount) GetSqlInsert<TSource, TTarget>(
        IQueryable<TSource> source,
        Expression<Func<TSource, TTarget>> selector
    )
        where TTarget : class
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        if (selector == null)
            throw new ArgumentNullException(nameof(selector));

        if (source.Provider is not IAsyncQueryProvider)
            throw new ArgumentException("The source must be an Entity Framework Core query.", nameof(source));

        var context = BatchUtil.GetDbContext(source) ?? throw new ArgumentException("Unable to determine context");
        if (context.Database.ProviderName != SqlServerProviderName)
            throw new NotSupportedException($"InsertFromQuery is only supported on SQL Server, not with the provider '{context.Database.ProviderName}'.");

        var target = MappedTable.For(context, typeof(TTarget));
        var parameter = selector.Parameters[0];
        var evaluatableExpressionFilter = context.GetService<IEvaluatableExpressionFilter>();
        var dbServer = SqlAdaptersMapping.DbServer(context);

        var insertColumns = new List<string>();
        var selectValues = new List<string>();
        var parameters = new List<object>();
        var serverBindings = new List<MemberAssignment>();

        foreach (var (property, column, assignment) in target.Assignments(selector.Body, dbServer, "selector"))
        {
            insertColumns.Add(column);

            if (IsEvaluatedOnClient(assignment.Expression, parameter, evaluatableExpressionFilter, context.Model))
            {
                var parameterName = ParameterNamePrefix + parameters.Count;
                parameters.Add(CreateParameter(property, parameterName, Evaluate(assignment.Expression)));
                selectValues.Add(parameterName);
                continue;
            }

            if (property.GetTypeMapping().Converter != null)
            {
                throw new NotSupportedException(
                    $"'{target.ClrType.Name}.{property.Name}' has a value converter (enums have one too), and its value comes from the query: the query's SQL value " +
                    "would be inserted without the target's conversion. Assign it a value that does not depend on the query, or insert these rows another way.");
            }

            // EF Core projects an SQL expression once, under the first member's name, so equal values share that column.
            // A value that does not match here but still translates to the same SQL makes SQL Server reject the statement
            // (invalid column name) before it writes anything.
            var canonical = SameSqlCanonicalizer.Canonicalize(assignment.Expression);
            var sameExpression = serverBindings.FirstOrDefault(a => ExpressionEqualityComparer.Instance.Equals(SameSqlCanonicalizer.Canonicalize(a.Expression), canonical));
            if (sameExpression == null)
            {
                serverBindings.Add(assignment);
                sameExpression = assignment;
            }

            selectValues.Add($"{Delimit(SourceAlias)}.{Delimit(sameExpression.Member.Name)}");
        }

        if (serverBindings.Count == 0)
            throw new NotSupportedException("At least one of the selector's values must come from the query.");

        if (EndsWithOrdering(source.Expression))
        {
            throw new NotSupportedException("InsertFromQuery does not keep the query's order; remove the OrderBy/ThenBy, or limit it with Take or Skip.");
        }

        var convertedProperty = ConvertedValueFinder.Find(context.Model, [source.Expression, .. serverBindings.Select(a => a.Expression)]);
        if (convertedProperty != null)
        {
            throw new NotSupportedException(
                $"'{convertedProperty.DeclaringType.ClrType.Name}.{convertedProperty.Name}' has a value converter and its value can reach the inserted rows: " +
                "SQL Server would insert the value as stored, without the conversion EF Core applies when it reads it. Insert these rows another way.");
        }

        var projection = Expression.Lambda<Func<TSource, TTarget>>(Expression.MemberInit(((MemberInitExpression)selector.Body).NewExpression, serverBindings), parameter);
        var projectedQuery = source.Select(projection);
        EnsureTranslatedToSql(projectedQuery);

        var (querySql, queryParameters) = projectedQuery.ToParametrizedSql();
        parameters.AddRange(queryParameters);

        var rowCount = new SqlParameter(RowCountParameterName, SqlDbType.Int) { Direction = ParameterDirection.Output };
        parameters.Add(rowCount);

        var sql = $"INSERT INTO {target.FullTableName} ({string.Join(", ", insertColumns)})" + Environment.NewLine
                  + $"SELECT {string.Join(", ", selectValues)}" + Environment.NewLine
                  + "FROM (" + Environment.NewLine
                  + querySql + Environment.NewLine
                  + $") AS {Delimit(SourceAlias)};" + Environment.NewLine
                  + $"SET {RowCountParameterName} = @@ROWCOUNT;";

        // ExecuteSqlRaw formats the SQL with string.Format
        sql = sql.Replace("{", "{{").Replace("}", "}}");

        return (context, sql, parameters, rowCount);
    }

    /// <summary>An entity type that InsertFromQuery can write to: its table, and the columns an object initializer of it assigns.</summary>
    internal sealed record MappedTable(Type ClrType, IEntityType EntityType, StoreObjectIdentifier StoreObject, string FullTableName)
    {
        public static MappedTable For(DbContext context, Type clrType)
        {
            var entityType = context.Model.FindEntityType(clrType)
                             ?? throw new InvalidOperationException($"'{clrType.Name}' is not an entity type of {context.GetType().Name}.");
            if (entityType.IsOwned() || entityType.BaseType != null || entityType.GetDirectlyDerivedTypes().Any())
                throw new NotSupportedException($"InsertFromQuery does not support owned entity types or inheritance hierarchies ('{clrType.Name}').");

            var tableName = entityType.GetTableName() ?? throw new NotSupportedException($"'{clrType.Name}' is not mapped to a table.");
            var schemaName = entityType.GetSchema();
            return new MappedTable(clrType, entityType, StoreObjectIdentifier.Table(tableName, schemaName), $"{Delimit(schemaName ?? "dbo")}.{Delimit(tableName)}");
        }

        /// <summary>The assignments of an object initializer creating this table's entity, each with its property and delimited column.</summary>
        public IEnumerable<(IProperty Property, string Column, MemberAssignment Assignment)> Assignments(Expression body, IDbServer dbServer, string selectorName)
        {
            if (body is not MemberInitExpression memberInit || memberInit.NewExpression.Arguments.Count != 0 || memberInit.Type != ClrType)
                throw new NotSupportedException($"The {selectorName} must create the {ClrType.Name} with an object initializer, like 'x => new {ClrType.Name} {{ Column = x.Value, ... }}'.");
            if (memberInit.Bindings.Count == 0)
                throw new NotSupportedException($"The {selectorName} must assign at least one property.");

            var assigned = new HashSet<IProperty>();
            foreach (var binding in memberInit.Bindings)
            {
                if (binding is not MemberAssignment assignment)
                    throw new NotSupportedException($"'{ClrType.Name}.{binding.Member.Name}' must be assigned a value; nested initializers are not supported.");

                var property = EntityType.FindProperty(assignment.Member.Name)
                               ?? throw new NotSupportedException($"'{ClrType.Name}.{assignment.Member.Name}' is not a mapped property.");
                var column = property.GetColumnName(StoreObject)
                             ?? throw new NotSupportedException($"'{ClrType.Name}.{property.Name}' is not mapped to a column of {FullTableName}.");

                if (!assigned.Add(property))
                    throw new NotSupportedException($"'{ClrType.Name}.{property.Name}' is assigned more than once.");
                if (dbServer.PropertyHasIdentity(property))
                    throw new NotSupportedException($"'{ClrType.Name}.{property.Name}' is an identity column, which the database generates; remove it from the {selectorName}.");
                if (property.GetComputedColumnSql() != null)
                    throw new NotSupportedException($"'{ClrType.Name}.{property.Name}' is a computed column; remove it from the {selectorName}.");
                if (property.IsConcurrencyToken && property.ValueGenerated == ValueGenerated.OnAddOrUpdate)
                    throw new NotSupportedException($"'{ClrType.Name}.{property.Name}' is a row version, which the database generates; remove it from the {selectorName}.");

                yield return (property, Delimit(column), assignment);
            }
        }
    }

    private static string Delimit(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    /// <summary>Operators after which EF Core keeps an ordering in the SQL.</summary>
    private static readonly HashSet<string> OrderPreservingMethods =
    [
        nameof(Queryable.Where), nameof(Queryable.Select),
        nameof(EntityFrameworkQueryableExtensions.AsNoTracking), nameof(EntityFrameworkQueryableExtensions.AsNoTrackingWithIdentityResolution),
        nameof(EntityFrameworkQueryableExtensions.AsTracking), nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters),
        nameof(EntityFrameworkQueryableExtensions.IgnoreAutoIncludes), nameof(EntityFrameworkQueryableExtensions.TagWith),
        nameof(EntityFrameworkQueryableExtensions.TagWithCallSite), nameof(EntityFrameworkQueryableExtensions.Include),
        nameof(EntityFrameworkQueryableExtensions.ThenInclude), nameof(RelationalQueryableExtensions.AsSplitQuery),
        nameof(RelationalQueryableExtensions.AsSingleQuery),
    ];

    /// <summary>
    /// Whether the query's SQL ends with an ORDER BY that no TOP or OFFSET limits, which SQL Server does not allow in the derived table
    /// (error 1033): an OrderBy/ThenBy reached from the outermost call through operators that keep the order. Take, Skip and any other
    /// operator end the search.
    /// </summary>
    internal static bool EndsWithOrdering(Expression expression)
    {
        while (expression is MethodCallExpression { Arguments.Count: > 0 } call)
        {
            if (call.Method.DeclaringType == typeof(Queryable)
                && call.Method.Name is nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending) or nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending))
            {
                return true;
            }

            if (!OrderPreservingMethods.Contains(call.Method.Name))
            {
                return false;
            }

            expression = call.Arguments[0];
        }

        return false;
    }

    /// <summary>
    /// Rewrites forms that EF Core translates to the same SQL into one form, so equal values are recognised before EF Core merges them:
    /// <c>EF.Property(x, "Name")</c> becomes <c>x.Name</c> (converted back to the type the call had, so the expressions around it still fit),
    /// and conversions that only add or remove <see cref="Nullable{T}"/> are dropped at the top of the value, where nothing is rebuilt around them.
    /// Used only for comparing: if the rewrite fails, the expression is compared as written.
    /// </summary>
    internal sealed class SameSqlCanonicalizer : ExpressionVisitor
    {
        private static readonly SameSqlCanonicalizer Instance = new();

        public static Expression Canonicalize(Expression expression)
        {
            try
            {
                var canonical = Instance.Visit(expression);
                while (canonical is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
                       && (Nullable.GetUnderlyingType(convert.Type) == convert.Operand.Type || Nullable.GetUnderlyingType(convert.Operand.Type) == convert.Type))
                {
                    canonical = convert.Operand;
                }

                return canonical;
            }
            catch (Exception)
            {
                return expression;
            }
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(EF) && node.Method.Name == nameof(EF.Property)
                && node.Arguments[1] is ConstantExpression { Value: string propertyName }
                && node.Arguments[0].Type.GetProperty(propertyName) is { } property)
            {
                Expression member = Expression.Property(Visit(node.Arguments[0]), property);
                return member.Type == node.Type ? member : Expression.Convert(member, node.Type);
            }

            return base.VisitMethodCall(node);
        }
    }

    /// <summary>
    /// Whether EF Core would evaluate the expression once on the client rather than translate it: it does not use the selector's
    /// parameter, contains no query, and every node passes the provider's <see cref="IEvaluatableExpressionFilter"/> (which keeps, for
    /// example, <c>DateTime.UtcNow</c> and <c>Guid.NewGuid()</c> on the server).
    /// </summary>
    internal static bool IsEvaluatedOnClient(Expression expression, ParameterExpression parameter, IEvaluatableExpressionFilter filter, IModel model)
    {
        var finder = new ClientEvaluationFinder(parameter, filter, model);
        finder.Visit(expression);
        return finder.IsEvaluatable;
    }

    internal static object? Evaluate(Expression expression) =>
        Expression.Lambda<Func<object?>>(Expression.Convert(expression, typeof(object))).Compile(preferInterpretation: true)();

    internal static DbParameter CreateParameter(IProperty property, string name, object? value)
    {
        using var command = new SqlCommand();
        return property.GetRelationalTypeMapping().CreateParameter(command, name, value, property.IsNullable);
    }

    /// <summary>
    /// EF Core evaluates on the client whatever part of a final projection it cannot translate, and leaves it out of the SQL.
    /// It refuses a set operation over such a projection, so compiling (not running) one shows whether it translated entirely.
    /// </summary>
    internal static void EnsureTranslatedToSql(IQueryable projectedQuery)
    {
        var check = Expression.Call(typeof(Queryable), nameof(Queryable.Concat), [projectedQuery.ElementType], projectedQuery.Expression, projectedQuery.Expression);
        try
        {
            projectedQuery.Provider.Execute<IEnumerable>(check);
        }
        catch (InvalidOperationException ex)
        {
            throw new NotSupportedException("InsertFromQuery needs the selector's values that come from the query to be translated to SQL entirely: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Finds an entity property with a value converter whose value can reach the inserted rows. The SQL would carry its stored
    /// (provider) value, where reading the rows and inserting them would carry the converted one. Lambdas that only filter or
    /// order (Where, OrderBy, Any, join keys, ...) are skipped. EF Core's default enum-to-number converter is allowed: an enum
    /// target needs a converter and is refused anyway, and a number or a string derived from the enum is computed by EF Core.
    /// </summary>
    internal sealed class ConvertedValueFinder(IModel model) : ExpressionVisitor
    {
        private static readonly HashSet<string> FilterMethods =
        [
            nameof(Queryable.Where), nameof(Queryable.OrderBy), nameof(Queryable.OrderByDescending), nameof(Queryable.ThenBy), nameof(Queryable.ThenByDescending),
            nameof(Queryable.Any), nameof(Queryable.All), nameof(Queryable.Count), nameof(Queryable.LongCount), nameof(Queryable.SkipWhile), nameof(Queryable.TakeWhile),
        ];

        private IProperty? _property;

        public static IProperty? Find(IModel model, IEnumerable<Expression> expressions)
        {
            var finder = new ConvertedValueFinder(model);
            foreach (var expression in expressions)
            {
                finder.Visit(expression);
            }

            return finder._property;
        }

        public override Expression? Visit(Expression? node) => _property == null ? base.Visit(node) : node;

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            var declaringType = node.Method.DeclaringType;
            if (declaringType == typeof(Queryable) || declaringType == typeof(Enumerable))
            {
                if (FilterMethods.Contains(node.Method.Name))
                {
                    Visit(node.Arguments[0]); // only the source flows on
                    return node;
                }

                if (node.Method.Name is nameof(Queryable.Join) or nameof(Queryable.GroupJoin) && node.Arguments.Count == 5)
                {
                    Visit(node.Arguments[0]);
                    Visit(node.Arguments[1]);
                    Visit(node.Arguments[4]); // the key selectors only compare
                    return node;
                }
            }

            if (declaringType == typeof(EF) && node.Method.Name == nameof(EF.Property)
                && node.Arguments[1] is ConstantExpression { Value: string propertyName })
            {
                Check(node.Arguments[0].Type, propertyName);
            }

            return base.VisitMethodCall(node);
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression != null && node.Member is System.Reflection.PropertyInfo)
            {
                Check(node.Expression.Type, node.Member.Name);
            }

            // A query captured in a closure (a subquery) is part of the SQL too
            if (typeof(IQueryable).IsAssignableFrom(node.Type) && node.Expression is null or ConstantExpression or MemberExpression
                && TryEvaluate(node) is IQueryable capturedQuery && capturedQuery.Expression != node)
            {
                Visit(capturedQuery.Expression);
            }

            return base.VisitMember(node);
        }

        private void Check(Type type, string propertyName)
        {
            foreach (var entityType in model.GetEntityTypes().Where(a => a.ClrType == type))
            {
                var property = entityType.FindProperty(propertyName);
                var converter = property?.GetTypeMapping().Converter;
                if (converter != null && !IsDefaultEnumConverter(converter))
                {
                    _property = property;
                    return;
                }
            }
        }

        private static bool IsDefaultEnumConverter(Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter converter) =>
            converter.GetType().IsGenericType
            && converter.GetType().GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.Storage.ValueConversion.EnumToNumberConverter<,>);

        private static object? TryEvaluate(Expression expression)
        {
            try
            {
                return Expression.Lambda<Func<object?>>(Expression.Convert(expression, typeof(object))).Compile(preferInterpretation: true)();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private sealed class ClientEvaluationFinder(ParameterExpression parameter, IEvaluatableExpressionFilter filter, IModel model) : ExpressionVisitor
    {
        public bool IsEvaluatable { get; private set; } = true;

        public override Expression? Visit(Expression? node)
        {
            if (node == null || !IsEvaluatable)
            {
                return node;
            }

            if (node == parameter || typeof(IQueryable).IsAssignableFrom(node.Type) || !filter.IsEvaluatableExpression(node, model))
            {
                IsEvaluatable = false;
                return node;
            }

            return base.Visit(node);
        }
    }
}
