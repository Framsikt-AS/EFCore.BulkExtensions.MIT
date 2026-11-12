using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace EFCore.BulkExtensions.SqlAdapters.SqlServer;

/// <summary>
/// The columns <see cref="SqlBulkCopy"/> gets for a list of entities, and what is needed to fill them:
/// the empty <see cref="DataTable"/> and the lookups <see cref="SqlOperationsServerAdapter"/> builds before its first row.
/// </summary>
internal sealed class BulkCopySchema(
    DataTable dataTable,
    Dictionary<string, object?> columnsDict,
    HashSet<string> ownedEntitiesMappedProperties,
    bool isSqlServer,
    IEntityType entityType,
    Dictionary<string, IProperty> entityPropertiesDict,
    Dictionary<string, INavigation> entityNavigationOwnedDict,
    Dictionary<string, IProperty> entityShadowFkPropertiesDict,
    Dictionary<string, string?> entityShadowFkPropertyColumnNamesDict,
    Dictionary<string, string?> shadowPropertyColumnNamesDict,
    PropertyInfo[] properties
)
{
    /// <summary>The columns, in the order they are copied, with no rows.</summary>
    public DataTable DataTable { get; } = dataTable;

    /// <summary>One entry per column, in column order, keyed as the row loop writes it, with the value before the first row.</summary>
    public Dictionary<string, object?> ColumnsDict { get; } = columnsDict;

    public HashSet<string> OwnedEntitiesMappedProperties { get; } = ownedEntitiesMappedProperties;
    public bool IsSqlServer { get; } = isSqlServer;
    public IEntityType EntityType { get; } = entityType;
    public Dictionary<string, IProperty> EntityPropertiesDict { get; } = entityPropertiesDict;
    public Dictionary<string, INavigation> EntityNavigationOwnedDict { get; } = entityNavigationOwnedDict;
    public Dictionary<string, IProperty> EntityShadowFkPropertiesDict { get; } = entityShadowFkPropertiesDict;
    public Dictionary<string, string?> EntityShadowFkPropertyColumnNamesDict { get; } = entityShadowFkPropertyColumnNamesDict;
    public Dictionary<string, string?> ShadowPropertyColumnNamesDict { get; } = shadowPropertyColumnNamesDict;
    public PropertyInfo[] Properties { get; } = properties;
}

/// <summary>
/// Chooses between <see cref="EntityBulkCopyReader{T}"/> and the <see cref="DataTable"/> for <see cref="SqlBulkCopy"/>.
/// </summary>
internal static class EntityBulkCopyReader
{
    internal enum SourceMode
    {
        /// <summary>The reader where it is safe (see <see cref="EntityBulkCopyReader{T}.CanStream"/>), otherwise the DataTable.</summary>
        Auto,
        /// <summary>Always the DataTable. Tests only.</summary>
        DataTable,
        /// <summary>The reader whenever it can mirror the DataTable, ignoring <see cref="EntityBulkCopyReader{T}.CanStream"/>. Tests only.</summary>
        Reader,
    }

    /// <summary>Tests only: overrides the choice. Read through reflection by the test project.</summary>
    internal static SourceMode Mode = SourceMode.Auto;

    /// <summary>Tests only: told whether each bulk copy used the reader. Set through reflection by the test project.</summary>
#pragma warning disable CS0649 // assigned through reflection
    internal static Action<bool>? SourceChosen;
#pragma warning restore CS0649
}

/// <summary>
/// A forward-only reader over a list of entities for <see cref="SqlBulkCopy"/>, in place of the <see cref="DataTable"/>
/// that <c>SqlOperationsServerAdapter.FillDataTable</c> builds. It has the same columns, from the same
/// <see cref="BulkCopySchema"/>, and per row it does what that method's row loop does, in the same order and with the same
/// side effects (getters, value converters, rounding, spatial and hierarchy conversion, shadow properties); only work that is
/// the same for every row is done once. Each value is then converted as a <see cref="DataTable"/> column of that type would store it,
/// so <see cref="SqlBulkCopy"/> receives the same values.
/// </summary>
/// <remarks>
/// Unlike the DataTable, the reader runs the row loop while <see cref="SqlBulkCopy"/> is already writing, so
/// <see cref="CanStream"/> only allows it where that cannot be told apart.
/// </remarks>
internal sealed class EntityBulkCopyReader<T> : DbDataReader
{
    private enum StepKind
    {
        /// <summary>Writes the value to its column.</summary>
        Column,
        /// <summary>Writes the principal key of the navigation to the shadow foreign key column.</summary>
        ShadowForeignKey,
        /// <summary>Writes the owned type's properties to their columns.</summary>
        Owned,
        /// <summary>Only computes the value, for its side effects (a getter, a converter, the spatial SRID).</summary>
        ComputeOnly,
    }

    private enum ValueStorage
    {
        /// <summary>Stored unchanged when of the column's type.</summary>
        AsIs,
        /// <summary>Stored as a DateTime of unspecified kind.</summary>
        DateTime,
        /// <summary>Stored as the enum's underlying integer.</summary>
        Enum,
    }

    private sealed class OwnedStep(FastProperty fastProperty, ValueConverter? converter, int slot)
    {
        public FastProperty FastProperty { get; } = fastProperty;
        public ValueConverter? Converter { get; } = converter;
        public int Slot { get; } = slot;
    }

    private sealed class PropertyStep(PropertyInfo property, FastProperty? fastProperty, int? dateTime2Precision, ValueConverter? converter)
    {
        public PropertyInfo Property { get; } = property;
        public FastProperty? FastProperty { get; } = fastProperty;
        public int? DateTime2Precision { get; } = dateTime2Precision;
        public ValueConverter? Converter { get; } = converter;
        public StepKind Kind { get; set; }
        public int Slot { get; set; } = -1;
        public PropertyInfo? PrincipalKeyProperty { get; set; }
        public OwnedStep[] OwnedSteps { get; set; } = [];
    }

    private sealed class ShadowStep(string propertyName, ValueConverter? converter, int slot)
    {
        public string PropertyName { get; } = propertyName;
        public ValueConverter? Converter { get; } = converter;
        public int Slot { get; } = slot;
    }

    /// <summary>The fewest entities a direct insert streams (see <see cref="CanStream"/>).</summary>
    internal const int DirectInsertMinimumCount = 10_000;

    private readonly DbContext _context;
    private readonly IList<T> _entities;
    private readonly TableInfo _tableInfo;
    private readonly bool _isSqlServer;
    private readonly PropertyStep[] _steps;
    private readonly ShadowStep[] _shadowSteps;
    private readonly int _originalIndexSlot;
    private readonly string[] _names;
    private readonly Type[] _columnTypes;
    private readonly Type[] _fieldTypes;
    private readonly ValueStorage[] _storages;
    private readonly DataTable _schemaTable;
    private readonly object?[] _current;
    private readonly object[] _values;
    private readonly object[] _nullValues;
    private readonly SqlServerBytesWriter _sqlServerBytesWriter = new();

    private readonly bool _endOnFailure;

    private IEnumerator<T>? _enumerator;
    private int _index = -1;
    private bool _closed;
    private DataRow? _conversionRow;

    private EntityBulkCopyReader(
        DbContext context,
        IList<T> entities,
        TableInfo tableInfo,
        BulkCopySchema schema,
        PropertyStep[] steps,
        ShadowStep[] shadowSteps,
        int originalIndexSlot
    )
    {
        _context = context;
        _entities = entities;
        _tableInfo = tableInfo;
        _isSqlServer = schema.IsSqlServer;
        _steps = steps;
        _shadowSteps = shadowSteps;
        _originalIndexSlot = originalIndexSlot;
        _schemaTable = schema.DataTable;
        _endOnFailure = tableInfo.InsertToTempTable;

        var columns = schema.DataTable.Columns.Cast<DataColumn>().ToArray();
        _names = columns.Select(a => a.ColumnName).ToArray();
        _columnTypes = columns.Select(a => a.DataType).ToArray();
        _storages = _columnTypes.Select(a => a.IsEnum ? ValueStorage.Enum : a == typeof(DateTime) ? ValueStorage.DateTime : ValueStorage.AsIs).ToArray();
        _fieldTypes = _columnTypes.Select(a => a.IsEnum ? Enum.GetUnderlyingType(a) : a).ToArray();
        _current = schema.ColumnsDict.Values.ToArray();
        _values = new object[columns.Length];

        // What a DataTable returns for a null: DBNull, or the type's own Null for an INullable column (like SqlHierarchyId.Null)
        var nullRow = schema.DataTable.Clone().Rows.Add(new object?[columns.Length]);
        _nullValues = columns.Select(a => nullRow[a.Ordinal]).ToArray();
    }

    /// <summary>
    /// The reader for these entities, or <c>null</c> when the <see cref="DataTable"/> should be used.
    /// </summary>
    public static async Task<EntityBulkCopyReader<T>?> TryCreateAsync(
        DbContext context,
        IList<T> entities,
        TableInfo tableInfo,
        BulkCopySchema schema,
        SqlConnection connection,
        SqlTransaction? transaction,
        bool isAsync,
        CancellationToken cancellationToken
    )
    {
        var mode = EntityBulkCopyReader.Mode;
        EntityBulkCopyReader<T>? reader = null;
        if (mode == EntityBulkCopyReader.SourceMode.Reader)
        {
            reader = Create(context, entities, tableInfo, schema);
        }
        else if (mode == EntityBulkCopyReader.SourceMode.Auto && CanStream(context, entities, tableInfo, schema))
        {
            reader = Create(context, entities, tableInfo, schema);

            // A failing entity cancels a direct insert midway (see CanStream). With XACT_ABORT ON that also rolls back the open transaction,
            // which a DataTable built first would have left alone.
            if (reader != null && !tableInfo.InsertToTempTable
                && await CancelRollsBackTransactionAsync(context, connection, transaction, isAsync, cancellationToken).ConfigureAwait(false))
            {
                reader.Dispose();
                reader = null;
            }
        }

        EntityBulkCopyReader.SourceChosen?.Invoke(reader != null);
        return reader;
    }

    private static async Task<bool> CancelRollsBackTransactionAsync(DbContext context, SqlConnection connection, SqlTransaction? transaction, bool isAsync, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT CASE WHEN @@TRANCOUNT > 0 AND (@@OPTIONS & 16384) <> 0 THEN 1 ELSE 0 END"; // 16384: XACT_ABORT
        var commandTimeout = context.Database.GetCommandTimeout();
        if (commandTimeout.HasValue)
        {
            command.CommandTimeout = commandTimeout.Value;
        }

        var result = isAsync ? await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) : command.ExecuteScalar();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>
    /// Whether streaming cannot be told apart from copying a DataTable that was built first.
    /// </summary>
    internal static bool CanStream(DbContext context, IList<T> entities, TableInfo tableInfo, BulkCopySchema schema)
    {
        var bulkConfig = tableInfo.BulkConfig;

        // With EnableStreaming, SqlBulkCopy reads large values from a reader through GetStream/GetTextReader, which it never does for a DataTable.
        if (bulkConfig.EnableStreaming)
        {
            return false;
        }

        // Building the DataTable fails before anything is sent; the reader can fail midway, with rows already sent.
        // Into the temp table, the reader then ends the copy normally and the exception is thrown after it (see Failure):
        // the temp table is never merged and is dropped as before. Into the table itself the copy has to be cancelled instead,
        // which keeps the rows of batches already finished (even in a transaction that is later committed) and rolls back
        // the open transaction under XACT_ABORT ON (checked in TryCreateAsync). So a direct insert streams only as one batch,
        // and only when it is large enough that the extra query for XACT_ABORT does not matter.
        bool isSingleBatch = bulkConfig.BatchSize == 0 || entities.Count <= bulkConfig.BatchSize;
        if (!tableInfo.InsertToTempTable && !(isSingleBatch && entities.Count >= DirectInsertMinimumCount))
        {
            return false;
        }

        // Code that might query the database would now run while SqlBulkCopy holds the connection:
        // a ShadowPropertyValue delegate, and the getters of navigations to principals when lazy loading can be on.
        if (bulkConfig.EnableShadowProperties && bulkConfig.ShadowPropertyValue != null)
        {
            return false;
        }

        if (schema.EntityShadowFkPropertiesDict.Count > 0 && MayLazyLoad(context, schema.EntityType))
        {
            return false;
        }

        return true;
    }

    private static bool MayLazyLoad(DbContext context, IEntityType entityType)
    {
        if (!context.ChangeTracker.LazyLoadingEnabled)
        {
            return false;
        }

        // Lazy loading without proxies injects an ILazyLoader (a service property); with proxies the Proxies options extension is registered.
        return entityType.GetServiceProperties().Any()
            || context.GetService<IDbContextOptions>().Extensions.Any(a => a.GetType().Namespace?.StartsWith("Microsoft.EntityFrameworkCore.Proxies", StringComparison.Ordinal) ?? false);
    }

    /// <summary>
    /// Plans the row loop of <c>FillDataTable</c>, or returns <c>null</c> if that loop would not fill exactly the schema's columns
    /// (it would then throw, or read a key it does not have); the DataTable path keeps that behaviour.
    /// </summary>
    private static EntityBulkCopyReader<T>? Create(DbContext context, IList<T> entities, TableInfo tableInfo, BulkCopySchema schema)
    {
        if (schema.ColumnsDict.Count != schema.DataTable.Columns.Count)
        {
            return null;
        }

        // SqlBulkCopy treats System.Data.SqlTypes values (SqlString.Null, ...) as nulls when they come from a DataTable,
        // but from a reader only for UDT columns (like hierarchyid). A column of such a type keeps the DataTable.
        if (schema.DataTable.Columns.Cast<DataColumn>().Any(a => a.DataType.Namespace == "System.Data.SqlTypes"))
        {
            return null;
        }

        // Into a decimal or numeric column, SqlBulkCopy converts a double or float from a DataTable (going by the column's type)
        // with more digits than one from a reader. The destination's type is not known for sure here, so such a column keeps the DataTable.
        if (schema.DataTable.Columns.Cast<DataColumn>().Any(a => a.DataType == typeof(double) || a.DataType == typeof(float)))
        {
            return null;
        }

        var slots = new Dictionary<string, int>();
        foreach (var key in schema.ColumnsDict.Keys)
        {
            slots.Add(key, slots.Count);
        }

        var bulkConfig = tableInfo.BulkConfig;
        bool hasConverterProperties = tableInfo.ConvertiblePropertyColumnDict.Count > 0;

        // omit virtual Navigation (except Owned and ShadowNavig.) since it's Getter can cause unwanted Select-s from Db
        var propertiesToLoad = schema.Properties.Where(a =>
            !tableInfo.AllNavigationsDictionary.ContainsKey(a.Name) || schema.EntityShadowFkPropertiesDict.ContainsKey(a.Name) || tableInfo.OwnedTypesDict.ContainsKey(a.Name)
        );

        var steps = new List<PropertyStep>();
        foreach (var property in propertiesToLoad)
        {
            tableInfo.FastPropertyDict.TryGetValue(property.Name, out var fastProperty);

            var hasDefaultVauleOnInsert =
                bulkConfig.OperationType == OperationType.Insert && !bulkConfig.SetOutputIdentity && tableInfo.DefaultValueProperties.Contains(property.Name);

            int? dateTime2Precision = null;
            if (bulkConfig.DateTime2PrecisionForceRound && schema.IsSqlServer && tableInfo.DateTime2PropertiesPrecisionLessThen7Dict.TryGetValue(property.Name, out var precision))
            {
                dateTime2Precision = precision;
            }

            ValueConverter? converter = null;
            if (hasConverterProperties && tableInfo.ConvertiblePropertyColumnDict.TryGetValue(property.Name, out var convertibleColumnName)
                && !tableInfo.ConvertibleColumnConverterDict.TryGetValue(convertibleColumnName, out converter))
            {
                return null;
            }

            var step = new PropertyStep(property, fastProperty, dateTime2Precision, converter);

            if (schema.EntityPropertiesDict.ContainsKey(property.Name) && !hasDefaultVauleOnInsert)
            {
                if (!slots.TryGetValue(property.Name, out var slot))
                {
                    return null;
                }

                step.Kind = StepKind.Column;
                step.Slot = slot;
            }
            else if (schema.EntityShadowFkPropertiesDict.TryGetValue(property.Name, out var foreignKeyShadowProperty))
            {
                var columnName = schema.EntityShadowFkPropertyColumnNamesDict[property.Name] ?? string.Empty;
                if (!schema.EntityPropertiesDict.TryGetValue(columnName, out var entityProperty) || entityProperty is null)
                {
                    step.Kind = StepKind.ComputeOnly; // BulkRead
                }
                else
                {
                    if (!slots.TryGetValue(columnName, out var slot))
                    {
                        return null;
                    }

                    step.Kind = StepKind.ShadowForeignKey;
                    step.Slot = slot;
                    step.PrincipalKeyProperty = foreignKeyShadowProperty.FindFirstPrincipal()?.PropertyInfo;
                }
            }
            else if (schema.EntityNavigationOwnedDict.ContainsKey(property.Name) && !tableInfo.LoadOnlyPKColumn)
            {
                var ownedSteps = new List<OwnedStep>();
                var ownedProperties = property.PropertyType.GetProperties().Where(a => schema.OwnedEntitiesMappedProperties.Contains(property.Name + "_" + a.Name));
                foreach (var ownedProperty in ownedProperties)
                {
                    var columnName = $"{property.Name}_{ownedProperty.Name}";
                    if (!tableInfo.FastPropertyDict.TryGetValue(columnName, out var ownedFastProperty) || !slots.TryGetValue(columnName, out var slot))
                    {
                        return null;
                    }

                    tableInfo.ConvertibleColumnConverterDict.TryGetValue(columnName, out var ownedConverter);
                    ownedSteps.Add(new OwnedStep(ownedFastProperty, ownedConverter, slot));
                }

                step.Kind = StepKind.Owned;
                step.OwnedSteps = ownedSteps.ToArray();
            }
            else
            {
                step.Kind = StepKind.ComputeOnly;
            }

            if (step.Kind == StepKind.ComputeOnly && fastProperty == null && converter == null)
            {
                continue; // the value is null and nothing is done with it
            }

            steps.Add(step);
        }

        var shadowSteps = new List<ShadowStep>();
        if (bulkConfig.EnableShadowProperties)
        {
            foreach (var shadowPropertyName in schema.ShadowPropertyColumnNamesDict.Keys)
            {
                var columnName = schema.ShadowPropertyColumnNamesDict[shadowPropertyName] ?? string.Empty;
                if (!slots.TryGetValue(shadowPropertyName, out var slot))
                {
                    return null;
                }

                tableInfo.ConvertibleColumnConverterDict.TryGetValue(columnName, out var converter);
                shadowSteps.Add(new ShadowStep(shadowPropertyName, converter, slot));
            }
        }

        int originalIndexSlot = -1;
        if (bulkConfig.UseOriginalIndexToIdentityMappingColumn && !slots.TryGetValue(tableInfo.OriginalIndexColumnName, out originalIndexSlot))
        {
            return null;
        }

        return new EntityBulkCopyReader<T>(context, entities, tableInfo, schema, steps.ToArray(), shadowSteps.ToArray(), originalIndexSlot);
    }

    /// <summary>
    /// The exception an entity threw while copying into the temp table. The reader then ends the copy instead of throwing into
    /// SqlBulkCopy, which would cancel it; the caller throws this after the copy.
    /// </summary>
    public ExceptionDispatchInfo? Failure { get; private set; }

    /// <inheritdoc />
    public override bool Read()
    {
        if (_closed || Failure != null)
        {
            return false;
        }

        if (!_endOnFailure)
        {
            return ReadEntity();
        }

        try
        {
            return ReadEntity();
        }
        catch (Exception exception)
        {
            Failure = ExceptionDispatchInfo.Capture(exception);
            return false;
        }
    }

    private bool ReadEntity()
    {
        _enumerator ??= _entities.GetEnumerator();
        if (!_enumerator.MoveNext())
        {
            return false;
        }

        _index++;
        T entity = _enumerator.Current;

        foreach (var step in _steps)
        {
            LoadProperty(step, entity);
        }

        if (_shadowSteps.Length > 0)
        {
            foreach (var shadowStep in _shadowSteps)
            {
                object? propertyValue;

                if (_tableInfo.BulkConfig.ShadowPropertyValue == null)
                {
                    propertyValue = _context.Entry(entity!).Property(shadowStep.PropertyName).CurrentValue;
                }
                else
                {
                    propertyValue = _tableInfo.BulkConfig.ShadowPropertyValue(entity!, shadowStep.PropertyName);
                }

                if (shadowStep.Converter != null)
                {
                    propertyValue = shadowStep.Converter.ConvertToProvider.Invoke(propertyValue);
                }

                _current[shadowStep.Slot] = propertyValue;
            }
        }

        if (_originalIndexSlot >= 0)
        {
            _current[_originalIndexSlot] = _index;
        }

        for (int i = 0; i < _current.Length; i++)
        {
            _values[i] = ToStoredValue(i, _current[i]);
        }

        return true;
    }

    private void LoadProperty(PropertyStep step, T entity)
    {
        var tableInfo = _tableInfo;
        object? propertyValue = step.FastProperty?.Get(entity!);

        if (step.DateTime2Precision is int precision)
        {
            DateTime? dateTimePropertyValue = (DateTime?)propertyValue;

            if (dateTimePropertyValue is not null)
            {
                int digitsToRemove = 7 - precision;
                int powerOf10 = (int)Math.Pow(10, digitsToRemove);

                long subsecondTicks = dateTimePropertyValue.Value.Ticks % 10000000;
                long ticksToRound = subsecondTicks + (subsecondTicks % 10 == 0 ? 1 : 0); // if ends with 0 add 1 tick to make sure rounding of value .5_zeros is rounded to Upper like SqlServer is doing, not to Even as Math.Round works
                int roundedTicks = Convert.ToInt32(Math.Round((decimal)ticksToRound / powerOf10, 0)) * powerOf10;
                dateTimePropertyValue = dateTimePropertyValue.Value.AddTicks(-subsecondTicks).AddTicks(roundedTicks);

                propertyValue = dateTimePropertyValue;
            }
        }

        if (step.Converter != null)
        {
            propertyValue = step.Converter.ConvertToProvider.Invoke(propertyValue);
        }

        if (tableInfo.HasSpatialType && propertyValue is Geometry geometryValue)
        {
            geometryValue.SRID = tableInfo.BulkConfig.SRID;

            if (tableInfo.PropertyColumnNamesDict.TryGetValue(step.Property.Name, out var spatialColumnName))
            {
                _sqlServerBytesWriter.IsGeography = tableInfo.ColumnNamesTypesDict[spatialColumnName] == "geography"; // "geography" type is default, otherwise it's "geometry" type
            }

            propertyValue = _sqlServerBytesWriter.Write(geometryValue);
        }

        if (propertyValue is HierarchyId hierarchyValue && _isSqlServer)
        {
            using MemoryStream memStream = new();
            using BinaryWriter binWriter = new(memStream);
#if !NET8_0 && !NET9_0 && !NET10_0
            hierarchyValue.Write(binWriter);
#endif
            propertyValue = memStream.ToArray();
        }

        switch (step.Kind)
        {
            case StepKind.Column:
                _current[step.Slot] = propertyValue;
                break;

            case StepKind.ShadowForeignKey:
                _current[step.Slot] = propertyValue == null ? null : step.PrincipalKeyProperty?.GetValue(propertyValue);
                break;

            case StepKind.Owned:
                foreach (var ownedStep in step.OwnedSteps)
                {
                    var ownedPropertyValue = propertyValue == null ? null : ownedStep.FastProperty.Get(propertyValue);

                    if (ownedStep.Converter != null)
                    {
                        _current[ownedStep.Slot] = ownedPropertyValue == null ? null : ownedStep.Converter.ConvertToProvider.Invoke(ownedPropertyValue);
                    }
                    else if (tableInfo.HasSpatialType && ownedPropertyValue is Geometry ownedGeometryValue)
                    {
                        ownedGeometryValue.SRID = tableInfo.BulkConfig.SRID;

                        if (tableInfo.PropertyColumnNamesDict.TryGetValue(step.Property.Name, out var ownedSpatialColumnName))
                        {
                            _sqlServerBytesWriter.IsGeography = tableInfo.ColumnNamesTypesDict[ownedSpatialColumnName] == "geography"; // "geography" type is default, otherwise it's "geometry" type
                        }

                        _current[ownedStep.Slot] = _sqlServerBytesWriter.Write(ownedGeometryValue);
                    }
                    else
                    {
                        _current[ownedStep.Slot] = ownedPropertyValue;
                    }
                }
                break;
        }
    }

    /// <summary>
    /// The value a <see cref="DataTable"/> column of the same type returns after this value is stored in it.
    /// </summary>
    private object ToStoredValue(int ordinal, object? value)
    {
        if (value == null)
        {
            return _nullValues[ordinal];
        }

        if (value is DBNull)
        {
            return _nullValues[ordinal] is DBNull ? DBNull.Value : ConvertLikeDataTable(ordinal, value);
        }

        var columnType = _columnTypes[ordinal];
        switch (_storages[ordinal])
        {
            case ValueStorage.DateTime when value is DateTime dateTime:
                return DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified); // DataColumn.DateTimeMode is UnspecifiedLocal by default

            case ValueStorage.Enum when value.GetType() == columnType:
                return Convert.ChangeType(value, _fieldTypes[ordinal], CultureInfo.InvariantCulture); // a DataTable stores an enum as its underlying integer

            case ValueStorage.AsIs when value.GetType() == columnType || (!columnType.IsValueType && columnType.IsInstanceOfType(value)):
                return value;

            default:
                return ConvertLikeDataTable(ordinal, value);
        }
    }

    /// <summary>
    /// For a value not of its column's type: stores it in a one-row DataTable with the same columns and reads it back,
    /// so it is converted, or rejected with the same exception, exactly as <see cref="DataRowCollection.Add(object[])"/> would.
    /// </summary>
    private object ConvertLikeDataTable(int ordinal, object value)
    {
        if (_conversionRow == null)
        {
            var conversionTable = _schemaTable.Clone();
            _conversionRow = conversionTable.NewRow();
            conversionTable.Rows.Add(_conversionRow);
        }

        _conversionRow[ordinal] = value;
        return _conversionRow[ordinal];
    }

    #region DbDataReader

    /// <inheritdoc />
    public override int FieldCount => _names.Length;

    /// <inheritdoc />
    public override bool HasRows => _entities.Count > 0;

    /// <inheritdoc />
    public override bool IsClosed => _closed;

    /// <inheritdoc />
    public override int RecordsAffected => -1;

    /// <inheritdoc />
    public override int Depth => 0;

    /// <inheritdoc />
    public override object this[int ordinal] => GetValue(ordinal);

    /// <inheritdoc />
    public override object this[string name] => GetValue(GetOrdinal(name));

    /// <inheritdoc />
    public override bool NextResult() => false;

    /// <inheritdoc />
    public override void Close()
    {
        _closed = true;
        _enumerator?.Dispose();
        _enumerator = null;
    }

    /// <inheritdoc />
    public override string GetName(int ordinal) => _names[ordinal];

    /// <inheritdoc />
    public override int GetOrdinal(string name)
    {
        int index = Array.IndexOf(_names, name);
        if (index < 0)
        {
            index = Array.FindIndex(_names, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        }

        return index >= 0 ? index : throw new IndexOutOfRangeException(name);
    }

    /// <inheritdoc />
    public override Type GetFieldType(int ordinal) => _fieldTypes[ordinal];

    /// <inheritdoc />
    public override string GetDataTypeName(int ordinal) => _fieldTypes[ordinal].Name;

    /// <inheritdoc />
    public override object GetValue(int ordinal) => _values[ordinal];

    /// <inheritdoc />
    public override int GetValues(object[] values)
    {
        int count = Math.Min(values.Length, _values.Length);
        Array.Copy(_values, values, count);
        return count;
    }

    /// <inheritdoc />
    public override bool IsDBNull(int ordinal) => _values[ordinal] is DBNull;

    /// <inheritdoc />
    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    /// <inheritdoc />
    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

    /// <inheritdoc />
    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

    /// <inheritdoc />
    public override char GetChar(int ordinal) => (char)GetValue(ordinal);

    /// <inheritdoc />
    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

    /// <inheritdoc />
    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);

    /// <inheritdoc />
    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);

    /// <inheritdoc />
    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);

    /// <inheritdoc />
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

    /// <inheritdoc />
    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

    /// <inheritdoc />
    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

    /// <inheritdoc />
    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

    /// <inheritdoc />
    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    /// <inheritdoc />
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = (byte[])GetValue(ordinal);
        return CopyOut(bytes, dataOffset, buffer, bufferOffset, length);
    }

    /// <inheritdoc />
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var chars = GetValue(ordinal) switch
        {
            string text => text.ToCharArray(),
            char[] array => array,
            var other => throw new InvalidCastException($"Column {ordinal} holds {other.GetType().Name}, not text."),
        };
        return CopyOut(chars, dataOffset, buffer, bufferOffset, length);
    }

    private static long CopyOut<TElement>(TElement[] source, long dataOffset, TElement[]? buffer, int bufferOffset, int length)
    {
        if (buffer == null)
        {
            return source.Length;
        }

        int count = (int)Math.Max(0, Math.Min(length, source.Length - dataOffset));
        Array.Copy(source, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    #endregion
}
