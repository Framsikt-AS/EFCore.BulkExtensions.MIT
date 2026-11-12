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

        var targetType = typeof(TTarget);
        var entityType = context.Model.FindEntityType(targetType)
                         ?? throw new InvalidOperationException($"'{targetType.Name}' is not an entity type of {context.GetType().Name}.");
        if (entityType.IsOwned() || entityType.BaseType != null || entityType.GetDirectlyDerivedTypes().Any())
            throw new NotSupportedException($"InsertFromQuery does not support owned entity types or inheritance hierarchies ('{targetType.Name}').");

        var tableName = entityType.GetTableName() ?? throw new NotSupportedException($"'{targetType.Name}' is not mapped to a table.");
        var schemaName = entityType.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(tableName, schemaName);
        var fullTableName = $"{Delimit(schemaName ?? "dbo")}.{Delimit(tableName)}"; // same default schema as the bulk operations

        if (selector.Body is not MemberInitExpression memberInit || memberInit.NewExpression.Arguments.Count != 0 || memberInit.Type != targetType)
            throw new NotSupportedException($"The selector must create the target entity with an object initializer, like 'x => new {targetType.Name} {{ Column = x.Value, ... }}'.");
        if (memberInit.Bindings.Count == 0)
            throw new NotSupportedException("The selector must assign at least one property.");

        var parameter = selector.Parameters[0];
        var evaluatableExpressionFilter = context.GetService<IEvaluatableExpressionFilter>();
        var dbServer = SqlAdaptersMapping.DbServer(context);

        var insertColumns = new List<string>();
        var selectValues = new List<string>();
        var parameters = new List<object>();
        var serverBindings = new List<MemberAssignment>();
        var assignedProperties = new HashSet<IProperty>();

        foreach (var binding in memberInit.Bindings)
        {
            if (binding is not MemberAssignment assignment)
                throw new NotSupportedException($"'{targetType.Name}.{binding.Member.Name}' must be assigned a value; nested initializers are not supported.");

            var property = entityType.FindProperty(assignment.Member.Name)
                           ?? throw new NotSupportedException($"'{targetType.Name}.{assignment.Member.Name}' is not a mapped property.");
            var columnName = property.GetColumnName(storeObject)
                             ?? throw new NotSupportedException($"'{targetType.Name}.{property.Name}' is not mapped to a column of {fullTableName}.");

            if (!assignedProperties.Add(property))
                throw new NotSupportedException($"'{targetType.Name}.{property.Name}' is assigned more than once.");
            if (dbServer.PropertyHasIdentity(property))
                throw new NotSupportedException($"'{targetType.Name}.{property.Name}' is an identity column, which the database generates; remove it from the selector.");
            if (property.GetComputedColumnSql() != null)
                throw new NotSupportedException($"'{targetType.Name}.{property.Name}' is a computed column; remove it from the selector.");
            if (property.IsConcurrencyToken && property.ValueGenerated == ValueGenerated.OnAddOrUpdate)
                throw new NotSupportedException($"'{targetType.Name}.{property.Name}' is a row version, which the database generates; remove it from the selector.");

            insertColumns.Add(Delimit(columnName));

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
                    $"'{targetType.Name}.{property.Name}' has a value converter (enums have one too), and its value comes from the query: the query's SQL value " +
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

        var projection = Expression.Lambda<Func<TSource, TTarget>>(Expression.MemberInit(memberInit.NewExpression, serverBindings), parameter);
        var projectedQuery = source.Select(projection);
        EnsureTranslatedToSql(projectedQuery);

        var (querySql, queryParameters) = projectedQuery.ToParametrizedSql();
        parameters.AddRange(queryParameters);

        var rowCount = new SqlParameter(RowCountParameterName, SqlDbType.Int) { Direction = ParameterDirection.Output };
        parameters.Add(rowCount);

        var sql = $"INSERT INTO {fullTableName} ({string.Join(", ", insertColumns)})" + Environment.NewLine
                  + $"SELECT {string.Join(", ", selectValues)}" + Environment.NewLine
                  + "FROM (" + Environment.NewLine
                  + querySql + Environment.NewLine
                  + $") AS {Delimit(SourceAlias)};" + Environment.NewLine
                  + $"SET {RowCountParameterName} = @@ROWCOUNT;";

        // ExecuteSqlRaw formats the SQL with string.Format
        sql = sql.Replace("{", "{{").Replace("}", "}}");

        return (context, sql, parameters, rowCount);
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
    private static bool EndsWithOrdering(Expression expression)
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
    /// <c>EF.Property(x, "Name")</c> becomes <c>x.Name</c>, and conversions that only add or remove <see cref="Nullable{T}"/> are dropped.
    /// Used only for comparing.
    /// </summary>
    private sealed class SameSqlCanonicalizer : ExpressionVisitor
    {
        private static readonly SameSqlCanonicalizer Instance = new();

        public static Expression Canonicalize(Expression expression) => Instance.Visit(expression);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType == typeof(EF) && node.Method.Name == nameof(EF.Property)
                && node.Arguments[1] is ConstantExpression { Value: string propertyName }
                && node.Arguments[0].Type.GetProperty(propertyName) is { } property)
            {
                return Expression.Property(Visit(node.Arguments[0]), property);
            }

            return base.VisitMethodCall(node);
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (node.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
                && (Nullable.GetUnderlyingType(node.Type) == node.Operand.Type || Nullable.GetUnderlyingType(node.Operand.Type) == node.Type))
            {
                return Visit(node.Operand);
            }

            return base.VisitUnary(node);
        }
    }

    /// <summary>
    /// Whether EF Core would evaluate the expression once on the client rather than translate it: it does not use the selector's
    /// parameter, contains no query, and every node passes the provider's <see cref="IEvaluatableExpressionFilter"/> (which keeps, for
    /// example, <c>DateTime.UtcNow</c> and <c>Guid.NewGuid()</c> on the server).
    /// </summary>
    private static bool IsEvaluatedOnClient(Expression expression, ParameterExpression parameter, IEvaluatableExpressionFilter filter, IModel model)
    {
        var finder = new ClientEvaluationFinder(parameter, filter, model);
        finder.Visit(expression);
        return finder.IsEvaluatable;
    }

    private static object? Evaluate(Expression expression) =>
        Expression.Lambda<Func<object?>>(Expression.Convert(expression, typeof(object))).Compile(preferInterpretation: true)();

    private static DbParameter CreateParameter(IProperty property, string name, object? value)
    {
        using var command = new SqlCommand();
        return property.GetRelationalTypeMapping().CreateParameter(command, name, value, property.IsNullable);
    }

    /// <summary>
    /// EF Core evaluates on the client whatever part of a final projection it cannot translate, and leaves it out of the SQL.
    /// It refuses a set operation over such a projection, so compiling (not running) one shows whether it translated entirely.
    /// </summary>
    private static void EnsureTranslatedToSql<TTarget>(IQueryable<TTarget> projectedQuery)
    {
        var check = projectedQuery.Concat(projectedQuery);
        try
        {
            check.Provider.Execute<IEnumerable>(check.Expression);
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
    private sealed class ConvertedValueFinder(IModel model) : ExpressionVisitor
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
