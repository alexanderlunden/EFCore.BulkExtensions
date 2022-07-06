﻿using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EFCore.BulkExtensions.SQLAdapters.PostgreSql;

/// <summary>
/// Contains a list of methods to generate SQL queries required by EFCore
/// </summary>
public static class SqlQueryBuilderPostgreSql
{
    /// <summary>
    /// Generates SQL query to create table copy
    /// </summary>
    /// <param name="existingTableName"></param>
    /// <param name="newTableName"></param>
    public static string CreateTableCopy(string existingTableName, string newTableName)
    {
        var q = $"CREATE TABLE {newTableName} " +
                $"AS TABLE {existingTableName} " +
                $"WITH NO DATA;";
        q = q.Replace("[", @"""").Replace("]", @"""");
        return q;
    }

    /// <summary>
    /// Generates SQL to copy table columns from STDIN 
    /// </summary>
    /// <param name="tableInfo"></param>
    /// <param name="operationType"></param>
    /// <param name="tableName"></param>
    public static string InsertIntoTable(TableInfo tableInfo, OperationType operationType, string? tableName = null)
    {
        tableName ??= tableInfo.InsertToTempTable ? tableInfo.FullTempTableName : tableInfo.FullTableName;
        tableName = tableName.Replace("[", @"""").Replace("]", @"""");

        var columnsList = GetColumnList(tableInfo, operationType);

        var commaSeparatedColumns = SqlQueryBuilder.GetCommaSeparatedColumns(columnsList).Replace("[", @"""").Replace("]", @"""");

        var q = $"COPY {tableName} " +
                $"({commaSeparatedColumns}) " +
                $"FROM STDIN (FORMAT BINARY)";

        return q + ";";
    }

    /// <summary>
    /// Generates SQL merge statement
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="tableInfo"></param>
    /// <param name="operationType"></param>
    /// <exception cref="NotImplementedException"></exception>
    public static string MergeTable<T>(TableInfo tableInfo, OperationType operationType) where T : class
    {
        var columnsList = GetColumnList(tableInfo, operationType);

        if (operationType == OperationType.InsertOrUpdateOrDelete)
        {
            throw new NotImplementedException($"For Postgres method {OperationType.InsertOrUpdateOrDelete} is not yet supported. Use combination of InsertOrUpdate with Read and Delete");
        }

        string q;
        if (operationType == OperationType.Read)
        {
            var readByColumns = SqlQueryBuilder.GetCommaSeparatedColumns(tableInfo.PrimaryKeysPropertyColumnNameDict.Values.ToList()); //, tableInfo.FullTableName, tableInfo.FullTempTableName

            q = $"SELECT {tableInfo.FullTableName}.* FROM {tableInfo.FullTableName} " +
                $"JOIN {tableInfo.FullTempTableName} " +
                $"USING ({readByColumns})"; //$"ON ({tableInfo.FullTableName}.readByColumns = {tableInfo.FullTempTableName}.readByColumns);";
        }
        else if(operationType == OperationType.Delete)
        {
            var deleteByColumns = SqlQueryBuilder.GetCommaSeparatedColumns(tableInfo.PrimaryKeysPropertyColumnNameDict.Values.ToList(), tableInfo.FullTableName, tableInfo.FullTempTableName);
            deleteByColumns = deleteByColumns.Replace(",", " AND");
            deleteByColumns = deleteByColumns.Replace("[", @"""").Replace("]", @"""");

            q = $"DELETE FROM {tableInfo.FullTableName} " +
                $"USING {tableInfo.FullTempTableName} " +
                $@"WHERE {deleteByColumns}";
        }
        else
        {
            var commaSeparatedColumns = SqlQueryBuilder.GetCommaSeparatedColumns(columnsList).Replace("[", @"""").Replace("]", @"""");

            var updateByColumns = SqlQueryBuilder.GetCommaSeparatedColumns(tableInfo.PrimaryKeysPropertyColumnNameDict.Values.ToList()).Replace("[", @"""").Replace("]", @"""");

            var columnsListEquals = GetColumnList(tableInfo, OperationType.Insert);
            var columnsToUpdate = columnsListEquals.Where(c => tableInfo.PropertyColumnNamesUpdateDict.ContainsValue(c)).ToList();
            var equalsColumns = SqlQueryBuilder.GetCommaSeparatedColumns(columnsToUpdate, equalsTable: "EXCLUDED").Replace("[", @"""").Replace("]", @"""");

            q = $"INSERT INTO {tableInfo.FullTableName} ({commaSeparatedColumns}) " +
                $"(SELECT {commaSeparatedColumns} FROM {tableInfo.FullTempTableName}) " +
                $"ON CONFLICT ({updateByColumns}) " +
                $"DO UPDATE SET {equalsColumns}";

            if (tableInfo.CreatedOutputTable)
            {
                var allColumnsList = tableInfo.PropertyColumnNamesDict.Values.ToList();
                string commaSeparatedColumnsNames = SqlQueryBuilder.GetCommaSeparatedColumns(allColumnsList).Replace("[", @"""").Replace("]", @"""");
                q += $" RETURNING {commaSeparatedColumnsNames}";
            }
        }

        q = q.Replace("[", @"""").Replace("]", @"""");
        q += ";";

        Dictionary<string, string>? sourceDestinationMappings = tableInfo.BulkConfig.CustomSourceDestinationMappingColumns;
        if (tableInfo.BulkConfig.CustomSourceTableName != null && sourceDestinationMappings != null && sourceDestinationMappings.Count > 0)
        {
            var textSelect = "SELECT ";
            var textFrom = " FROM";
            int startIndex = q.IndexOf(textSelect);
            var qSegment = q[startIndex..q.IndexOf(textFrom)];
            var qSegmentUpdated = qSegment;
            foreach (var mapping in sourceDestinationMappings)
            {
                var propertyFormated = $@"""{mapping.Value}""";
                var sourceProperty = mapping.Key;

                if (qSegment.Contains(propertyFormated))
                {
                    qSegmentUpdated = qSegmentUpdated.Replace(propertyFormated, $@"""{sourceProperty}""");
                }
            }
            if (qSegment != qSegmentUpdated)
            {
                q = q.Replace(qSegment, qSegmentUpdated);
            }
        }

        return q;
    }

    /// <summary>
    /// Returns a list of columns for the given table
    /// </summary>
    /// <param name="tableInfo"></param>
    /// <param name="operationType"></param>
    public static List<string> GetColumnList(TableInfo tableInfo, OperationType operationType)
    {
        var tempDict = tableInfo.PropertyColumnNamesDict;
        if (operationType == OperationType.Insert && tableInfo.PropertyColumnNamesDict.Any()) // Only OnInsert omit colums with Default values
        {
            tableInfo.PropertyColumnNamesDict = tableInfo.PropertyColumnNamesDict.Where(a => !tableInfo.DefaultValueProperties.Contains(a.Key)).ToDictionary(a => a.Key, a => a.Value);
        }

        List<string> columnsList = tableInfo.PropertyColumnNamesDict.Values.ToList();
        List<string> propertiesList = tableInfo.PropertyColumnNamesDict.Keys.ToList();

        tableInfo.PropertyColumnNamesDict = tempDict;

        bool keepIdentity = tableInfo.BulkConfig.SqlBulkCopyOptions.HasFlag(SqlBulkCopyOptions.KeepIdentity);
        var uniquColumnName = tableInfo.PrimaryKeysPropertyColumnNameDict.Values.ToList().FirstOrDefault();
        if (!keepIdentity && tableInfo.HasIdentity && (operationType == OperationType.Insert || tableInfo.IdentityColumnName != uniquColumnName))
        {
            var identityPropertyName = tableInfo.PropertyColumnNamesDict.SingleOrDefault(a => a.Value == tableInfo.IdentityColumnName).Key;
            columnsList = columnsList.Where(a => a != tableInfo.IdentityColumnName).ToList();
            propertiesList = propertiesList.Where(a => a != identityPropertyName).ToList();
        }

        return columnsList;
    }

    /// <summary>
    /// Generates SQL query to truncate a table
    /// </summary>
    /// <param name="tableName"></param>
    public static string TruncateTable(string tableName)
    {
        var q = $"TRUNCATE {tableName} RESTART IDENTITY;";
        q = q.Replace("[", @"""").Replace("]", @"""");
        return q;
    }

    /// <summary>
    /// Generates SQL query to drop a table
    /// </summary>
    /// <param name="tableName"></param>
    public static string DropTable(string tableName)
    {
        string q = $"DROP TABLE IF EXISTS {tableName}";
        q = q.Replace("[", @"""").Replace("]", @"""");
        return q;
    }

    /// <summary>
    /// Generates SQL query to count the unique constranints
    /// </summary>
    /// <param name="tableInfo"></param>
    public static string CountUniqueConstrain(TableInfo tableInfo)
    {
        var primaryKeysColumns = tableInfo.PrimaryKeysPropertyColumnNameDict.Values.ToList();

        var q = $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc ";
        foreach (var (pkColumn, index) in primaryKeysColumns.Select((value, i) => (value, i)))
        {
            q = q +
                $"INNER JOIN INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE cu{index} " +
                $"ON cu{index}.CONSTRAINT_NAME = tc.CONSTRAINT_NAME AND cu{index}.COLUMN_NAME = '{pkColumn}' ";
        }

        q = q +
            $"WHERE (tc.CONSTRAINT_TYPE = 'UNIQUE' OR tc.CONSTRAINT_TYPE = 'PRIMARY KEY') " +
            $"AND tc.TABLE_NAME = '{tableInfo.TableName}' ";

        return q;
    }

    /// <summary>
    /// Generate SQL query to create a unique index
    /// </summary>
    /// <param name="tableInfo"></param>
    public static string CreateUniqueIndex(TableInfo tableInfo)
    {
        var tableName = tableInfo.TableName;
        var schemaFormated = tableInfo.Schema == null ? "" : $@"""{tableInfo.Schema}"".";
        var fullTableNameFormated = $@"{schemaFormated}""{tableName}""";

        var uniqueColumnNames = tableInfo.PrimaryKeysPropertyColumnNameDict.Values.ToList();
        var uniqueColumnNamesDash = string.Join("_", uniqueColumnNames);
        var uniqueColumnNamesFormated = @"""" + string.Join(@""", """, uniqueColumnNames) + @"""";
        var schemaDash = tableInfo.Schema == null ? "" : $"{tableInfo.Schema}_";

        var q = $@"CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ""tempUniqueIndex_{schemaDash}{tableName}_{uniqueColumnNamesDash}"" " +
                $@"ON {fullTableNameFormated} ({uniqueColumnNamesFormated})";
        return q;
    }

    /// <summary>
    /// Generates SQL query to create a unique constraint
    /// </summary>
    /// <param name="tableInfo"></param>
    public static string CreateUniqueConstrain(TableInfo tableInfo)
    {
        var tableName = tableInfo.TableName;
        var schemaFormated = tableInfo.Schema == null ? "" : $@"""{tableInfo.Schema}"".";
        var fullTableNameFormated = $@"{schemaFormated}""{tableName}""";

        var uniqueColumnNames = tableInfo.PrimaryKeysPropertyColumnNameDict.Values.ToList();
        var uniqueColumnNamesDash = string.Join("_", uniqueColumnNames);
        var schemaDash = tableInfo.Schema == null ? "" : $"{tableInfo.Schema}_";
        var uniqueConstrainName = $"tempUniqueIndex_{schemaDash}{tableName}_{uniqueColumnNamesDash}";

        var q = $@"ALTER TABLE {fullTableNameFormated} " +
                $@"ADD CONSTRAINT ""{uniqueConstrainName}"" " +
                $@"UNIQUE USING INDEX ""{uniqueConstrainName}""";
        return q;
    }

    /// <summary>
    /// Generates SQL query to drop a unique contstraint
    /// </summary>
    /// <param name="tableInfo"></param>
    public static string DropUniqueConstrain(TableInfo tableInfo)
    {
        var tableName = tableInfo.TableName;
        var schemaFormated = tableInfo.Schema == null ? "" : $@"""{tableInfo.Schema}"".";
        var fullTableNameFormated = $@"{schemaFormated}""{tableName}""";

        var uniqueColumnNames = tableInfo.PrimaryKeysPropertyColumnNameDict.Values.ToList();
        var uniqueColumnNamesDash = string.Join("_", uniqueColumnNames);
        var schemaDash = tableInfo.Schema == null ? "" : $"{tableInfo.Schema}_";
        var uniqueConstrainName = $"tempUniqueIndex_{schemaDash}{tableName}_{uniqueColumnNamesDash}";

        var q = $@"ALTER TABLE {fullTableNameFormated} " +
                $@"DROP CONSTRAINT ""{uniqueConstrainName}"";";
        return q;
    }

    /// <summary>
    /// Restructures a sql query for batch commands
    /// </summary>
    /// <param name="sql"></param>
    /// <param name="isDelete"></param>
    public static string RestructureForBatch(string sql, bool isDelete = false)
    {
        sql = sql.Replace("[", @"""").Replace("]", @"""");
        string firstLetterOfTable = sql.Substring(7, 1);

        if (isDelete)
        {
            //FROM
            // DELETE i FROM "Item" AS i WHERE i."ItemId" <= 1"
            //TO
            // DELETE FROM "Item" AS i WHERE i."ItemId" <= 1"
            //WOULD ALSO WORK
            // DELETE FROM "Item" WHERE "ItemId" <= 1

            sql = sql.Replace($"DELETE {firstLetterOfTable}", "DELETE ");
        }
        else
        {
            //FROM
            // UPDATE i SET "Description" = @Description, "Price\" = @Price FROM "Item" AS i WHERE i."ItemId" <= 1
            //TO
            // UPDATE "Item" AS i SET "Description" = 'Update N', "Price" = 1.5 FROM "Item" WHERE i."ItemId" <= 1
            //WOULD ALSO WORK
            // UPDATE "Item" SET "Description" = 'Update N', "Price" = 1.5 FROM "Item" WHERE "ItemId" <= 1

            string tableAS = sql.Substring(sql.IndexOf("FROM") + 4, sql.IndexOf($"AS {firstLetterOfTable}") - sql.IndexOf("FROM"));
            
            if (!sql.Contains("JOIN"))
            {
                sql = sql.Replace($"AS {firstLetterOfTable}", "");
            }
            else
            {
                int positionFROM = sql.IndexOf("FROM");
                int positionEndJOIN = sql.IndexOf("JOIN ") + "JOIN ".Length;
                int positionON = sql.IndexOf(" ON");
                int positionEndON = positionON + " ON".Length;
                int positionWHERE = sql.IndexOf("WHERE");
                string oldSqlSegment = sql[positionFROM..positionWHERE];
                string newSqlSegment = "FROM " + sql[positionEndJOIN..positionON];
                string equalsPkFk = sql[positionEndON..positionWHERE];
                sql = sql.Replace(oldSqlSegment, newSqlSegment);
                sql = sql.Replace("WHERE", " WHERE");
                sql = sql + " AND" + equalsPkFk;
            }

            sql = sql.Replace($"UPDATE {firstLetterOfTable}", "UPDATE" + tableAS);
        }

        return sql;
    }
}
