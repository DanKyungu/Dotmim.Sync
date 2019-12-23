using Dotmim.Sync.Data;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Manager;
using Dotmim.Sync.Messages;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Dotmim.Sync
{
    public abstract partial class CoreProvider
    {
        /// <summary>
        /// Generate the DmTable configuration from a given columns list
        /// Validate that all columns are currently supported by the provider
        /// </summary>
        private void ValidateTableFromColumns(SyncTable schemaTable, IEnumerable<SyncColumn> columns, SyncSet schema, IDbManagerTable dbManagerTable)
        {
            schemaTable.OriginalProvider = this.ProviderTypeName;

            var ordinal = 0;

            // Eventually, do not raise exception here, just we don't have any columns
            if (columns == null || columns.Any() == false)
                return;

            // Get PrimaryKey
            var schemaPrimaryKeys = dbManagerTable.GetTablePrimaryKeys();

            if (schemaPrimaryKeys == null || schemaPrimaryKeys.Any() == false)
                throw new MissingPrimaryKeyException($"No Primary Keys in table {schemaTable.TableName}, Can't make a synchronization with a table without primary keys.");

            //// Check if we have more than one column (excepting primarykeys)
            //var columnsNotPkeys = columns.Count(c => !dmTableKeys.Contains(c.ColumnName));

            //if (columnsNotPkeys <= 0)
            //    throw new NotSupportedException($"{dmTable.TableName} does not contains any columns, excepting primary keys.");

            // Delete all existing columns
            if (schemaTable.PrimaryKeys.Count > 0)
                schemaTable.PrimaryKeys.Clear();

            if (schemaTable.Columns.Count > 0)
                schemaTable.Columns.Clear();

            foreach (var column in columns.OrderBy(c => c.Ordinal))
            {
                // First of all validate if the column is currently supported
                if (!this.Metadata.IsValid(column))
                    throw new NotSupportedException($"The Column {column.ColumnName} of type {column.OriginalTypeName} from provider {this.ProviderTypeName} is not currently supported.");

                var columnNameLower = column.ColumnName.ToLowerInvariant();
                if (columnNameLower == "sync_scope_name"
                    || columnNameLower == "scope_timestamp"
                    || columnNameLower == "scope_is_local"
                    || columnNameLower == "scope_last_sync"
                    || columnNameLower == "create_scope_id"
                    || columnNameLower == "update_scope_id"
                    || columnNameLower == "create_timestamp"
                    || columnNameLower == "update_timestamp"
                    || columnNameLower == "timestamp"
                    || columnNameLower == "sync_row_is_tombstone"
                    || columnNameLower == "last_change_datetime"
                    || columnNameLower == "sync_scope_name"
                    || columnNameLower == "sync_scope_name"
                    )
                    throw new NotSupportedException($"The Column name {column.ColumnName} from provider {this.ProviderTypeName} is a reserved column name. Please choose another column name.");

                schemaTable.Columns.Add(column);

                // Validate max length
                column.MaxLength = this.Metadata.ValidateMaxLength(column.OriginalTypeName, column.IsUnsigned, column.IsUnicode, column.MaxLength);

                // Gets the datastore owner dbType (could be SqlDbtype, MySqlDbType, SqliteDbType, NpgsqlDbType & so on ...)
                var datastoreDbType = this.Metadata.ValidateOwnerDbType(column.OriginalTypeName, column.IsUnsigned, column.IsUnicode, column.MaxLength);

                // once we have the datastore type, we can have the managed type
                var columnType = this.Metadata.ValidateType(datastoreDbType);

                // and the DbType
                column.DbType = (int)this.Metadata.ValidateDbType(column.OriginalTypeName, column.IsUnsigned, column.IsUnicode, column.MaxLength);

                // Gets the owner dbtype (SqlDbType, OracleDbType, MySqlDbType, NpsqlDbType & so on ...)
                // Sqlite does not have it's own type, so it's DbType too
                column.OriginalDbType = datastoreDbType.ToString();

                // Validate if column should be readonly
                column.IsReadOnly = this.Metadata.ValidateIsReadonly(column);

                // set position ordinal
                column.SetOrdinal(ordinal);
                ordinal++;

                // Validate the precision and scale properties
                if (this.Metadata.IsNumericType(column.OriginalTypeName))
                {
                    if (this.Metadata.SupportScale(column.OriginalTypeName))
                    {
                        var (p, s) = this.Metadata.ValidatePrecisionAndScale(column);
                        column.Precision = p;
                        column.PrecisionSpecified = true;
                        column.Scale = s;
                        column.ScaleSpecified = true;
                    }
                    else
                    {
                        column.Precision = this.Metadata.ValidatePrecision(column);
                        column.PrecisionSpecified = true;
                        column.ScaleSpecified = false;
                    }

                }

            }

            // Set the primary Key
            foreach (var rowColumn in schemaPrimaryKeys.OrderBy(r => r.Ordinal))
            {
                var columnKey = schemaTable.Columns.FirstOrDefault(rowColumn.ColumnName);

                if (columnKey == null)
                    throw new ArgumentException($"Column {rowColumn.ColumnName} does not exist in {schemaTable.TableName}");

                schemaTable.PrimaryKeys.Add(columnKey.ColumnName);
            }

        }

        /// <summary>
        /// update configuration object with tables desc from server database
        /// </summary>
        private void ReadSchema(SyncSet schema, DbConnection connection, DbTransaction transaction)
        {
            if (schema == null || schema.Tables.Count <= 0)
                throw new ArgumentNullException("syncConfiguration", "Configuration should contains Tables, at least tables with a name");

            var relations = new List<DbRelationDefinition>(20);
            var schemaTables = schema.Tables;

            foreach (var schemaTable in schemaTables)
            {
                var builderTable = this.GetDbManager(schemaTable.TableName, schemaTable.SchemaName);
                var tblManager = builderTable.CreateManagerTable(connection, transaction);

                // get columns list
                var lstColumns = tblManager.GetTableDefinition();

                // Validate the column list and get the dmTable configuration object.
                this.ValidateTableFromColumns(schemaTable, lstColumns, schema, tblManager);

                relations.AddRange(tblManager.GetTableRelations());
            }

            if (relations.Any())
            {
                foreach (var r in relations)
                {
                    // Get table from the relation where we need to work on
                    var schemaTable = schema.Tables[r.TableName, r.SchemaName];

                    // get SchemaColumn from SchemaTable, based on the columns from relations
                    var schemaColumns = r.Columns.OrderBy(kc => kc.Order)
                        .Select(kc => {
                            var schemaColumn = schemaTable.Columns[kc.KeyColumnName];
                            return new SyncColumnIdentifier(schemaColumn.ColumnName, schemaTable.TableName, schemaTable.SchemaName);
                        });

                    // then Get the foreign table as well
                    var foreignTable = schemaTables[r.ReferenceTableName, r.ReferenceSchemaName];

                    // Since we can have a table with a foreign key but not the parent table
                    // It's not a problem, just forget it
                    if (foreignTable == null || foreignTable.Columns.Count == 0)
                        continue;

                    var foreignColumns = r.Columns.OrderBy(kc => kc.Order)
                         .Select(fc =>
                         {
                             var schemaColumn = foreignTable.Columns[fc.ReferenceColumnName];
                             return new SyncColumnIdentifier(schemaColumn.ColumnName, foreignTable.TableName, foreignTable.SchemaName);
                         });


                    if (foreignColumns == null || foreignColumns.Any(c => c == null))
                        throw new NotSupportedException(
                            $"Foreign columns {string.Join(",", r.Columns.Select(kc => kc.ReferenceColumnName))} does not exist in table {r.ReferenceTableName}");

                    var schemaRelation = new SyncRelation(r.ForeignKey, schemaColumns.ToList(), foreignColumns.ToList());

                    schema.Relations.Add(schemaRelation);
                }
            }

        }

        /// <summary>
        /// Ensure configuration is correct on both server and client side
        /// </summary>
        public virtual async Task<(SyncContext, SyncSet)> EnsureSchemaAsync(SyncContext context, SyncSet schema,
                             DbConnection connection, DbTransaction transaction,
                             CancellationToken cancellationToken, IProgress<ProgressArgs> progress = null)
        {
            try
            {
                context.SyncStage = SyncStage.SchemaReading;

                // if we dont have already read the tables || we want to overwrite the current config
                if (schema.HasTables && !schema.HasColumns)
                    this.ReadSchema(schema, connection, transaction);

                // Progress & Interceptor
                context.SyncStage = SyncStage.SchemaRead;
                var schemaArgs = new SchemaArgs(context, schema, connection, transaction);
                this.ReportProgress(context, progress, schemaArgs);
                await this.InterceptAsync(schemaArgs).ConfigureAwait(false);

                return (context, schema);
            }
            catch (SyncException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new SyncException(ex, SyncStage.SchemaReading);
            }
        }


    }
}
