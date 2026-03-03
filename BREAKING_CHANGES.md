# Breaking Changes

## SqlBulkCopyOptions Default Changed to TableLock

### What Changed?

The default value for `BulkConfig.SqlBulkCopyOptions` has been changed from `Default` (no options) to `TableLock`.

### Why?

1. **Prevents Deadlocks**: The previous default allowed concurrent access during bulk operations, which could lead to deadlocks when multiple processes access the same table simultaneously.

2. **Better Performance**: Using `TableLock` actually improves performance by reducing logging overhead and lock escalation issues.

3. **Consistency with Industry Standards**: This matches the behavior of Z.EntityFramework.Extensions, which has proven to be a safer default for bulk operations.

### Migration Guide

#### If you want the old behavior (allow concurrent access):

```csharp
var bulkConfig = new BulkConfig 
{ 
    SqlBulkCopyOptions = SqlBulkCopyOptions.Default 
};

await context.BulkInsertAsync(entities, bulkConfig);
```

#### If you need to combine with other options (e.g., KeepIdentity):

```csharp
var bulkConfig = new BulkConfig 
{ 
    SqlBulkCopyOptions = SqlBulkCopyOptions.TableLock | SqlBulkCopyOptions.KeepIdentity 
};

await context.BulkInsertAsync(entities, bulkConfig);
```

#### If you don't specify BulkConfig (new default behavior):

```csharp
// This now uses TableLock by default
await context.BulkInsertAsync(entities);
```

### Impact

- **Positive**: Applications experiencing deadlocks during bulk operations will now be more stable by default.
- **Potential Issue**: If your application relies on concurrent access to tables during bulk operations, you may experience blocking. In such cases, explicitly set `SqlBulkCopyOptions = SqlBulkCopyOptions.Default`.

### Testing

A new unit test `BulkConfigDefaultsSqlBulkCopyOptionsToTableLock()` has been added to verify this default behavior.

### References

- See the discussion about deadlock issues: [Original issue #46](https://github.com/borisdj/EFCore.BulkExtensions/issues/46)
- SQL Server documentation on [SqlBulkCopyOptions](https://docs.microsoft.com/en-us/dotnet/api/system.data.sqlclient.sqlbulkcopyoptions)
