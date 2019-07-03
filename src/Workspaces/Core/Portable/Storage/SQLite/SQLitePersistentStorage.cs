﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.SQLite.Interop;
using Microsoft.CodeAnalysis.Storage;

namespace Microsoft.CodeAnalysis.SQLite
{
    /// <summary>
    /// Implementation of an <see cref="IPersistentStorage"/> backed by SQLite.
    /// </summary>
    internal partial class SQLitePersistentStorage : AbstractPersistentStorage
    {
        // Version history.
        // 1. Initial use of sqlite as the persistence layer.  Simple key->value storage tables.
        // 2. Updated to store checksums.  Tables now key->(checksum,value).  Allows for reading
        //    and validating checksums without the overhead of reading the full 'value' into
        //    memory.
        // 3. Use an in-memory DB to cache writes before flushing to disk.
        private const string Version = "3";

        /// <summary>
        /// Inside the DB we have a table dedicated to storing strings that also provides a unique 
        /// integral ID per string.  This allows us to store data keyed in a much more efficient
        /// manner as we can use those IDs instead of duplicating strings all over the place.  For
        /// example, there may be many pieces of data associated with a file.  We don't want to 
        /// key off the file path in all these places as that would cause a large amount of bloat.
        /// 
        /// Because the string table can map from arbitrary strings to unique IDs, it can also be
        /// used to create IDs for compound objects.  For example, given the IDs for the FilePath
        /// and Name of a Project, we can get an ID that represents the project itself by just
        /// creating a compound key of those two IDs.  This ID can then be used in other compound
        /// situations.  For example, a Document's ID is creating by compounding its Project's 
        /// ID, along with the IDs for the Document's FilePath and Name.
        /// 
        /// The format of the table is:
        /// 
        ///  StringInfo
        ///  --------------------------------------------------------------
        ///  | Id (integer, primary key, auto increment) | Data (varchar) |
        ///  --------------------------------------------------------------
        /// </summary>
        private const string StringInfoTableName = "StringInfo" + Version;

        /// <summary>
        /// Inside the DB we have a table for data corresponding to the <see cref="Solution"/>.  The 
        /// data is just a blob that is keyed by a string Id.  Data with this ID can be retrieved
        /// or overwritten.
        /// 
        /// The format of the table is:
        /// 
        ///  SolutionData
        ///  -------------------------------------------------------------------
        ///  | DataId (primary key, varchar) | | Checksum (blob) | Data (blob) |
        ///  -------------------------------------------------------------------
        /// </summary>
        private const string SolutionDataTableName = "SolutionData" + Version;

        /// <summary>
        /// Inside the DB we have a table for data that we want associated with a <see cref="Project"/>.
        /// The data is keyed off of an integral value produced by combining the ID of the Project and
        /// the ID of the name of the data (see <see cref="SQLitePersistentStorage.ReadStreamAsync(Project, string, Checksum, CancellationToken)"/>.
        /// 
        /// This gives a very efficient integral key, and means that the we only have to store a 
        /// single mapping from stream name to ID in the string table.
        /// 
        /// The format of the table is:
        /// 
        ///  ProjectData
        ///  -------------------------------------------------------------------
        ///  | DataId (primary key, integer) | | Checksum (blob) | Data (blob) |
        ///  -------------------------------------------------------------------
        /// </summary>
        private const string ProjectDataTableName = "ProjectData" + Version;

        /// <summary>
        /// Inside the DB we have a table for data that we want associated with a <see cref="Document"/>.
        /// The data is keyed off of an integral value produced by combining the ID of the Document and
        /// the ID of the name of the data (see <see cref="SQLitePersistentStorage.ReadStreamAsync(Document, string, Checksum, CancellationToken)"/>.
        /// 
        /// This gives a very efficient integral key, and means that the we only have to store a 
        /// single mapping from stream name to ID in the string table.
        /// 
        /// The format of the table is:
        /// 
        ///  DocumentData
        ///  -------------------------------------------------------------------
        ///  | DataId (primary key, integer) | | Checksum (blob) | Data (blob) |
        ///  -------------------------------------------------------------------
        /// </summary>
        private const string DocumentDataTableName = "DocumentData" + Version;

        private const string DataIdColumnName = "DataId";
        private const string ChecksumColumnName = "Checksum";
        private const string DataColumnName = "Data";

        /// <summary>
        /// Name of the on-disk db.  "main" is the default that sqlite uses.  This just allows us to
        /// be explicit that we want this db.
        /// </summary>
        private const string MainDBName = "main";

        /// <summary>
        /// Name for the in-memory write-cache db.  Writes will be staged there and will be periodically
        /// flushed to the real on-disk db to help with perf.
        /// </summary>
        private const string WriteCacheDBName = "writecache";

        private readonly CancellationTokenSource _shutdownTokenSource = new CancellationTokenSource();

        private readonly IDisposable _dbOwnershipLock;
        private readonly IPersistentStorageFaultInjector _faultInjectorOpt;

        // Accessors that allow us to retrieve/store data into specific DB tables.  The
        // core Accessor type has logic that we to share across all reading/writing, while
        // the derived types contain only enough logic to specify how to read/write from
        // their respective tables.

        private readonly SolutionAccessor _solutionAccessor;
        private readonly ProjectAccessor _projectAccessor;
        private readonly DocumentAccessor _documentAccessor;

        // cached query strings

        private readonly string _select_star_from_main_0;
        private readonly string _insert_into_main_0_1_values;
        private readonly string _select_star_from_main_0_where_1_limit_one;

        // We pool connections to the DB so that we don't have to take the hit of 
        // reconnecting.  The connections also cache the prepared statements used
        // to get/set data from the db.  A connection is safe to use by one thread
        // at a time, but is not safe for simultaneous use by multiple threads.
        private readonly object _connectionGate = new object();
        private readonly Stack<SqlConnection> _connectionsPool = new Stack<SqlConnection>();

        public SQLitePersistentStorage(
            string workingFolderPath,
            string solutionFilePath,
            string databaseFile,
            IDisposable dbOwnershipLock,
            IPersistentStorageFaultInjector faultInjectorOpt)
            : base(workingFolderPath, solutionFilePath, databaseFile)
        {
            _dbOwnershipLock = dbOwnershipLock;
            _faultInjectorOpt = faultInjectorOpt;

            _solutionAccessor = new SolutionAccessor(this);
            _projectAccessor = new ProjectAccessor(this);
            _documentAccessor = new DocumentAccessor(this);

            _select_star_from_main_0 = $@"select * from ""{MainDBName}.{StringInfoTableName}""";
            _insert_into_main_0_1_values = $@"insert into ""{MainDBName}.{StringInfoTableName}""(""{DataColumnName}"") values (?)";
            _select_star_from_main_0_where_1_limit_one = $@"select * from ""{MainDBName}.{StringInfoTableName}"" where (""{DataColumnName}"" = ?) limit 1";
        }

        private SqlConnection GetConnection()
        {
            lock (_connectionGate)
            {
                // If we have an available connection, just return that.
                if (_connectionsPool.Count > 0)
                {
                    return _connectionsPool.Pop();
                }
            }

            // Otherwise create a new connection.
            return SqlConnection.Create(_faultInjectorOpt, DatabaseFile);
        }

        private void ReleaseConnection(SqlConnection connection)
        {
            lock (_connectionGate)
            {
                // If we've been asked to shutdown, then don't actually add the connection back to 
                // the pool.  Instead, just close it as we no longer need it.
                if (_shutdownTokenSource.IsCancellationRequested)
                {
                    connection.Close_OnlyForUseBySqlPersistentStorage();
                    return;
                }

                _connectionsPool.Push(connection);
            }
        }

        public override void Dispose()
        {
            // Flush all pending writes so that all data our features wanted written
            // are definitely persisted to the DB.
            try
            {
                CloseWorker();
            }
            finally
            {
                // let the lock go
                _dbOwnershipLock.Dispose();
            }
        }

        private void CloseWorker()
        {
            // Flush all pending writes so that all data our features wanted written
            // are definitely persisted to the DB.
            try
            {
                FlushInMemoryDataToDisk();
            }
            catch (Exception e)
            {
                // Flushing may fail.  We still have to close all our connections.
                StorageDatabaseLogger.LogException(e);
            }

            lock (_connectionGate)
            {
                // Notify any outstanding async work that it should stop.
                _shutdownTokenSource.Cancel();

                // Go through all our pooled connections and close them.
                while (_connectionsPool.Count > 0)
                {
                    var connection = _connectionsPool.Pop();
                    connection.Close_OnlyForUseBySqlPersistentStorage();
                }
            }
        }

        /// <summary>
        /// Gets a <see cref="SqlConnection"/> from the connection pool, or creates one if none are available.
        /// </summary>
        /// <remarks>
        /// Database connections have a large amount of overhead, and should be returned to the pool when they are no
        /// longer in use. In particular, make sure to avoid letting a connection lease cross an <see langword="await"/>
        /// boundary, as it will prevent code in the asynchronous operation from using the existing connection.
        /// </remarks>
        private PooledConnection GetPooledConnection()
            => new PooledConnection(this, GetConnection());

        public void Initialize(Solution solution)
        {
            // Create a connection to the DB and ensure it has tables for the types we care about. 
            using (var pooledConnection = GetPooledConnection())
            {
                var connection = pooledConnection.Connection;

                // Enable write-ahead logging to increase write performance by reducing amount of disk writes,
                // by combining writes at checkpoint, salong with using sequential-only writes to populate the log.
                // Also, WAL allows for relaxed ("normal") "synchronous" mode, see below.
                connection.ExecuteCommand("pragma journal_mode=wal", throwOnError: false);

                // Set "synchronous" mode to "normal" instead of default "full" to reduce the amount of buffer flushing syscalls,
                // significantly reducing both the blocked time and the amount of context switches.
                // When coupled with WAL, this (according to https://sqlite.org/pragma.html#pragma_synchronous and 
                // https://www.sqlite.org/wal.html#performance_considerations) is unlikely to significantly affect durability,
                // while significantly increasing performance, because buffer flushing is done for each checkpoint, instead of each
                // transaction. While some writes can be lost, they are never reordered, and higher layers will recover from that.
                connection.ExecuteCommand("pragma synchronous=normal", throwOnError: false);

                CreateTables(connection, MainDBName);

                // Also get the known set of string-to-id mappings we already have in the DB.
                // Do this in one batch if possible.
                var fetched = TryFetchStringTableFromMainDB(connection);

                // If we weren't able to retrieve the entire string table in one batch,
                // attempt to retrieve it for each 
                var fetchStringTable = !fetched;

                // Try to bulk populate all the IDs we'll need for strings/projects/documents.
                // Bulk population is much faster than trying to do everything individually.
                BulkPopulateIds(connection, solution, fetchStringTable);

                // Create and initialize the in-memory write-cache.
                // From: https://www.sqlite.org/sharedcache.html
                // Enabling shared-cache for an in-memory database allows two or more database 
                // connections in the same process to have access to the same in-memory database.
                // An in-memory database in shared cache is automatically deleted and memory is
                // reclaimed when the last connection to that database closes.

                connection.ExecuteCommand($"ATTACH DATABASE 'file::memory:?cache=shared' AS ${WriteCacheDBName};");
                CreateTables(connection, WriteCacheDBName);
            }
        }

        private static void CreateTables(SqlConnection connection, string tableName)
        {
            // First, create all our tables
            connection.ExecuteCommand(
$@"create table if not exists ""{tableName}.{StringInfoTableName}"" (
    ""{DataIdColumnName}"" integer primary key autoincrement not null,
    ""{DataColumnName}"" varchar)");

            // Ensure that the string-info table's 'Value' column is defined to be 'unique'.
            // We don't allow duplicate strings in this table.
            connection.ExecuteCommand(
$@"create unique index if not exists ""{tableName}.{StringInfoTableName}_{DataColumnName}"" on ""{StringInfoTableName}""(""{DataColumnName}"")");

            connection.ExecuteCommand(
$@"create table if not exists ""{tableName}.{SolutionDataTableName}"" (
    ""{DataIdColumnName}"" varchar primary key not null,
    ""{ChecksumColumnName}"" blob,
    ""{DataColumnName}"" blob)");

            connection.ExecuteCommand(
$@"create table if not exists ""{tableName}.{ProjectDataTableName}"" (
    ""{DataIdColumnName}"" integer primary key not null,
    ""{ChecksumColumnName}"" blob,
    ""{DataColumnName}"" blob)");

            connection.ExecuteCommand(
$@"create table if not exists ""{tableName}.{DocumentDataTableName}"" (
    ""{DataIdColumnName}"" integer primary key not null,
    ""{ChecksumColumnName}"" blob,
    ""{DataColumnName}"" blob)");
        }
    }
}
