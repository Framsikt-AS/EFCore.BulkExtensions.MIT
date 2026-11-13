using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using EFCore.BulkExtensions.SqlAdapters;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

namespace EFCore.BulkExtensions;

/// <summary>
/// Builds the statements of <see cref="IQueryableBatchExtensions.InsertFromQuery{TSource, TTarget, TOutput}"/>: a <c>MERGE</c> that inserts
/// the rows and outputs, for each, the values of an output row into a temp table, then an <c>INSERT</c> from the temp table into the output's table.
/// </summary>
/// <remarks>
/// <c>MERGE</c> rather than <c>INSERT</c>, because only its <c>OUTPUT</c> can read the source row next to the inserted one. The output goes through a temp
/// table because <c>OUTPUT INTO</c> cannot target a table with triggers or on either side of a foreign key.
/// Every value taken from the source row is projected into its own member of a generated row type, named <c>C0</c>, <c>C1</c>, ..., because EF Core
/// names the columns of a projection after its members and renames members that collide (<c>[t]</c>, <c>[t0]</c>), which a projection into the
/// target type and the output type together would do.
/// </remarks>
internal static class InsertFromQueryOutputUtil
{
    private const string SqlServerProviderName = "Microsoft.EntityFrameworkCore.SqlServer";
    private const string ParameterNamePrefix = "@__InsertFromQuery_p";
    private const string RowCountParameterName = "@__InsertFromQuery_rowCount";
    private const string SourceAlias = "__InsertFromQuery_source";
    private const string OwnTransactionVariable = "@__InsertFromQuery_ownTransaction";

    internal static (DbContext Context, string Sql, List<object> Parameters, DbParameter RowCount) GetSql<TSource, TTarget, TOutput>(
        IQueryable<TSource> source,
        Expression<Func<TSource, TTarget>> selector,
        Expression<Func<TSource, TTarget, TOutput>> outputSelector
    )
        where TTarget : class
        where TOutput : class
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));
        if (selector == null)
            throw new ArgumentNullException(nameof(selector));
        if (outputSelector == null)
            throw new ArgumentNullException(nameof(outputSelector));
        if (source.Provider is not IAsyncQueryProvider)
            throw new ArgumentException("The source must be an Entity Framework Core query.", nameof(source));

        var context = BatchUtil.GetDbContext(source) ?? throw new ArgumentException("Unable to determine context");
        if (context.Database.ProviderName != SqlServerProviderName)
            throw new NotSupportedException($"InsertFromQuery is only supported on SQL Server, not with the provider '{context.Database.ProviderName}'.");

        var target = InsertFromQueryUtil.MappedTable.For(context, typeof(TTarget));
        var output = InsertFromQueryUtil.MappedTable.For(context, typeof(TOutput));
        var evaluatableExpressionFilter = context.GetService<IEvaluatableExpressionFilter>();
        var dbServer = SqlAdaptersMapping.DbServer(context);

        var sourceParameter = selector.Parameters[0];
        var sourceValues = new List<Expression>();
        var parameters = new List<object>();

        string SourceValue(Expression value)
        {
            var canonical = InsertFromQueryUtil.SameSqlCanonicalizer.Canonicalize(value);
            var index = sourceValues.FindIndex(a => ExpressionEqualityComparer.Instance.Equals(InsertFromQueryUtil.SameSqlCanonicalizer.Canonicalize(a), canonical));
            if (index < 0)
            {
                sourceValues.Add(value);
                index = sourceValues.Count - 1;
            }

            return $"{Delimit(SourceAlias)}.{Delimit(RowType.MemberName(index))}";
        }

        string Parameter(IProperty property, Expression value)
        {
            var parameterName = ParameterNamePrefix + parameters.Count;
            parameters.Add(InsertFromQueryUtil.CreateParameter(property, parameterName, InsertFromQueryUtil.Evaluate(value)));
            return parameterName;
        }

        var insertColumns = new List<string>();
        var insertValues = new List<string>();
        foreach (var (property, column, assignment) in target.Assignments(selector.Body, dbServer, "selector"))
        {
            var value = assignment.Expression;
            insertColumns.Add(column);
            if (InsertFromQueryUtil.IsEvaluatedOnClient(value, sourceParameter, evaluatableExpressionFilter, context.Model))
            {
                insertValues.Add(Parameter(property, value));
            }
            else
            {
                RefuseConverter(target, property);
                insertValues.Add(SourceValue(value));
            }
        }

        var outputSourceParameter = outputSelector.Parameters[0];
        var outputTargetParameter = outputSelector.Parameters[1];
        var outputColumns = new List<string>();
        var outputValues = new List<string>();
        var copiedColumns = new List<string>();
        var copiedValues = new List<string>();
        foreach (var (property, column, assignment) in output.Assignments(outputSelector.Body, dbServer, "outputSelector"))
        {
            var value = assignment.Expression;
            if (ParameterFinder.Uses(value, outputTargetParameter))
            {
                if (ParameterFinder.Uses(value, outputSourceParameter) || InsertedColumn(target, value, outputTargetParameter) is not { } insertedColumn)
                {
                    throw new NotSupportedException(
                        $"'{output.ClrType.Name}.{property.Name}' must be a property of the inserted {target.ClrType.Name} as it is (like 'inserted.Id'), " +
                        "or a value that does not use the inserted row.");
                }

                RefuseConverter(output, property);
                outputColumns.Add(column);
                outputValues.Add($"INSERTED.{insertedColumn}");
            }
            else if (InsertFromQueryUtil.IsEvaluatedOnClient(value, outputSourceParameter, evaluatableExpressionFilter, context.Model))
            {
                copiedColumns.Add(column);
                copiedValues.Add(Parameter(property, value));
            }
            else
            {
                RefuseConverter(output, property);
                outputColumns.Add(column);
                outputValues.Add(SourceValue(ParameterReplacer.Replace(value, outputSourceParameter, sourceParameter)));
            }
        }

        if (sourceValues.Count == 0)
            throw new NotSupportedException("At least one of the selector's values must come from the query.");
        if (outputColumns.Count == 0)
            throw new NotSupportedException("At least one of the output selector's values must come from the inserted row or the query's row.");

        if (InsertFromQueryUtil.EndsWithOrdering(source.Expression))
            throw new NotSupportedException("InsertFromQuery does not keep the query's order; remove the OrderBy/ThenBy, or limit it with Take or Skip.");

        var convertedProperty = InsertFromQueryUtil.ConvertedValueFinder.Find(context.Model, [source.Expression, .. sourceValues]);
        if (convertedProperty != null)
        {
            throw new NotSupportedException(
                $"'{convertedProperty.DeclaringType.ClrType.Name}.{convertedProperty.Name}' has a value converter and its value can reach the inserted rows: " +
                "SQL Server would insert the value as stored, without the conversion EF Core applies when it reads it. Insert these rows another way.");
        }

        var projectedQuery = RowType.Project(source, sourceParameter, sourceValues);
        InsertFromQueryUtil.EnsureTranslatedToSql(projectedQuery);

        var (querySql, queryParameters) = projectedQuery.ToParametrizedSql();
        parameters.AddRange(queryParameters);

        var rowCount = new SqlParameter(RowCountParameterName, SqlDbType.Int) { Direction = ParameterDirection.Output };
        parameters.Add(rowCount);

        // The two inserts are one change: in the session's transaction, or else in one the batch starts. A failure inside the
        // session's transaction is left to its owner. The temp table goes when the batch's sp_executesql scope ends.
        var outputTable = Delimit("#__InsertFromQuery_output_" + Guid.NewGuid().ToString("N"));
        var allCopiedColumns = outputColumns.Concat(copiedColumns).ToList();
        var nl = Environment.NewLine;
        var sql = $"DECLARE {OwnTransactionVariable} bit = CASE WHEN @@TRANCOUNT = 0 THEN 1 ELSE 0 END;" + nl
                  + "BEGIN TRY" + nl
                  + $"IF {OwnTransactionVariable} = 1 BEGIN TRANSACTION;" + nl
                  + $"SELECT TOP (0) {string.Join(", ", outputColumns)} INTO {outputTable} FROM {output.FullTableName};" + nl
                  + $"MERGE INTO {target.FullTableName} AS [T]" + nl
                  + "USING (" + nl
                  + querySql + nl
                  + $") AS {Delimit(SourceAlias)}" + nl
                  + "ON 1 = 0" + nl
                  + $"WHEN NOT MATCHED THEN INSERT ({string.Join(", ", insertColumns)}) VALUES ({string.Join(", ", insertValues)})" + nl
                  + $"OUTPUT {string.Join(", ", outputValues)} INTO {outputTable} ({string.Join(", ", outputColumns)});" + nl
                  + $"SET {RowCountParameterName} = @@ROWCOUNT;" + nl
                  + $"INSERT INTO {output.FullTableName} ({string.Join(", ", allCopiedColumns)})" + nl
                  + $"SELECT {string.Join(", ", outputColumns.Concat(copiedValues))} FROM {outputTable};" + nl
                  + $"DROP TABLE {outputTable};" + nl
                  + $"IF {OwnTransactionVariable} = 1 COMMIT TRANSACTION;" + nl
                  + "END TRY" + nl
                  + "BEGIN CATCH" + nl
                  + $"IF {OwnTransactionVariable} = 1 AND @@TRANCOUNT > 0 ROLLBACK TRANSACTION;" + nl
                  + "THROW;" + nl
                  + "END CATCH";

        // ExecuteSqlRaw formats the SQL with string.Format
        sql = sql.Replace("{", "{{").Replace("}", "}}");

        return (context, sql, parameters, rowCount);
    }

    private static string Delimit(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    private static void RefuseConverter(InsertFromQueryUtil.MappedTable table, IProperty property)
    {
        if (property.GetTypeMapping().Converter != null)
        {
            throw new NotSupportedException(
                $"'{table.ClrType.Name}.{property.Name}' has a value converter (enums have one too), and its value comes from a row: the SQL value " +
                "would be copied without the conversion. Assign it a value that does not depend on the row, or insert these rows another way.");
        }
    }

    /// <summary>The delimited column of the inserted row that <paramref name="value"/> reads as it is, or null.</summary>
    private static string? InsertedColumn(InsertFromQueryUtil.MappedTable target, Expression value, ParameterExpression inserted)
    {
        while (value is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
               && (Nullable.GetUnderlyingType(convert.Type) == convert.Operand.Type || Nullable.GetUnderlyingType(convert.Operand.Type) == convert.Type))
        {
            value = convert.Operand;
        }

        var propertyName = value switch
        {
            MemberExpression { Expression: var instance, Member: PropertyInfo member } when instance == inserted => member.Name,
            MethodCallExpression { Method: { Name: nameof(EF.Property) } method, Arguments: [var instance, ConstantExpression { Value: string name }] }
                when method.DeclaringType == typeof(EF) && instance == inserted => name,
            _ => null,
        };

        if (propertyName == null || target.EntityType.FindProperty(propertyName) is not { } property)
            return null;

        RefuseConverter(target, property);
        return property.GetColumnName(target.StoreObject) is { } column ? Delimit(column) : null;
    }

    private sealed class ParameterFinder(ParameterExpression parameter) : ExpressionVisitor
    {
        private bool _found;

        public static bool Uses(Expression expression, ParameterExpression parameter)
        {
            var finder = new ParameterFinder(parameter);
            finder.Visit(expression);
            return finder._found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            _found |= node == parameter;
            return node;
        }
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        public static Expression Replace(Expression expression, ParameterExpression from, ParameterExpression to) => new ParameterReplacer(from, to).Visit(expression);

        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }

    /// <summary>Row types with one public property per projected value, <c>C0</c> to <c>Cn</c>, generated once per list of value types.</summary>
    private static class RowType
    {
        private static readonly ConcurrentDictionary<string, Type> Types = new();
        private static readonly ModuleBuilder Module = AssemblyBuilder
            .DefineDynamicAssembly(new AssemblyName("EFCore.BulkExtensions.InsertFromQueryRows"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("InsertFromQueryRows");

        public static string MemberName(int index) => "C" + index;

        public static IQueryable Project<TSource>(IQueryable<TSource> source, ParameterExpression parameter, IReadOnlyList<Expression> values)
        {
            var rowType = Get(values.Select(a => a.Type).ToList());
            var row = Expression.MemberInit(
                Expression.New(rowType),
                values.Select((value, index) => Expression.Bind(rowType.GetProperty(MemberName(index))!, value)));
            var lambda = Expression.Lambda(row, parameter);
            var select = Expression.Call(typeof(Queryable), nameof(Queryable.Select), [typeof(TSource), rowType], source.Expression, Expression.Quote(lambda));
            return source.Provider.CreateQuery(select);
        }

        private static Type Get(IReadOnlyList<Type> valueTypes) =>
            Types.GetOrAdd(string.Join("|", valueTypes.Select(a => a.AssemblyQualifiedName)), _ => Build(valueTypes));

        private static Type Build(IReadOnlyList<Type> valueTypes)
        {
            lock (Module)
            {
                var type = Module.DefineType("Row" + Guid.NewGuid().ToString("N"), TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
                for (var i = 0; i < valueTypes.Count; i++)
                {
                    var valueType = valueTypes[i];
                    var field = type.DefineField("_c" + i, valueType, FieldAttributes.Private);
                    var property = type.DefineProperty(MemberName(i), PropertyAttributes.None, valueType, null);
                    const MethodAttributes accessor = MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;

                    var getter = type.DefineMethod("get_" + MemberName(i), accessor, valueType, Type.EmptyTypes);
                    var il = getter.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, field);
                    il.Emit(OpCodes.Ret);
                    property.SetGetMethod(getter);

                    var setter = type.DefineMethod("set_" + MemberName(i), accessor, null, [valueType]);
                    il = setter.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Stfld, field);
                    il.Emit(OpCodes.Ret);
                    property.SetSetMethod(setter);
                }

                return type.CreateType();
            }
        }
    }
}
