# Breaking Changes

## SqlBulkCopyOptions Default Changed Back to Default (version 28)

### What Changed?

`BulkConfig.SqlBulkCopyOptions` defaults to `Default` (no options) again, as upstream does. Versions 25 to 27 defaulted to `TableLock`.

### Why?

- `TableLock` takes a lock on the whole table `SqlBulkCopy` writes to (BU on a heap, X on a table with a clustered index) and holds it until the operation's transaction commits, so other sessions that write to the table wait for it. That is the target for a plain insert; `BulkInsertOrUpdate` and `SetOutputIdentity` copy into a temporary table, and their `MERGE` locks the target separately. Where many processes share a table, that turns short inserts into long queues and timeouts.
- Its main gain, minimal logging, needs the SIMPLE or BULK_LOGGED recovery model. Under FULL, which Azure SQL Database always uses, it only saves lock overhead.
- Without it, each statement takes row or page locks on what it writes. SQL Server can still lock the whole table: a statement that takes about 5,000 locks or more on one table is escalated, so a `BatchSize` of 0 or a very large batch can end up locking it, and a table with the `table lock on bulk load` option set is always locked.

### Migration Guide

To keep the table lock, ask for it:

```csharp
var bulkConfig = new BulkConfig { SqlBulkCopyOptions = SqlBulkCopyOptions.TableLock };

await context.BulkInsertAsync(entities, bulkConfig);
```

Code that already sets `SqlBulkCopyOptions` explicitly is not affected.

## SqlBulkCopyOptions Default Changed to TableLock (versions 25 to 27)

Versions 25 to 27 defaulted `BulkConfig.SqlBulkCopyOptions` to `TableLock`. Reverted in 28, see above.
