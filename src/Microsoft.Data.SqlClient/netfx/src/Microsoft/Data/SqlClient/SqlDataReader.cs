// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Data.Common;
using Microsoft.Data.ProviderBase;
using Microsoft.Data.SqlClient.DataClassification;
using Microsoft.Data.SqlClient.Server;
using Microsoft.Data.SqlTypes;

namespace Microsoft.Data.SqlClient
{
    /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/SqlDataReader/*' />
    public class SqlDataReader : DbDataReader, IDataReader
    {

        private enum ALTROWSTATUS
        {
            Null = 0,           // default and after Done
            AltRow,             // after calling NextResult and the first AltRow is available for read
            Done,               // after consuming the value (GetValue -> GetValueInternal)
        }

        internal class SharedState
        { // parameters needed to execute cleanup from parser
            internal int _nextColumnHeaderToRead;
            internal int _nextColumnDataToRead;
            internal long _columnDataBytesRemaining;
            internal bool _dataReady; // ready to ProcessRow
        }

        internal SharedState _sharedState = new SharedState();

        private TdsParser _parser;                 // TODO: Probably don't need this, since it's on the stateObj
        private TdsParserStateObject _stateObj;
        private SqlCommand _command;
        private SqlConnection _connection;
        private int _defaultLCID;
        private bool _haltRead;               // bool to denote whether we have read first row for single row behavior
        private bool _metaDataConsumed;
        private bool _browseModeInfoConsumed;
        private bool _isClosed;
        private bool _isInitialized;          // Webdata 104560
        private bool _hasRows;
        private ALTROWSTATUS _altRowStatus;
        private int _recordsAffected = -1;
        private long _defaultTimeoutMilliseconds;
        private SqlConnectionString.TypeSystem _typeSystem;

        // SQLStatistics support
        private SqlStatistics _statistics;
        private SqlBuffer[] _data;         // row buffer, filled in by ReadColumnData()
        private SqlStreamingXml _streamingXml; // Used by Getchars on an Xml column for sequential access

        // buffers and metadata
        private _SqlMetaDataSet _metaData;                 // current metaData for the stream, it is lazily loaded
        private _SqlMetaDataSetCollection _altMetaDataSetCollection;
        private FieldNameLookup _fieldNameLookup;
        private CommandBehavior _commandBehavior;

        private static int _objectTypeCount; // EventSource Counter
        internal readonly int ObjectID = System.Threading.Interlocked.Increment(ref _objectTypeCount);

        // context
        // undone: we may still want to do this...it's nice to pass in an lpvoid (essentially) and just have the reader keep the state
        // private object _context = null; // this is never looked at by the stream object.  It is used by upper layers who wish
        // to remain stateless

        // metadata (no explicit table, use 'Table')
        private MultiPartTableName[] _tableNames = null;
        private string _resetOptionsString;

        private int _lastColumnWithDataChunkRead;
        private long _columnDataBytesRead;       // last byte read by user
        private long _columnDataCharsRead;       // last char read by user
        private char[] _columnDataChars;
        private int _columnDataCharsIndex;      // Column index that is currently loaded in _columnDataChars

        private Task _currentTask;
        private Snapshot _snapshot;
        private CancellationTokenSource _cancelAsyncOnCloseTokenSource;
        private CancellationToken _cancelAsyncOnCloseToken;

        private SqlSequentialStream _currentStream;
        private SqlSequentialTextReader _currentTextReader;

        private IsDBNullAsyncCallContext _cachedIsDBNullContext;
        private ReadAsyncCallContext _cachedReadAsyncContext;

        internal SqlDataReader(SqlCommand command, CommandBehavior behavior)
        {
            SqlConnection.VerifyExecutePermission();

            _command = command;
            _commandBehavior = behavior;
            if (_command != null)
            {
                _defaultTimeoutMilliseconds = (long)command.CommandTimeout * 1000L;
                _connection = command.Connection;
                if (_connection != null)
                {
                    _statistics = _connection.Statistics;
                    _typeSystem = _connection.TypeSystem;
                }
            }
            _sharedState._dataReady = false;
            _metaDataConsumed = false;
            _hasRows = false;
            _browseModeInfoConsumed = false;
            _currentStream = null;
            _currentTextReader = null;
            _cancelAsyncOnCloseTokenSource = new CancellationTokenSource();
            _cancelAsyncOnCloseToken = _cancelAsyncOnCloseTokenSource.Token;
            _columnDataCharsIndex = -1;
        }

        internal bool BrowseModeInfoConsumed
        {
            set
            {
                _browseModeInfoConsumed = value;
            }
        }

        internal SqlCommand Command
        {
            get
            {
                return _command;
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/Connection/*' />
        protected SqlConnection Connection
        {
            get
            {
                return _connection;
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/SensitivityClassification/*' />
        public SensitivityClassification SensitivityClassification { get; internal set; }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/Depth/*' />
        override public int Depth
        {
            get
            {
                if (this.IsClosed)
                {
                    throw ADP.DataReaderClosed("Depth");
                }

                return 0;
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/FieldCount/*' />
        // fields/attributes collection
        override public int FieldCount
        {
            get
            {
                if (this.IsClosed)
                {
                    throw ADP.DataReaderClosed("FieldCount");
                }
                if (_currentTask != null)
                {
                    throw ADP.AsyncOperationPending();
                }

                if (MetaData == null)
                {
                    return 0;
                }

                return _metaData.Length;
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/HasRows/*' />
        override public bool HasRows
        {
            get
            {
                if (this.IsClosed)
                {
                    throw ADP.DataReaderClosed("HasRows");
                }
                if (_currentTask != null)
                {
                    throw ADP.AsyncOperationPending();
                }

                return _hasRows;
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/IsClosed/*' />
        override public bool IsClosed
        {
            get
            {
                return _isClosed;
            }
        }

        internal bool IsInitialized
        {
            get
            {
                return _isInitialized;
            }
            set
            {
                Debug.Assert(value, "attempting to uninitialize a data reader?");
                _isInitialized = value;
            }
        }

        // NOTE: For PLP values this indicates the amount of data left in the current chunk (or 0 if there are no more chunks left)
        internal long ColumnDataBytesRemaining()
        {
            // If there are an unknown (-1) number of bytes left for a PLP, read its size
            if (-1 == _sharedState._columnDataBytesRemaining)
            {
                _sharedState._columnDataBytesRemaining = (long)_parser.PlpBytesLeft(_stateObj);
            }

            return _sharedState._columnDataBytesRemaining;
        }

        internal _SqlMetaDataSet MetaData
        {
            get
            {
                if (IsClosed)
                {
                    throw ADP.DataReaderClosed("MetaData");
                }
                // metaData comes in pieces: colmetadata, tabname, colinfo, etc
                // if we have any metaData, return it.  If we have none,
                // then fetch it
                if (_metaData == null && !_metaDataConsumed)
                {
                    if (_currentTask != null)
                    {
                        throw SQL.PendingBeginXXXExists();
                    }

                    RuntimeHelpers.PrepareConstrainedRegions();
                    try
                    {
#if DEBUG
                        TdsParser.ReliabilitySection tdsReliabilitySection = new TdsParser.ReliabilitySection();

                        RuntimeHelpers.PrepareConstrainedRegions();
                        try
                        {
                            tdsReliabilitySection.Start();
#else
                        {
#endif //DEBUG

                            Debug.Assert(_stateObj == null || _stateObj._syncOverAsync, "Should not attempt pends in a synchronous call");
                            if (!TryConsumeMetaData())
                            {
                                throw SQL.SynchronousCallMayNotPend();
                            }
                        }
#if DEBUG
                        finally
                        {
                            tdsReliabilitySection.Stop();
                        }
#endif //DEBUG
                    }
                    catch (System.OutOfMemoryException e)
                    {
                        _isClosed = true;
                        if (null != _connection)
                        {
                            _connection.Abort(e);
                        }
                        throw;
                    }
                    catch (System.StackOverflowException e)
                    {
                        _isClosed = true;
                        if (null != _connection)
                        {
                            _connection.Abort(e);
                        }
                        throw;
                    }
                    catch (System.Threading.ThreadAbortException e)
                    {
                        _isClosed = true;
                        if (null != _connection)
                        {
                            _connection.Abort(e);
                        }
                        throw;
                    }
                }
                return _metaData;
            }
        }

        internal virtual SmiExtendedMetaData[] GetInternalSmiMetaData()
        {
            SmiExtendedMetaData[] metaDataReturn = null;
            _SqlMetaDataSet metaData = this.MetaData;

            if (null != metaData && 0 < metaData.Length)
            {
                metaDataReturn = new SmiExtendedMetaData[metaData.visibleColumns];

                for (int index = 0; index < metaData.Length; index++)
                {
                    _SqlMetaData colMetaData = metaData[index];

                    if (!colMetaData.IsHidden)
                    {
                        SqlCollation collation = colMetaData.collation;

                        string typeSpecificNamePart1 = null;
                        string typeSpecificNamePart2 = null;
                        string typeSpecificNamePart3 = null;

                        if (SqlDbType.Xml == colMetaData.type)
                        {
                            typeSpecificNamePart1 = colMetaData.xmlSchemaCollection?.Database;
                            typeSpecificNamePart2 = colMetaData.xmlSchemaCollection?.OwningSchema;
                            typeSpecificNamePart3 = colMetaData.xmlSchemaCollection?.Name;
                        }
                        else if (SqlDbType.Udt == colMetaData.type)
                        {
                            Connection.CheckGetExtendedUDTInfo(colMetaData, true);    // SQLBUDT #370593 ensure that colMetaData.udtType is set

                            typeSpecificNamePart1 = colMetaData.udt?.DatabaseName;
                            typeSpecificNamePart2 = colMetaData.udt?.SchemaName;
                            typeSpecificNamePart3 = colMetaData.udt?.TypeName;
                        }

                        int length = colMetaData.length;
                        if (length > TdsEnums.MAXSIZE)
                        {
                            length = (int)SmiMetaData.UnlimitedMaxLengthIndicator;
                        }
                        else if (SqlDbType.NChar == colMetaData.type
                                || SqlDbType.NVarChar == colMetaData.type)
                        {
                            length /= ADP.CharSize;
                        }

                        metaDataReturn[index] = new SmiQueryMetaData(
                                                        colMetaData.type,
                                                        length,
                                                        colMetaData.precision,
                                                        colMetaData.scale,
                                                        (null != collation) ? collation.LCID : _defaultLCID,
                                                        (null != collation) ? collation.SqlCompareOptions : SqlCompareOptions.None,
                                                        colMetaData.udt?.Type,
                                                        false,  // isMultiValued
                                                        null,   // fieldmetadata
                                                        null,   // extended properties
                                                        colMetaData.column,
                                                        typeSpecificNamePart1,
                                                        typeSpecificNamePart2,
                                                        typeSpecificNamePart3,
                                                        colMetaData.IsNullable,
                                                        colMetaData.serverName,
                                                        colMetaData.catalogName,
                                                        colMetaData.schemaName,
                                                        colMetaData.tableName,
                                                        colMetaData.baseColumn,
                                                        colMetaData.IsKey,
                                                        colMetaData.IsIdentity,
                                                        colMetaData.IsReadOnly,
                                                        colMetaData.IsExpression,
                                                        colMetaData.IsDifferentName,
                                                        colMetaData.IsHidden
                                                        );
                    }
                }
            }

            return metaDataReturn;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/RecordsAffected/*' />
        override public int RecordsAffected
        {
            get
            {
                if (null != _command)
                    return _command.InternalRecordsAffected;

                // cached locally for after Close() when command is nulled out
                return _recordsAffected;
            }
        }

        internal string ResetOptionsString
        {
            set
            {
                _resetOptionsString = value;
            }
        }

        private SqlStatistics Statistics
        {
            get
            {
                return _statistics;
            }
        }

        internal MultiPartTableName[] TableNames
        {
            get
            {
                return _tableNames;
            }
            set
            {
                _tableNames = value;
            }
        }
        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/VisibleFieldCount/*' />
        override public int VisibleFieldCount
        {
            get
            {
                if (this.IsClosed)
                {
                    throw ADP.DataReaderClosed("VisibleFieldCount");
                }
                _SqlMetaDataSet md = this.MetaData;
                if (md == null)
                {
                    return 0;
                }
                return (md.visibleColumns);
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/ItemI/*' />
        // this operator
        override public object this[int i]
        {
            get
            {
                return GetValue(i);
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/ItemName/*' />
        override public object this[string name]
        {
            get
            {
                return GetValue(GetOrdinal(name));
            }
        }

        internal void Bind(TdsParserStateObject stateObj)
        {
            Debug.Assert(null != stateObj, "null stateobject");

            Debug.Assert(null == _snapshot, "Should not change during execution of asynchronous command");

            stateObj.Owner = this;
            _stateObj = stateObj;
            _parser = stateObj.Parser;
            _defaultLCID = _parser.DefaultLCID;
        }

        // Fills in a schema table with meta data information.  This function should only really be called by
        // UNDONE: need a way to refresh the table with more information as more data comes online for browse info like
        // table names and key information
        internal DataTable BuildSchemaTable()
        {
            _SqlMetaDataSet md = this.MetaData;
            Debug.Assert(null != md, "BuildSchemaTable - unexpected null metadata information");

            DataTable schemaTable = new DataTable("SchemaTable");
            schemaTable.Locale = CultureInfo.InvariantCulture;
            schemaTable.MinimumCapacity = md.Length;

            DataColumn ColumnName = new DataColumn(SchemaTableColumn.ColumnName, typeof(System.String));
            DataColumn Ordinal = new DataColumn(SchemaTableColumn.ColumnOrdinal, typeof(System.Int32));
            DataColumn Size = new DataColumn(SchemaTableColumn.ColumnSize, typeof(System.Int32));
            DataColumn Precision = new DataColumn(SchemaTableColumn.NumericPrecision, typeof(System.Int16));
            DataColumn Scale = new DataColumn(SchemaTableColumn.NumericScale, typeof(System.Int16));

            DataColumn DataType = new DataColumn(SchemaTableColumn.DataType, typeof(System.Type));
            DataColumn ProviderSpecificDataType = new DataColumn(SchemaTableOptionalColumn.ProviderSpecificDataType, typeof(System.Type));
            DataColumn NonVersionedProviderType = new DataColumn(SchemaTableColumn.NonVersionedProviderType, typeof(System.Int32));
            DataColumn ProviderType = new DataColumn(SchemaTableColumn.ProviderType, typeof(System.Int32));

            DataColumn IsLong = new DataColumn(SchemaTableColumn.IsLong, typeof(System.Boolean));
            DataColumn AllowDBNull = new DataColumn(SchemaTableColumn.AllowDBNull, typeof(System.Boolean));
            DataColumn IsReadOnly = new DataColumn(SchemaTableOptionalColumn.IsReadOnly, typeof(System.Boolean));
            DataColumn IsRowVersion = new DataColumn(SchemaTableOptionalColumn.IsRowVersion, typeof(System.Boolean));

            DataColumn IsUnique = new DataColumn(SchemaTableColumn.IsUnique, typeof(System.Boolean));
            DataColumn IsKey = new DataColumn(SchemaTableColumn.IsKey, typeof(System.Boolean));
            DataColumn IsAutoIncrement = new DataColumn(SchemaTableOptionalColumn.IsAutoIncrement, typeof(System.Boolean));
            DataColumn IsHidden = new DataColumn(SchemaTableOptionalColumn.IsHidden, typeof(System.Boolean));

            DataColumn BaseCatalogName = new DataColumn(SchemaTableOptionalColumn.BaseCatalogName, typeof(System.String));
            DataColumn BaseSchemaName = new DataColumn(SchemaTableColumn.BaseSchemaName, typeof(System.String));
            DataColumn BaseTableName = new DataColumn(SchemaTableColumn.BaseTableName, typeof(System.String));
            DataColumn BaseColumnName = new DataColumn(SchemaTableColumn.BaseColumnName, typeof(System.String));

            // unique to SqlClient
            DataColumn BaseServerName = new DataColumn(SchemaTableOptionalColumn.BaseServerName, typeof(System.String));
            DataColumn IsAliased = new DataColumn(SchemaTableColumn.IsAliased, typeof(System.Boolean));
            DataColumn IsExpression = new DataColumn(SchemaTableColumn.IsExpression, typeof(System.Boolean));
            DataColumn IsIdentity = new DataColumn("IsIdentity", typeof(System.Boolean));
            DataColumn DataTypeName = new DataColumn("DataTypeName", typeof(System.String));
            DataColumn UdtAssemblyQualifiedName = new DataColumn("UdtAssemblyQualifiedName", typeof(System.String));
            // Xml metadata specific
            DataColumn XmlSchemaCollectionDatabase = new DataColumn("XmlSchemaCollectionDatabase", typeof(System.String));
            DataColumn XmlSchemaCollectionOwningSchema = new DataColumn("XmlSchemaCollectionOwningSchema", typeof(System.String));
            DataColumn XmlSchemaCollectionName = new DataColumn("XmlSchemaCollectionName", typeof(System.String));
            // SparseColumnSet
            DataColumn IsColumnSet = new DataColumn("IsColumnSet", typeof(System.Boolean));

            Ordinal.DefaultValue = 0;
            IsLong.DefaultValue = false;

            DataColumnCollection columns = schemaTable.Columns;

            // must maintain order for backward compatibility
            columns.Add(ColumnName);
            columns.Add(Ordinal);
            columns.Add(Size);
            columns.Add(Precision);
            columns.Add(Scale);
            columns.Add(IsUnique);
            columns.Add(IsKey);
            columns.Add(BaseServerName);
            columns.Add(BaseCatalogName);
            columns.Add(BaseColumnName);
            columns.Add(BaseSchemaName);
            columns.Add(BaseTableName);
            columns.Add(DataType);
            columns.Add(AllowDBNull);
            columns.Add(ProviderType);
            columns.Add(IsAliased);
            columns.Add(IsExpression);
            columns.Add(IsIdentity);
            columns.Add(IsAutoIncrement);
            columns.Add(IsRowVersion);
            columns.Add(IsHidden);
            columns.Add(IsLong);
            columns.Add(IsReadOnly);
            columns.Add(ProviderSpecificDataType);
            columns.Add(DataTypeName);
            columns.Add(XmlSchemaCollectionDatabase);
            columns.Add(XmlSchemaCollectionOwningSchema);
            columns.Add(XmlSchemaCollectionName);
            columns.Add(UdtAssemblyQualifiedName);
            columns.Add(NonVersionedProviderType);
            columns.Add(IsColumnSet);

            for (int i = 0; i < md.Length; i++)
            {
                _SqlMetaData col = md[i];
                DataRow schemaRow = schemaTable.NewRow();

                schemaRow[ColumnName] = col.column;
                schemaRow[Ordinal] = col.ordinal;
                //
                // be sure to return character count for string types, byte count otherwise
                // col.length is always byte count so for unicode types, half the length
                //
                // For MAX and XML datatypes, we get 0x7fffffff from the server. Do not divide this.
                if (col.cipherMD != null)
                {
                    Debug.Assert(col.baseTI != null && col.baseTI.metaType != null, "col.baseTI and col.baseTI.metaType should not be null.");
                    schemaRow[Size] = (col.baseTI.metaType.IsSizeInCharacters && (col.baseTI.length != 0x7fffffff)) ? (col.baseTI.length / 2) : col.baseTI.length;
                }
                else
                {
                    schemaRow[Size] = (col.metaType.IsSizeInCharacters && (col.length != 0x7fffffff)) ? (col.length / 2) : col.length;
                }

                schemaRow[DataType] = GetFieldTypeInternal(col);
                schemaRow[ProviderSpecificDataType] = GetProviderSpecificFieldTypeInternal(col);
                schemaRow[NonVersionedProviderType] = (int)(col.cipherMD != null ? col.baseTI.type : col.type); // SqlDbType enum value - does not change with TypeSystem.
                schemaRow[DataTypeName] = GetDataTypeNameInternal(col);

                if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && col.IsNewKatmaiDateTimeType)
                {
                    schemaRow[ProviderType] = SqlDbType.NVarChar;
                    switch (col.type)
                    {
                        case SqlDbType.Date:
                            schemaRow[Size] = TdsEnums.WHIDBEY_DATE_LENGTH;
                            break;
                        case SqlDbType.Time:
                            Debug.Assert(TdsEnums.UNKNOWN_PRECISION_SCALE == col.scale || (0 <= col.scale && col.scale <= 7), "Invalid scale for Time column: " + col.scale);
                            schemaRow[Size] = TdsEnums.WHIDBEY_TIME_LENGTH[TdsEnums.UNKNOWN_PRECISION_SCALE != col.scale ? col.scale : col.metaType.Scale];
                            break;
                        case SqlDbType.DateTime2:
                            Debug.Assert(TdsEnums.UNKNOWN_PRECISION_SCALE == col.scale || (0 <= col.scale && col.scale <= 7), "Invalid scale for DateTime2 column: " + col.scale);
                            schemaRow[Size] = TdsEnums.WHIDBEY_DATETIME2_LENGTH[TdsEnums.UNKNOWN_PRECISION_SCALE != col.scale ? col.scale : col.metaType.Scale];
                            break;
                        case SqlDbType.DateTimeOffset:
                            Debug.Assert(TdsEnums.UNKNOWN_PRECISION_SCALE == col.scale || (0 <= col.scale && col.scale <= 7), "Invalid scale for DateTimeOffset column: " + col.scale);
                            schemaRow[Size] = TdsEnums.WHIDBEY_DATETIMEOFFSET_LENGTH[TdsEnums.UNKNOWN_PRECISION_SCALE != col.scale ? col.scale : col.metaType.Scale];
                            break;
                    }
                }
                else if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && col.IsLargeUdt)
                {
                    if (_typeSystem == SqlConnectionString.TypeSystem.SQLServer2005)
                    {
                        schemaRow[ProviderType] = SqlDbType.VarBinary;
                    }
                    else
                    {
                        // TypeSystem.SQLServer2000
                        schemaRow[ProviderType] = SqlDbType.Image;
                    }
                }
                else if (_typeSystem != SqlConnectionString.TypeSystem.SQLServer2000)
                {
                    // TypeSystem.SQLServer2005 and above

                    // SqlDbType enum value - always the actual type for SQLServer2005.
                    schemaRow[ProviderType] = (int)(col.cipherMD != null ? col.baseTI.type : col.type);

                    if (col.type == SqlDbType.Udt)
                    { // Additional metadata for UDTs.
                        Debug.Assert(Connection.IsYukonOrNewer, "Invalid Column type received from the server");
                        schemaRow[UdtAssemblyQualifiedName] = col.udt?.AssemblyQualifiedName;
                    }
                    else if (col.type == SqlDbType.Xml)
                    { // Additional metadata for Xml.
                        Debug.Assert(Connection.IsYukonOrNewer, "Invalid DataType (Xml) for the column");
                        schemaRow[XmlSchemaCollectionDatabase] = col.xmlSchemaCollection?.Database;
                        schemaRow[XmlSchemaCollectionOwningSchema] = col.xmlSchemaCollection?.OwningSchema;
                        schemaRow[XmlSchemaCollectionName] = col.xmlSchemaCollection?.Name;
                    }
                }
                else
                {
                    // TypeSystem.SQLServer2000

                    // SqlDbType enum value - variable for certain types when SQLServer2000.
                    schemaRow[ProviderType] = GetVersionedMetaType(col.metaType).SqlDbType;
                }

                if (col.cipherMD != null)
                {
                    Debug.Assert(col.baseTI != null, @"col.baseTI should not be null.");
                    if (TdsEnums.UNKNOWN_PRECISION_SCALE != col.baseTI.precision)
                    {
                        schemaRow[Precision] = col.baseTI.precision;
                    }
                    else
                    {
                        schemaRow[Precision] = col.baseTI.metaType.Precision;
                    }
                }
                else if (TdsEnums.UNKNOWN_PRECISION_SCALE != col.precision)
                {
                    schemaRow[Precision] = col.precision;
                }
                else
                {
                    schemaRow[Precision] = col.metaType.Precision;
                }

                if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && col.IsNewKatmaiDateTimeType)
                {
                    schemaRow[Scale] = MetaType.MetaNVarChar.Scale;
                }
                else if (col.cipherMD != null)
                {
                    Debug.Assert(col.baseTI != null, @"col.baseTI should not be null.");
                    if (TdsEnums.UNKNOWN_PRECISION_SCALE != col.baseTI.scale)
                    {
                        schemaRow[Scale] = col.baseTI.scale;
                    }
                    else
                    {
                        schemaRow[Scale] = col.baseTI.metaType.Scale;
                    }
                }
                else if (TdsEnums.UNKNOWN_PRECISION_SCALE != col.scale)
                {
                    schemaRow[Scale] = col.scale;
                }
                else
                {
                    schemaRow[Scale] = col.metaType.Scale;
                }

                schemaRow[AllowDBNull] = col.IsNullable;

                // If no ColInfo token received, do not set value, leave as null.
                if (_browseModeInfoConsumed)
                {
                    schemaRow[IsAliased] = col.IsDifferentName;
                    schemaRow[IsKey] = col.IsKey;
                    schemaRow[IsHidden] = col.IsHidden;
                    schemaRow[IsExpression] = col.IsExpression;
                }

                schemaRow[IsIdentity] = col.IsIdentity;
                schemaRow[IsAutoIncrement] = col.IsIdentity;

                if (col.cipherMD != null)
                {
                    Debug.Assert(col.baseTI != null, @"col.baseTI should not be null.");
                    Debug.Assert(col.baseTI.metaType != null, @"col.baseTI.metaType should not be null.");
                    schemaRow[IsLong] = col.baseTI.metaType.IsLong;
                }
                else
                {
                    schemaRow[IsLong] = col.metaType.IsLong;
                }

                // mark unique for timestamp columns
                if (SqlDbType.Timestamp == col.type)
                {
                    schemaRow[IsUnique] = true;
                    schemaRow[IsRowVersion] = true;
                }
                else
                {
                    schemaRow[IsUnique] = false;
                    schemaRow[IsRowVersion] = false;
                }

                schemaRow[IsReadOnly] = col.IsReadOnly;
                schemaRow[IsColumnSet] = col.IsColumnSet;

                if (!ADP.IsEmpty(col.serverName))
                {
                    schemaRow[BaseServerName] = col.serverName;
                }
                if (!ADP.IsEmpty(col.catalogName))
                {
                    schemaRow[BaseCatalogName] = col.catalogName;
                }
                if (!ADP.IsEmpty(col.schemaName))
                {
                    schemaRow[BaseSchemaName] = col.schemaName;
                }
                if (!ADP.IsEmpty(col.tableName))
                {
                    schemaRow[BaseTableName] = col.tableName;
                }
                if (!ADP.IsEmpty(col.baseColumn))
                {
                    schemaRow[BaseColumnName] = col.baseColumn;
                }
                else if (!ADP.IsEmpty(col.column))
                {
                    schemaRow[BaseColumnName] = col.column;
                }

                schemaTable.Rows.Add(schemaRow);
                schemaRow.AcceptChanges();
            }

            // mark all columns as readonly
            foreach (DataColumn column in columns)
            {
                column.ReadOnly = true; // MDAC 70943
            }

            return schemaTable;
        }

        internal void Cancel(int objectID)
        {
            TdsParserStateObject stateObj = _stateObj;
            if (null != stateObj)
            {
                stateObj.Cancel(objectID);
            }
        }

        // wipe any data off the wire from a partial read
        // and reset all pointers for sequential access
        private bool TryCleanPartialRead()
        {
            AssertReaderState(requireData: true, permitAsync: true);

            // VSTS DEVDIV2 380446: It is possible that read attempt we are cleaning after ended with partially
            // processed header (if it falls between network packets). In this case the first thing to do is to
            // finish reading the header, otherwise code will start treating unread header as TDS payload.
            if (_stateObj._partialHeaderBytesRead > 0)
            {
                if (!_stateObj.TryProcessHeader())
                {
                    return false;
                }
            }

            // following cases for sequential read
            // i. user called read but didn't fetch anything
            // iia. user called read and fetched a subset of the columns
            // iib. user called read and fetched a subset of the column data

            // Wipe out any Streams or TextReaders
            if (-1 != _lastColumnWithDataChunkRead)
            {
                CloseActiveSequentialStreamAndTextReader();
            }

            // i. user called read but didn't fetch anything
            if (0 == _sharedState._nextColumnHeaderToRead)
            {
                if (!_stateObj.Parser.TrySkipRow(_metaData, _stateObj))
                {
                    return false;
                }
            }
            else
            {

                // iia.  if we still have bytes left from a partially read column, skip
                if (!TryResetBlobState())
                {
                    return false;
                }

                // iib.
                // now read the remaining values off the wire for this row
                if (!_stateObj.Parser.TrySkipRow(_metaData, _sharedState._nextColumnHeaderToRead, _stateObj))
                {
                    return false;
                }
            }

#if DEBUG
            if (_stateObj._pendingData)
            {
                byte token;
                if (!_stateObj.TryPeekByte(out token))
                {
                    return false;
                }

                Debug.Assert(TdsParser.IsValidTdsToken(token), string.Format("Invalid token after performing CleanPartialRead: {0,-2:X2}", token));

            }
#endif
            _sharedState._dataReady = false;

            return true;
        }

        private void CleanPartialReadReliable()
        {
            AssertReaderState(requireData: true, permitAsync: false);

            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
#if DEBUG
                TdsParser.ReliabilitySection tdsReliabilitySection = new TdsParser.ReliabilitySection();

                RuntimeHelpers.PrepareConstrainedRegions();
                try
                {
                    tdsReliabilitySection.Start();
#else
                {
#endif //DEBUG
                    bool result = TryCleanPartialRead();
                    Debug.Assert(result, "Should not pend on sync call");
                    Debug.Assert(!_sharedState._dataReady, "_dataReady should be cleared");
                }
#if DEBUG
                finally
                {
                    tdsReliabilitySection.Stop();
                }
#endif //DEBUG
            }
            catch (System.OutOfMemoryException e)
            {
                _isClosed = true;
                if (_connection != null)
                {
                    _connection.Abort(e);
                }
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _isClosed = true;
                if (_connection != null)
                {
                    _connection.Abort(e);
                }
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _isClosed = true;
                if (_connection != null)
                {
                    _connection.Abort(e);
                }
                throw;
            }
        }

        /// <include file='../../../../../../../doc/snippets/Microsoft.Data.SqlClient/SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/Dispose/*' />
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    Close();
                }
                base.Dispose(disposing);
            }
            catch (SqlException ex)
            {
                SqlClientEventSource.Log.TryTraceEvent("SqlDataReader.Dispose | ERR | Error Message: {0}, Stack Trace: {1}", ex.Message, ex.StackTrace);
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/Close/*' />
        override public void Close()
        {
            SqlStatistics statistics = null;
            using (TryEventScope.Create("<sc.SqlDataReader.Close|API> {0}", ObjectID))
            {
                try
                {
                    statistics = SqlStatistics.StartTimer(Statistics);
                    TdsParserStateObject stateObj = _stateObj;

                    // Request that the current task is stopped
                    _cancelAsyncOnCloseTokenSource.Cancel();
                    var currentTask = _currentTask;
                    if ((currentTask != null) && (!currentTask.IsCompleted))
                    {
                        try
                        {
                            // Wait for the task to complete
                            ((IAsyncResult)currentTask).AsyncWaitHandle.WaitOne();

                            // Ensure that we've finished reading any pending data
                            var networkPacketTaskSource = stateObj._networkPacketTaskSource;
                            if (networkPacketTaskSource != null)
                            {
                                ((IAsyncResult)networkPacketTaskSource.Task).AsyncWaitHandle.WaitOne();
                            }
                        }
                        catch (Exception)
                        {
                            // If we receive any exceptions while waiting, something has gone horribly wrong and we need to doom the connection and fast-fail the reader
                            _connection.InnerConnection.DoomThisConnection();
                            _isClosed = true;

                            if (stateObj != null)
                            {
                                lock (stateObj)
                                {
                                    _stateObj = null;
                                    _command = null;
                                    _connection = null;
                                }
                            }

                            throw;
                        }
                    }

                    // Close down any active Streams and TextReaders (this will also wait for them to finish their async tasks)
                    // NOTE: This must be done outside of the lock on the stateObj otherwise it will deadlock with CleanupAfterAsyncInvocation
                    CloseActiveSequentialStreamAndTextReader();

                    if (stateObj != null)
                    {

                        // protect against concurrent close and cancel
                        lock (stateObj)
                        {

                            if (_stateObj != null)
                            {  // reader not closed while we waited for the lock

                                // TryCloseInternal will clear out the snapshot when it is done
                                if (_snapshot != null)
                                {
#if DEBUG
                                    // The stack trace for replays will differ since they weren't captured during close
                                    stateObj._permitReplayStackTraceToDiffer = true;
#endif
                                    PrepareForAsyncContinuation();
                                }

                                SetTimeout(_defaultTimeoutMilliseconds);

                                // Close can be called from async methods in error cases,
                                // in which case we need to switch to syncOverAsync
                                stateObj._syncOverAsync = true;

                                if (!TryCloseInternal(true /*closeReader*/))
                                {
                                    throw SQL.SynchronousCallMayNotPend();
                                }

                                // DO NOT USE stateObj after this point - it has been returned to the TdsParser's session pool and potentially handed out to another thread
                            }
                        }
                    }
                }
                finally
                {
                    SqlStatistics.StopTimer(statistics);
                }
            }
        }

        private bool TryCloseInternal(bool closeReader)
        {
            TdsParser parser = _parser;
            TdsParserStateObject stateObj = _stateObj;
            bool closeConnection = (IsCommandBehavior(CommandBehavior.CloseConnection));
            bool aborting = false;
            bool cleanDataFailed = false;

            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
#if DEBUG
                TdsParser.ReliabilitySection tdsReliabilitySection = new TdsParser.ReliabilitySection();

                RuntimeHelpers.PrepareConstrainedRegions();
                try
                {
                    tdsReliabilitySection.Start();
#else
                {
#endif //DEBUG
                    if ((!_isClosed) && (parser != null) && (stateObj != null) && (stateObj._pendingData))
                    {

                        // It is possible for this to be called during connection close on a
                        // broken connection, so check state first.
                        if (parser.State == TdsParserState.OpenLoggedIn)
                        {
                            // if user called read but didn't fetch any values, skip the row
                            // same applies after NextResult on ALTROW because NextResult starts rowconsumption in that case ...

                            Debug.Assert(SniContext.Snix_Read == stateObj.SniContext, String.Format((IFormatProvider)null, "The SniContext should be Snix_Read but it actually is {0}", stateObj.SniContext));

                            if (_altRowStatus == ALTROWSTATUS.AltRow)
                            {
                                _sharedState._dataReady = true;      // set _sharedState._dataReady to not confuse CleanPartialRead
                            }
                            _stateObj.SetTimeoutStateStopped();
                            if (_sharedState._dataReady)
                            {
                                cleanDataFailed = true;
                                if (TryCleanPartialRead())
                                {
                                    cleanDataFailed = false;
                                }
                                else
                                {
                                    return false;
                                }
                            }
#if DEBUG
                            else
                            {
                                byte token;
                                if (!_stateObj.TryPeekByte(out token))
                                {
                                    return false;
                                }

                                Debug.Assert(TdsParser.IsValidTdsToken(token), $"DataReady is false, but next token is invalid: {token,-2:X2}");
                            }
#endif


                            bool ignored;
                            if (!parser.TryRun(RunBehavior.Clean, _command, this, null, stateObj, out ignored))
                            {
                                return false;
                            }
                        }
                    }

                    RestoreServerSettings(parser, stateObj);
                    return true;
                }
#if DEBUG
                finally
                {
                    tdsReliabilitySection.Stop();
                }
#endif //DEBUG
            }
            catch (System.OutOfMemoryException e)
            {
                _isClosed = true;
                aborting = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _isClosed = true;
                aborting = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _isClosed = true;
                aborting = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
            finally
            {
                if (aborting)
                {
                    _isClosed = true;
                    _command = null; // we are done at this point, don't allow navigation to the connection
                    _connection = null;
                    _statistics = null;
                    _stateObj = null;
                    _parser = null;
                }
                else if (closeReader)
                {
                    bool wasClosed = _isClosed;
                    _isClosed = true;
                    _parser = null;
                    _stateObj = null;
                    _data = null;

                    if (_snapshot != null)
                    {
                        CleanupAfterAsyncInvocationInternal(stateObj);
                    }

                    // SQLBUDT #284712 - Note the order here is extremely important:
                    //
                    // (1) First, we remove the reader from the reference collection
                    //     to prevent it from being forced closed by the parser if
                    //     any future work occurs.
                    //
                    // (2) Next, we ensure that cancellation can no longer happen by
                    //     calling CloseSession.

                    if (Connection != null)
                    {
                        Connection.RemoveWeakReference(this);  // This doesn't catch everything -- the connection may be closed, but it prevents dead readers from clogging the collection
                    }


                    RuntimeHelpers.PrepareConstrainedRegions();
                    try
                    {
#if DEBUG
                        TdsParser.ReliabilitySection tdsReliabilitySection = new TdsParser.ReliabilitySection();

                        RuntimeHelpers.PrepareConstrainedRegions();
                        try
                        {
                            tdsReliabilitySection.Start();
#else
                        {
#endif //DEBUG
                            // IsClosed may be true if CloseReaderFromConnection was called - in which case, the session has already been closed
                            if ((!wasClosed) && (null != stateObj))
                            {
                                if (!cleanDataFailed)
                                {
                                    stateObj.CloseSession();
                                }
                                else
                                {
                                    if (parser != null)
                                    {
                                        parser.State = TdsParserState.Broken; // We failed while draining data, so TDS pointer can be between tokens - cannot recover
                                        parser.PutSession(stateObj);
                                        parser.Connection.BreakConnection();
                                    }
                                }
                            }

                            // DO NOT USE stateObj after this point - it has been returned to the TdsParser's session pool and potentially handed out to another thread
                        }
#if DEBUG
                        finally
                        {
                            tdsReliabilitySection.Stop();
                        }
#endif //DEBUG
                    }
                    catch (System.OutOfMemoryException e)
                    {
                        if (null != _connection)
                        {
                            _connection.Abort(e);
                        }
                        throw;
                    }
                    catch (System.StackOverflowException e)
                    {
                        if (null != _connection)
                        {
                            _connection.Abort(e);
                        }
                        throw;
                    }
                    catch (System.Threading.ThreadAbortException e)
                    {
                        if (null != _connection)
                        {
                            _connection.Abort(e);
                        }
                        throw;
                    }

                    // do not retry here
                    bool result = TrySetMetaData(null, false);
                    Debug.Assert(result, "Should not pend a synchronous request");
                    _fieldNameLookup = null;

                    // if the user calls ExecuteReader(CommandBehavior.CloseConnection)
                    // then we close down the connection when we are done reading results
                    if (closeConnection)
                    {
                        if (Connection != null)
                        {
                            Connection.Close();
                        }
                    }
                    if (_command != null)
                    {
                        // cache recordsaffected to be returnable after DataReader.Close();
                        _recordsAffected = _command.InternalRecordsAffected;
                    }

                    _command = null; // we are done at this point, don't allow navigation to the connection
                    _connection = null;
                    _statistics = null;
                }
            }
        }

        virtual internal void CloseReaderFromConnection()
        {
            var parser = _parser;
            Debug.Assert(parser == null || parser.State != TdsParserState.OpenNotLoggedIn, "Reader on a connection that is not logged in");
            if ((parser != null) && (parser.State == TdsParserState.OpenLoggedIn))
            {
                // Connection is ok - proper cleanup
                // NOTE: This is NOT thread-safe
                Close();
            }
            else
            {
                // Connection is broken - quick cleanup
                // NOTE: This MUST be thread-safe as a broken connection can happen at any time

                var stateObj = _stateObj;
                _isClosed = true;
                // Request that the current task is stopped
                _cancelAsyncOnCloseTokenSource.Cancel();
                if (stateObj != null)
                {
                    var networkPacketTaskSource = stateObj._networkPacketTaskSource;
                    if (networkPacketTaskSource != null)
                    {
                        // If the connection is closed or broken, this will never complete
                        networkPacketTaskSource.TrySetException(ADP.ClosedConnectionError());
                    }
                    if (_snapshot != null)
                    {
                        // CleanWire will do cleanup - so we don't really care about the snapshot
                        CleanupAfterAsyncInvocationInternal(stateObj, resetNetworkPacketTaskSource: false);
                    }
                    // Switch to sync to prepare for cleanwire
                    stateObj._syncOverAsync = true;
                    // Remove owner (this will allow the stateObj to be disposed after the connection is closed)
                    stateObj.RemoveOwner();
                }
            }
        }

        private bool TryConsumeMetaData()
        {
            // warning:  Don't check the MetaData property within this function
            // warning:  as it will be a reentrant call
            while (_parser != null && _stateObj != null && _stateObj._pendingData && !_metaDataConsumed)
            {
                if (_parser.State == TdsParserState.Broken || _parser.State == TdsParserState.Closed)
                {
                    // Happened for DEVDIV2:180509    (SqlDataReader.ConsumeMetaData Hangs In 100% CPU Loop Forever When TdsParser._state == TdsParserState.Broken)
                    // during request for DTC address.
                    // NOTE: We doom connection for TdsParserState.Closed since it indicates that it is in some abnormal and unstable state, probably as a result of
                    // closing from another thread. In general, TdsParserState.Closed does not necessitate dooming the connection.
                    if (_parser.Connection != null)
                        _parser.Connection.DoomThisConnection();
                    throw SQL.ConnectionDoomed();
                }
                bool ignored;
                if (!_parser.TryRun(RunBehavior.ReturnImmediately, _command, this, null, _stateObj, out ignored))
                {
                    return false;
                }
                Debug.Assert(!ignored, "Parser read a row token while trying to read metadata");
            }

            // we hide hidden columns from the user so build an internal map
            // that compacts all hidden columns from the array
            if (null != _metaData)
            {

                if (_snapshot != null && object.ReferenceEquals(_snapshot._metadata, _metaData))
                {
                    _metaData = (_SqlMetaDataSet)_metaData.Clone();
                }

                _metaData.visibleColumns = 0;

                Debug.Assert(null == _metaData.indexMap, "non-null metaData indexmap");
                int[] indexMap = new int[_metaData.Length];
                for (int i = 0; i < indexMap.Length; ++i)
                {
                    indexMap[i] = _metaData.visibleColumns;

                    if (!(_metaData[i].IsHidden))
                    {
                        _metaData.visibleColumns++;
                    }
                }
                _metaData.indexMap = indexMap;
            }

            return true;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetDataTypeName/*' />
        override public string GetDataTypeName(int i)
        {
            SqlStatistics statistics = null;
            try
            {
                statistics = SqlStatistics.StartTimer(Statistics);
                CheckMetaDataIsReady(columnIndex: i);

                return GetDataTypeNameInternal(_metaData[i]);
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
            }
        }

        private string GetDataTypeNameInternal(_SqlMetaData metaData)
        {
            string dataTypeName = null;

            if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && metaData.IsNewKatmaiDateTimeType)
            {
                dataTypeName = MetaType.MetaNVarChar.TypeName;
            }
            else if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && metaData.IsLargeUdt)
            {
                if (_typeSystem == SqlConnectionString.TypeSystem.SQLServer2005)
                {
                    dataTypeName = MetaType.MetaMaxVarBinary.TypeName;
                }
                else
                {
                    // TypeSystem.SQLServer2000
                    dataTypeName = MetaType.MetaImage.TypeName;
                }
            }
            else if (_typeSystem != SqlConnectionString.TypeSystem.SQLServer2000)
            {
                // TypeSystem.SQLServer2005 and above

                if (metaData.type == SqlDbType.Udt)
                {
                    Debug.Assert(Connection.IsYukonOrNewer, "Invalid Column type received from the server");
                    dataTypeName = metaData.udt?.DatabaseName + "." + metaData.udt?.SchemaName + "." + metaData.udt?.TypeName;
                }
                else
                { // For all other types, including Xml - use data in MetaType.
                    if (metaData.cipherMD != null)
                    {
                        Debug.Assert(metaData.baseTI != null && metaData.baseTI.metaType != null, "metaData.baseTI and metaData.baseTI.metaType should not be null.");
                        dataTypeName = metaData.baseTI.metaType.TypeName;
                    }
                    else
                    {
                        dataTypeName = metaData.metaType.TypeName;
                    }
                }
            }
            else
            {
                // TypeSystem.SQLServer2000

                dataTypeName = GetVersionedMetaType(metaData.metaType).TypeName;
            }

            return dataTypeName;
        }

        virtual internal SqlBuffer.StorageType GetVariantInternalStorageType(int i)
        {
            Debug.Assert(null != _data, "Attempting to get variant internal storage type");
            Debug.Assert(i < _data.Length, "Reading beyond data length?");

            return _data[i].VariantInternalStorageType;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetEnumerator/*' />
        override public IEnumerator GetEnumerator()
        {
            return new DbEnumerator(this, IsCommandBehavior(CommandBehavior.CloseConnection));
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetFieldType/*' />
        override public Type GetFieldType(int i)
        {
            SqlStatistics statistics = null;
            try
            {
                statistics = SqlStatistics.StartTimer(Statistics);
                CheckMetaDataIsReady(columnIndex: i);

                return GetFieldTypeInternal(_metaData[i]);
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
            }
        }

        private Type GetFieldTypeInternal(_SqlMetaData metaData)
        {
            Type fieldType = null;

            if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && metaData.IsNewKatmaiDateTimeType)
            {
                // Return katmai types as string
                fieldType = MetaType.MetaNVarChar.ClassType;
            }
            else if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && metaData.IsLargeUdt)
            {
                if (_typeSystem == SqlConnectionString.TypeSystem.SQLServer2005)
                {
                    fieldType = MetaType.MetaMaxVarBinary.ClassType;
                }
                else
                {
                    // TypeSystem.SQLServer2000
                    fieldType = MetaType.MetaImage.ClassType;
                }
            }
            else if (_typeSystem != SqlConnectionString.TypeSystem.SQLServer2000)
            {
                // TypeSystem.SQLServer2005 and above

                if (metaData.type == SqlDbType.Udt)
                {
                    Debug.Assert(Connection.IsYukonOrNewer, "Invalid Column type received from the server");
                    Connection.CheckGetExtendedUDTInfo(metaData, false);
                    fieldType = metaData.udt?.Type;
                }
                else
                { // For all other types, including Xml - use data in MetaType.
                    if (metaData.cipherMD != null)
                    {
                        Debug.Assert(metaData.baseTI != null && metaData.baseTI.metaType != null, "metaData.baseTI and metaData.baseTI.metaType should not be null.");
                        fieldType = metaData.baseTI.metaType.ClassType;
                    }
                    else
                    {
                        fieldType = metaData.metaType.ClassType; // Com+ type.
                    }
                }
            }
            else
            {
                // TypeSystem.SQLServer2000

                fieldType = GetVersionedMetaType(metaData.metaType).ClassType; // Com+ type.
            }

            return fieldType;
        }

        virtual internal int GetLocaleId(int i)
        {
            _SqlMetaData sqlMetaData = MetaData[i];
            int lcid;

            if (sqlMetaData.cipherMD != null)
            {
                // If this column is encrypted, get the collation from baseTI
                //
                if (sqlMetaData.baseTI.collation != null)
                {
                    lcid = sqlMetaData.baseTI.collation.LCID;
                }
                else
                {
                    lcid = 0;
                }
            }
            else
            {
                if (sqlMetaData.collation != null)
                {
                    lcid = sqlMetaData.collation.LCID;
                }
                else
                {
                    lcid = 0;
                }
            }
            return lcid;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetName/*' />
        override public string GetName(int i)
        {
            CheckMetaDataIsReady(columnIndex: i);

            Debug.Assert(null != _metaData[i].column, "MDAC 66681");
            return _metaData[i].column;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetProviderSpecificFieldType/*' />
        override public Type GetProviderSpecificFieldType(int i)
        {
            SqlStatistics statistics = null;
            try
            {
                statistics = SqlStatistics.StartTimer(Statistics);
                CheckMetaDataIsReady(columnIndex: i);

                return GetProviderSpecificFieldTypeInternal(_metaData[i]);
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
            }
        }

        private Type GetProviderSpecificFieldTypeInternal(_SqlMetaData metaData)
        {
            Type providerSpecificFieldType = null;

            if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && metaData.IsNewKatmaiDateTimeType)
            {
                providerSpecificFieldType = MetaType.MetaNVarChar.SqlType;
            }
            else if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && metaData.IsLargeUdt)
            {
                if (_typeSystem == SqlConnectionString.TypeSystem.SQLServer2005)
                {
                    providerSpecificFieldType = MetaType.MetaMaxVarBinary.SqlType;
                }
                else
                {
                    // TypeSystem.SQLServer2000
                    providerSpecificFieldType = MetaType.MetaImage.SqlType;
                }
            }
            else if (_typeSystem != SqlConnectionString.TypeSystem.SQLServer2000)
            {
                // TypeSystem.SQLServer2005 and above

                if (metaData.type == SqlDbType.Udt)
                {
                    Debug.Assert(Connection.IsYukonOrNewer, "Invalid Column type received from the server");
                    Connection.CheckGetExtendedUDTInfo(metaData, false);
                    providerSpecificFieldType = metaData.udt?.Type;
                }
                else
                {
                    // For all other types, including Xml - use data in MetaType.
                    if (metaData.cipherMD != null)
                    {
                        Debug.Assert(metaData.baseTI != null && metaData.baseTI.metaType != null,
                            "metaData.baseTI and metaData.baseTI.metaType should not be null.");
                        providerSpecificFieldType = metaData.baseTI.metaType.SqlType; // SqlType type.
                    }
                    else
                    {
                        providerSpecificFieldType = metaData.metaType.SqlType; // SqlType type.
                    }
                }
            }
            else
            {
                // TypeSystem.SQLServer2000

                providerSpecificFieldType = GetVersionedMetaType(metaData.metaType).SqlType; // SqlType type.
            }

            return providerSpecificFieldType;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetOrdinal/*' />
        // named field access
        override public int GetOrdinal(string name)
        {
            SqlStatistics statistics = null;
            try
            {
                statistics = SqlStatistics.StartTimer(Statistics);
                if (null == _fieldNameLookup)
                {
                    CheckMetaDataIsReady();
                    _fieldNameLookup = new FieldNameLookup(this, _defaultLCID);
                }
                return _fieldNameLookup.GetOrdinal(name); // MDAC 71470
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetProviderSpecificValue/*' />
        override public object GetProviderSpecificValue(int i)
        {
            return GetSqlValue(i);
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetProviderSpecificValues/*' />
        override public int GetProviderSpecificValues(object[] values)
        {
            return GetSqlValues(values);
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSchemaTable/*' />
        override public DataTable GetSchemaTable()
        {
            SqlStatistics statistics = null;
            using (TryEventScope.Create("<sc.SqlDataReader.GetSchemaTable|API> {0}", ObjectID))
            {
                try
                {
                    statistics = SqlStatistics.StartTimer(Statistics);
                    if (null == _metaData || null == _metaData.schemaTable)
                    {
                        if (null != this.MetaData)
                        {
                            _metaData.schemaTable = BuildSchemaTable();
                            Debug.Assert(null != _metaData.schemaTable, "No schema information yet!");
                        }
                    }
                    return _metaData?.schemaTable;
                }
                finally
                {
                    SqlStatistics.StopTimer(statistics);
                }
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetBoolean/*' />
        override public bool GetBoolean(int i)
        {
            ReadColumn(i);
            return _data[i].Boolean;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetXmlReader/*' />
        virtual public XmlReader GetXmlReader(int i)
        {
            // NOTE: sql_variant can not contain a XML data type: http://msdn.microsoft.com/en-us/library/ms173829.aspx
            // If this ever changes, the following code should be changed to be like GetStream\GetTextReader
            CheckDataIsReady(columnIndex: i, methodName: "GetXmlReader");

            MetaType mt = _metaData[i].metaType;

            // XmlReader only allowed on XML types
            if (mt.SqlDbType != SqlDbType.Xml)
            {
                throw SQL.XmlReaderNotSupportOnColumnType(_metaData[i].column);
            }

            if (IsCommandBehavior(CommandBehavior.SequentialAccess))
            {
                // Wrap the sequential stream in an XmlReader
                _currentStream = new SqlSequentialStream(this, i);
                _lastColumnWithDataChunkRead = i;
                return SqlTypeWorkarounds.SqlXmlCreateSqlXmlReader(_currentStream, closeInput: true, async: false);
            }
            else
            {
                // Need to call ReadColumn, since we want to access the internal data structures (i.e. SqlBinary) rather than calling anther Get*() method
                ReadColumn(i);

                if (_data[i].IsNull)
                {
                    // A 'null' stream
                    return SqlTypeWorkarounds.SqlXmlCreateSqlXmlReader(new MemoryStream(new byte[0], writable: false), closeInput: true, async: false);
                }
                else
                {
                    // Grab already read data
                    return _data[i].SqlXml.CreateReader();
                }
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetStream/*' />
        override public Stream GetStream(int i)
        {
            CheckDataIsReady(columnIndex: i, methodName: "GetStream");

            // Streaming is not supported on encrypted columns.
            if (_metaData[i] != null && _metaData[i].cipherMD != null)
            {
                throw SQL.StreamNotSupportOnEncryptedColumn(_metaData[i].column);
            }

            // Stream is only for Binary, Image, VarBinary, Udt and Xml types
            // NOTE: IsBinType also includes Timestamp for some reason...
            MetaType mt = _metaData[i].metaType;
            if (((!mt.IsBinType) || (mt.SqlDbType == SqlDbType.Timestamp)) && (mt.SqlDbType != SqlDbType.Variant))
            {
                throw SQL.StreamNotSupportOnColumnType(_metaData[i].column);
            }

            // For non-variant types with sequential access, we support proper streaming
            if ((mt.SqlDbType != SqlDbType.Variant) && (IsCommandBehavior(CommandBehavior.SequentialAccess)))
            {
                _currentStream = new SqlSequentialStream(this, i);
                _lastColumnWithDataChunkRead = i;
                return _currentStream;
            }
            else
            {
                // Need to call ReadColumn, since we want to access the internal data structures (i.e. SqlBinary) rather than calling anther Get*() method
                ReadColumn(i);

                byte[] data;
                if (_data[i].IsNull)
                {
                    // A 'null' stream
                    data = new byte[0];
                }
                else
                {
                    // Grab already read data
                    data = _data[i].SqlBinary.Value;
                }

                // If non-sequential then we just have a read-only MemoryStream
                return new MemoryStream(data, writable: false);
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetByte/*' />
        override public byte GetByte(int i)
        {
            ReadColumn(i);
            return _data[i].Byte;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetBytes/*' />
        override public long GetBytes(int i, long dataIndex, byte[] buffer, int bufferIndex, int length)
        {
            SqlStatistics statistics = null;
            long cbBytes = 0;

            CheckDataIsReady(columnIndex: i, allowPartiallyReadColumn: true, methodName: "GetBytes");

            // don't allow get bytes on non-long or non-binary columns
            MetaType mt = _metaData[i].metaType;
            if (!(mt.IsLong || mt.IsBinType) || (SqlDbType.Xml == mt.SqlDbType))
            {
                throw SQL.NonBlobColumn(_metaData[i].column);
            }

            try
            {
                statistics = SqlStatistics.StartTimer(Statistics);
                SetTimeout(_defaultTimeoutMilliseconds);
                cbBytes = GetBytesInternal(i, dataIndex, buffer, bufferIndex, length);
                _lastColumnWithDataChunkRead = i;
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
            }
            return cbBytes;
        }

        // Used (indirectly) by SqlCommand.CompleteXmlReader
        virtual internal long GetBytesInternal(int i, long dataIndex, byte[] buffer, int bufferIndex, int length)
        {
            if (_currentTask != null)
            {
                throw ADP.AsyncOperationPending();
            }

            long value;
            Debug.Assert(_stateObj == null || _stateObj._syncOverAsync, "Should not attempt pends in a synchronous call");
            bool result = TryGetBytesInternal(i, dataIndex, buffer, bufferIndex, length, out value);
            if (!result)
            { throw SQL.SynchronousCallMayNotPend(); }
            return value;
        }

        private bool TryGetBytesInternal(int i, long dataIndex, byte[] buffer, int bufferIndex, int length, out long remaining)
        {
            remaining = 0;

            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
#if DEBUG
                TdsParser.ReliabilitySection tdsReliabilitySection = new TdsParser.ReliabilitySection();

                RuntimeHelpers.PrepareConstrainedRegions();
                try
                {
                    tdsReliabilitySection.Start();
#else
                {
#endif //DEBUG
                    int cbytes = 0;
                    AssertReaderState(requireData: true, permitAsync: true, columnIndex: i, enforceSequentialAccess: true);

                    // sequential reading
                    if (IsCommandBehavior(CommandBehavior.SequentialAccess))
                    {
                        Debug.Assert(!HasActiveStreamOrTextReaderOnColumn(i), "Column has an active Stream or TextReader");

                        if (_metaData[i] != null && _metaData[i].cipherMD != null)
                        {
                            throw SQL.SequentialAccessNotSupportedOnEncryptedColumn(_metaData[i].column);
                        }

                        if (_sharedState._nextColumnHeaderToRead <= i)
                        {
                            if (!TryReadColumnHeader(i))
                            {
                                return false;
                            }
                        }

                        // If data is null, ReadColumnHeader sets the data.IsNull bit.
                        if (_data[i] != null && _data[i].IsNull)
                        {
                            throw new SqlNullValueException();
                        }

                        // If there are an unknown (-1) number of bytes left for a PLP, read its size
                        if ((-1 == _sharedState._columnDataBytesRemaining) && (_metaData[i].metaType.IsPlp))
                        {
                            ulong left;
                            if (!_parser.TryPlpBytesLeft(_stateObj, out left))
                            {
                                return false;
                            }
                            _sharedState._columnDataBytesRemaining = (long)left;
                        }

                        if (0 == _sharedState._columnDataBytesRemaining)
                        {
                            return true; // We've read this column to the end
                        }

                        // if no buffer is passed in, return the number total of bytes, or -1
                        if (null == buffer)
                        {
                            if (_metaData[i].metaType.IsPlp)
                            {
                                remaining = (long)_parser.PlpBytesTotalLength(_stateObj);
                                return true;
                            }
                            remaining = _sharedState._columnDataBytesRemaining;
                            return true;
                        }

                        if (dataIndex < 0)
                            throw ADP.NegativeParameter("dataIndex");

                        if (dataIndex < _columnDataBytesRead)
                        {
                            throw ADP.NonSeqByteAccess(dataIndex, _columnDataBytesRead, nameof(GetBytes));
                        }

                        // if the dataIndex is not equal to bytes read, then we have to skip bytes
                        long cb = dataIndex - _columnDataBytesRead;

                        // if dataIndex is outside of the data range, return 0
                        if ((cb > _sharedState._columnDataBytesRemaining) && !_metaData[i].metaType.IsPlp)
                        {
                            return true;
                        }

                        // if bad buffer index, throw
                        if (bufferIndex < 0 || bufferIndex >= buffer.Length)
                            throw ADP.InvalidDestinationBufferIndex(buffer.Length, bufferIndex, "bufferIndex");

                        // if there is not enough room in the buffer for data
                        if (length + bufferIndex > buffer.Length)
                            throw ADP.InvalidBufferSizeOrIndex(length, bufferIndex);

                        if (length < 0)
                            throw ADP.InvalidDataLength(length);

                        // Skip if needed
                        if (cb > 0)
                        {
                            if (_metaData[i].metaType.IsPlp)
                            {
                                ulong skipped;
                                if (!_parser.TrySkipPlpValue((ulong)cb, _stateObj, out skipped))
                                {
                                    return false;
                                }
                                _columnDataBytesRead += (long)skipped;
                            }
                            else
                            {
                                if (!_stateObj.TrySkipLongBytes(cb))
                                {
                                    return false;
                                }
                                _columnDataBytesRead += cb;
                                _sharedState._columnDataBytesRemaining -= cb;
                            }
                        }

                        int bytesRead;
                        bool result = TryGetBytesInternalSequential(i, buffer, bufferIndex, length, out bytesRead);
                        remaining = (int)bytesRead;
                        return result;
                    }

                    // random access now!
                    // note that since we are caching in an array, and arrays aren't 64 bit ready yet,
                    // we need can cast to int if the dataIndex is in range
                    if (dataIndex < 0)
                        throw ADP.NegativeParameter("dataIndex");

                    if (dataIndex > Int32.MaxValue)
                    {
                        throw ADP.InvalidSourceBufferIndex(cbytes, dataIndex, "dataIndex");
                    }

                    int ndataIndex = (int)dataIndex;
                    byte[] data;

                    // WebData 99342 - in the non-sequential case, we need to support
                    //                 the use of GetBytes on string data columns, but
                    //                 GetSqlBinary isn't supposed to.  What we end up
                    //                 doing isn't exactly pretty, but it does work.
                    if (_metaData[i].metaType.IsBinType)
                    {
                        data = GetSqlBinary(i).Value;
                    }
                    else
                    {
                        Debug.Assert(_metaData[i].metaType.IsLong, "non long type?");
                        Debug.Assert(_metaData[i].metaType.IsCharType, "non-char type?");

                        SqlString temp = GetSqlString(i);
                        if (_metaData[i].metaType.IsNCharType)
                        {
                            data = temp.GetUnicodeBytes();
                        }
                        else
                        {
                            data = temp.GetNonUnicodeBytes();
                        }
                    }

                    cbytes = data.Length;

                    // if no buffer is passed in, return the number of characters we have
                    if (null == buffer)
                    {
                        remaining = cbytes;
                        return true;
                    }

                    // if dataIndex is outside of data range, return 0
                    if (ndataIndex < 0 || ndataIndex >= cbytes)
                    {
                        return true;
                    }
                    try
                    {
                        if (ndataIndex < cbytes)
                        {
                            // help the user out in the case where there's less data than requested
                            if ((ndataIndex + length) > cbytes)
                                cbytes = cbytes - ndataIndex;
                            else
                                cbytes = length;
                        }

                        Array.Copy(data, ndataIndex, buffer, bufferIndex, cbytes);
                    }
                    catch (Exception e)
                    {
                        // UNDONE - should not be catching all exceptions!!!
                        if (!ADP.IsCatchableExceptionType(e))
                        {
                            throw;
                        }
                        cbytes = data.Length;

                        if (length < 0)
                            throw ADP.InvalidDataLength(length);

                        // if bad buffer index, throw
                        if (bufferIndex < 0 || bufferIndex >= buffer.Length)
                            throw ADP.InvalidDestinationBufferIndex(buffer.Length, bufferIndex, "bufferIndex");

                        // if there is not enough room in the buffer for data
                        if (cbytes + bufferIndex > buffer.Length)
                            throw ADP.InvalidBufferSizeOrIndex(cbytes, bufferIndex);

                        throw;
                    }

                    remaining = cbytes;
                    return true;
                }
#if DEBUG
                finally
                {
                    tdsReliabilitySection.Stop();
                }
#endif //DEBUG
            }
            catch (System.OutOfMemoryException e)
            {
                _isClosed = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _isClosed = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _isClosed = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
        }

        internal int GetBytesInternalSequential(int i, byte[] buffer, int index, int length, long? timeoutMilliseconds = null)
        {
            if (_currentTask != null)
            {
                throw ADP.AsyncOperationPending();
            }

            int value;
            SqlStatistics statistics = null;
            Debug.Assert(_stateObj._syncOverAsync, "Should not attempt pends in a synchronous call");
            try
            {
                statistics = SqlStatistics.StartTimer(Statistics);
                SetTimeout(timeoutMilliseconds ?? _defaultTimeoutMilliseconds);

                bool result = TryReadColumnHeader(i);
                if (!result)
                { throw SQL.SynchronousCallMayNotPend(); }

                result = TryGetBytesInternalSequential(i, buffer, index, length, out value);
                if (!result)
                { throw SQL.SynchronousCallMayNotPend(); }
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
            }

            return value;
        }

        // This is meant to be called from other internal methods once we are at the column to read
        // NOTE: This method must be retriable WITHOUT replaying a snapshot
        // Every time you call this method increment the index and decrease length by the value of bytesRead
        internal bool TryGetBytesInternalSequential(int i, byte[] buffer, int index, int length, out int bytesRead)
        {
            AssertReaderState(requireData: true, permitAsync: true, columnIndex: i, enforceSequentialAccess: true);
            Debug.Assert(_sharedState._nextColumnHeaderToRead == i + 1 && _sharedState._nextColumnDataToRead == i, "Non sequential access");
            Debug.Assert(buffer != null, "Null buffer");
            Debug.Assert(index >= 0, "Invalid index");
            Debug.Assert(length >= 0, "Invalid length");
            Debug.Assert(index + length <= buffer.Length, "Buffer too small");

            bytesRead = 0;

            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
#if DEBUG
                TdsParser.ReliabilitySection tdsReliabilitySection = new TdsParser.ReliabilitySection();

                RuntimeHelpers.PrepareConstrainedRegions();
                try
                {
                    tdsReliabilitySection.Start();
#endif //DEBUG
                if ((_sharedState._columnDataBytesRemaining == 0) || (length == 0))
                {
                    // No data left or nothing requested, return 0
                    bytesRead = 0;
                    return true;
                }
                else
                {
                    // if plp columns, do partial reads. Don't read the entire value in one shot.
                    if (_metaData[i].metaType.IsPlp)
                    {
                        // Read in data
                        bool result = _stateObj.TryReadPlpBytes(ref buffer, index, length, out bytesRead);
                        _columnDataBytesRead += bytesRead;
                        if (!result)
                        {
                            return false;
                        }

                        // Query for number of bytes left
                        ulong left;
                        if (!_parser.TryPlpBytesLeft(_stateObj, out left))
                        {
                            _sharedState._columnDataBytesRemaining = -1;
                            return false;
                        }
                        _sharedState._columnDataBytesRemaining = (long)left;
                        return true;
                    }
                    else
                    {
                        // Read data (not exceeding the total amount of data available)
                        int bytesToRead = (int)Math.Min((long)length, _sharedState._columnDataBytesRemaining);
                        bool result = _stateObj.TryReadByteArray(buffer, index, bytesToRead, out bytesRead);
                        _columnDataBytesRead += bytesRead;
                        _sharedState._columnDataBytesRemaining -= bytesRead;
                        return result;
                    }
                }
#if DEBUG
                }
                finally
                {
                    tdsReliabilitySection.Stop();
                }
#endif //DEBUG
            }
            catch (System.OutOfMemoryException e)
            {
                _isClosed = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _isClosed = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _isClosed = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetTextReader/*' />
        override public TextReader GetTextReader(int i)
        {
            CheckDataIsReady(columnIndex: i, methodName: "GetTextReader");

            // Xml type is not supported
            MetaType mt = null;

            if (_metaData[i].cipherMD != null)
            {
                Debug.Assert(_metaData[i].baseTI != null, "_metaData[i].baseTI should not be null.");
                mt = _metaData[i].baseTI.metaType;
            }
            else
            {
                mt = _metaData[i].metaType;
            }

            Debug.Assert(mt != null, @"mt should not be null.");

            if (((!mt.IsCharType) && (mt.SqlDbType != SqlDbType.Variant)) || (mt.SqlDbType == SqlDbType.Xml))
            {
                throw SQL.TextReaderNotSupportOnColumnType(_metaData[i].column);
            }

            // For non-variant types with sequential access, we support proper streaming
            if ((mt.SqlDbType != SqlDbType.Variant) && (IsCommandBehavior(CommandBehavior.SequentialAccess)))
            {
                if (_metaData[i].cipherMD != null)
                {
                    throw SQL.SequentialAccessNotSupportedOnEncryptedColumn(_metaData[i].column);
                }

                System.Text.Encoding encoding;
                if (mt.IsNCharType)
                {
                    // NChar types always use unicode
                    encoding = SqlUnicodeEncoding.SqlUnicodeEncodingInstance;
                }
                else
                {
                    encoding = _metaData[i].encoding;
                }

                _currentTextReader = new SqlSequentialTextReader(this, i, encoding);
                _lastColumnWithDataChunkRead = i;
                return _currentTextReader;
            }
            else
            {
                // Need to call ReadColumn, since we want to access the internal data structures (i.e. SqlString) rather than calling anther Get*() method
                ReadColumn(i);

                string data;
                if (_data[i].IsNull)
                {
                    // A 'null' stream
                    data = string.Empty;
                }
                else
                {
                    // Grab already read data
                    data = _data[i].SqlString.Value;
                }

                // We've already read the data, so just wrap it in a StringReader
                return new StringReader(data);
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetChar/*' />
        [EditorBrowsableAttribute(EditorBrowsableState.Never)] // MDAC 69508
        override public char GetChar(int i)
        {
            throw ADP.NotSupported();
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetChars/*' />
        override public long GetChars(int i, long dataIndex, char[] buffer, int bufferIndex, int length)
        {
            SqlStatistics statistics = null;

            CheckMetaDataIsReady(columnIndex: i);

            if (_currentTask != null)
            {
                throw ADP.AsyncOperationPending();
            }

            MetaType mt = null;
            if (_metaData[i].cipherMD != null)
            {
                Debug.Assert(_metaData[i].baseTI != null, @"_metaData[i].baseTI should not be null.");
                mt = _metaData[i].baseTI.metaType;
            }
            else
            {
                mt = _metaData[i].metaType;
            }

            Debug.Assert(mt != null, "mt should not be null.");

            SqlDbType sqlDbType;
            if (_metaData[i].cipherMD != null)
            {
                Debug.Assert(_metaData[i].baseTI != null, @"_metaData[i].baseTI should not be null.");
                sqlDbType = _metaData[i].baseTI.type;
            }
            else
            {
                sqlDbType = _metaData[i].type;
            }

            try
            {
                statistics = SqlStatistics.StartTimer(Statistics);
                SetTimeout(_defaultTimeoutMilliseconds);
                if ((mt.IsPlp) &&
                    (IsCommandBehavior(CommandBehavior.SequentialAccess)))
                {
                    if (length < 0)
                    {
                        throw ADP.InvalidDataLength(length);
                    }

                    if (_metaData[i].cipherMD != null)
                    {
                        throw SQL.SequentialAccessNotSupportedOnEncryptedColumn(_metaData[i].column);
                    }

                    // if bad buffer index, throw
                    if ((bufferIndex < 0) || (buffer != null && bufferIndex >= buffer.Length))
                    {
                        throw ADP.InvalidDestinationBufferIndex(buffer.Length, bufferIndex, "bufferIndex");
                    }

                    // if there is not enough room in the buffer for data
                    if (buffer != null && (length + bufferIndex > buffer.Length))
                    {
                        throw ADP.InvalidBufferSizeOrIndex(length, bufferIndex);
                    }
                    long charsRead = 0;
                    if (sqlDbType == SqlDbType.Xml)
                    {
                        try
                        {
                            CheckDataIsReady(columnIndex: i, allowPartiallyReadColumn: true, methodName: "GetChars");
                        }
                        catch (Exception ex)
                        {
                            // Dev11 Bug #315513: Exception type breaking change from 4.0 RTM when calling GetChars on null xml
                            // We need to wrap all exceptions inside a TargetInvocationException to simulate calling CreateSqlReader via MethodInfo.Invoke
                            if (ADP.IsCatchableExceptionType(ex))
                            {
                                throw new TargetInvocationException(ex);
                            }
                            else
                            {
                                throw;
                            }
                        }
                        charsRead = GetStreamingXmlChars(i, dataIndex, buffer, bufferIndex, length);
                    }
                    else
                    {
                        CheckDataIsReady(columnIndex: i, allowPartiallyReadColumn: true, methodName: "GetChars");
                        charsRead = GetCharsFromPlpData(i, dataIndex, buffer, bufferIndex, length);
                    }
                    _lastColumnWithDataChunkRead = i;
                    return charsRead;
                }

                // Did we start reading this value yet?
                if ((_sharedState._nextColumnDataToRead == (i + 1)) && (_sharedState._nextColumnHeaderToRead == (i + 1)) && (_columnDataChars != null) && (IsCommandBehavior(CommandBehavior.SequentialAccess)) && (dataIndex < _columnDataCharsRead))
                {
                    // Don't allow re-read of same chars in sequential access mode
                    throw ADP.NonSeqByteAccess(dataIndex, _columnDataCharsRead, nameof(GetChars));
                }

                if (_columnDataCharsIndex != i)
                {
                    // if the object doesn't contain a char[] then the user will get an exception
                    string s = GetSqlString(i).Value;

                    _columnDataChars = s.ToCharArray();
                    _columnDataCharsRead = 0;
                    _columnDataCharsIndex = i;
                }

                int cchars = _columnDataChars.Length;

                // note that since we are caching in an array, and arrays aren't 64 bit ready yet,
                // we need can cast to int if the dataIndex is in range
                if (dataIndex > Int32.MaxValue)
                {
                    throw ADP.InvalidSourceBufferIndex(cchars, dataIndex, "dataIndex");
                }
                int ndataIndex = (int)dataIndex;

                // if no buffer is passed in, return the number of characters we have
                if (null == buffer)
                    return cchars;

                // if dataIndex outside of data range, return 0
                if (ndataIndex < 0 || ndataIndex >= cchars)
                    return 0;

                try
                {
                    if (ndataIndex < cchars)
                    {
                        // help the user out in the case where there's less data than requested
                        if ((ndataIndex + length) > cchars)
                            cchars = cchars - ndataIndex;
                        else
                            cchars = length;
                    }

                    Array.Copy(_columnDataChars, ndataIndex, buffer, bufferIndex, cchars);
                    _columnDataCharsRead += cchars;
                }
                catch (Exception e)
                {
                    // UNDONE - should not be catching all exceptions!!!
                    if (!ADP.IsCatchableExceptionType(e))
                    {
                        throw;
                    }
                    cchars = _columnDataChars.Length;

                    if (length < 0)
                        throw ADP.InvalidDataLength(length);

                    // if bad buffer index, throw
                    if (bufferIndex < 0 || bufferIndex >= buffer.Length)
                        throw ADP.InvalidDestinationBufferIndex(buffer.Length, bufferIndex, "bufferIndex");

                    // if there is not enough room in the buffer for data
                    if (cchars + bufferIndex > buffer.Length)
                        throw ADP.InvalidBufferSizeOrIndex(cchars, bufferIndex);

                    throw;
                }

                return cchars;
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
            }
        }

        private long GetCharsFromPlpData(int i, long dataIndex, char[] buffer, int bufferIndex, int length)
        {
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
#if DEBUG
                TdsParser.ReliabilitySection tdsReliabilitySection = new TdsParser.ReliabilitySection();

                RuntimeHelpers.PrepareConstrainedRegions();
                try
                {
                    tdsReliabilitySection.Start();
#else
                {
#endif //DEBUG
                    long cch;

                    AssertReaderState(requireData: true, permitAsync: false, columnIndex: i, enforceSequentialAccess: true);
                    Debug.Assert(!HasActiveStreamOrTextReaderOnColumn(i), "Column has active Stream or TextReader");
                    // don't allow get bytes on non-long or non-binary columns
                    Debug.Assert(_metaData[i].metaType.IsPlp, "GetCharsFromPlpData called on a non-plp column!");
                    // Must be sequential reading
                    Debug.Assert(IsCommandBehavior(CommandBehavior.SequentialAccess), "GetCharsFromPlpData called for non-Sequential access");

                    if (!_metaData[i].metaType.IsCharType)
                    {
                        throw SQL.NonCharColumn(_metaData[i].column);
                    }

                    if (_sharedState._nextColumnHeaderToRead <= i)
                    {
                        ReadColumnHeader(i);
                    }

                    // If data is null, ReadColumnHeader sets the data.IsNull bit.
                    if (_data[i] != null && _data[i].IsNull)
                    {
                        throw new SqlNullValueException();
                    }

                    if (dataIndex < _columnDataCharsRead)
                    {
                        // Don't allow re-read of same chars in sequential access mode
                        throw ADP.NonSeqByteAccess(dataIndex, _columnDataCharsRead, nameof(GetChars));
                    }

                    // If we start reading the new column, either dataIndex is 0 or
                    // _columnDataCharsRead is 0 and dataIndex > _columnDataCharsRead is true below.
                    // In both cases we will clean decoder
                    if (dataIndex == 0)
                        _stateObj._plpdecoder = null;

                    bool isUnicode = _metaData[i].metaType.IsNCharType;

                    // If there are an unknown (-1) number of bytes left for a PLP, read its size
                    if (-1 == _sharedState._columnDataBytesRemaining)
                    {
                        _sharedState._columnDataBytesRemaining = (long)_parser.PlpBytesLeft(_stateObj);
                    }

                    if (0 == _sharedState._columnDataBytesRemaining)
                    {
                        _stateObj._plpdecoder = null;
                        return 0; // We've read this column to the end
                    }

                    // if no buffer is passed in, return the total number of characters or -1
                    // TODO: for DBCS encoding it returns number of bytes, not number of chars
                    if (null == buffer)
                    {
                        cch = (long)_parser.PlpBytesTotalLength(_stateObj);
                        return (isUnicode && (cch > 0)) ? cch >> 1 : cch;
                    }
                    if (dataIndex > _columnDataCharsRead)
                    {
                        // Skip chars

                        // Clean decoder state: we do not reset it, but destroy to ensure
                        // that we do not start decoding the column with decoder from the old one
                        _stateObj._plpdecoder = null;

                        // TODO: for DBCS encoding skip positioning dataIndex is not in characters but is interpreted as
                        // number of chars already read + number of bytes to skip
                        cch = dataIndex - _columnDataCharsRead;
                        cch = isUnicode ? (cch << 1) : cch;
                        cch = (long)_parser.SkipPlpValue((ulong)(cch), _stateObj);
                        _columnDataBytesRead += cch;
                        _columnDataCharsRead += (isUnicode && (cch > 0)) ? cch >> 1 : cch;
                    }
                    cch = length;

                    if (isUnicode)
                    {
                        cch = (long)_parser.ReadPlpUnicodeChars(ref buffer, bufferIndex, length, _stateObj);
                        _columnDataBytesRead += (cch << 1);
                    }
                    else
                    {
                        cch = (long)_parser.ReadPlpAnsiChars(ref buffer, bufferIndex, length, _metaData[i], _stateObj);
                        _columnDataBytesRead += cch << 1;
                    }
                    _columnDataCharsRead += cch;
                    _sharedState._columnDataBytesRemaining = (long)_parser.PlpBytesLeft(_stateObj);
                    return cch;
                }
#if DEBUG
                finally
                {
                    tdsReliabilitySection.Stop();
                }
#endif //DEBUG
            }
            catch (System.OutOfMemoryException e)
            {
                _isClosed = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _isClosed = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _isClosed = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
        }

        internal long GetStreamingXmlChars(int i, long dataIndex, char[] buffer, int bufferIndex, int length)
        {
            SqlStreamingXml localSXml = null;
            if ((_streamingXml != null) && (_streamingXml.ColumnOrdinal != i))
            {
                _streamingXml.Close();
                _streamingXml = null;
            }
            if (_streamingXml == null)
            {
                localSXml = new SqlStreamingXml(i, this);
            }
            else
            {
                localSXml = _streamingXml;
            }
            long cnt = localSXml.GetChars(dataIndex, buffer, bufferIndex, length);
            if (_streamingXml == null)
            {
                // Data is read through GetBytesInternal which may dispose _streamingXml if it has to advance the column ordinal.
                // Therefore save the new SqlStreamingXml class after the read succeeds.
                _streamingXml = localSXml;
            }
            return cnt;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/System.Data.IDataRecord.GetData/*' />
        [EditorBrowsableAttribute(EditorBrowsableState.Never)] // MDAC 69508
        IDataReader IDataRecord.GetData(int i)
        {
            throw ADP.NotSupported();
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetDateTime/*' />
        override public DateTime GetDateTime(int i)
        {
            ReadColumn(i);

            DateTime dt = _data[i].DateTime;
            // This accessor can be called for regular DateTime column. In this case we should not throw
            if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && _metaData[i].IsNewKatmaiDateTimeType)
            {
                // TypeSystem.SQLServer2005 or less

                // If the above succeeds, then we received a valid DateTime instance, now we need to force
                // an InvalidCastException since DateTime is not exposed with the version knob in this setting.
                // To do so, we simply force the exception by casting the string representation of the value
                // To DateTime.
                object temp = (object)_data[i].String;
                dt = (DateTime)temp;
            }

            return dt;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetDecimal/*' />
        override public Decimal GetDecimal(int i)
        {
            ReadColumn(i);
            return _data[i].Decimal;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetDouble/*' />
        override public double GetDouble(int i)
        {
            ReadColumn(i);
            return _data[i].Double;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetFloat/*' />
        override public float GetFloat(int i)
        {
            ReadColumn(i);
            return _data[i].Single;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetGuid/*' />
        override public Guid GetGuid(int i)
        {
            ReadColumn(i);
            return _data[i].SqlGuid.Value;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetInt16/*' />
        override public Int16 GetInt16(int i)
        {
            ReadColumn(i);
            return _data[i].Int16;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetInt32/*' />
        override public Int32 GetInt32(int i)
        {
            ReadColumn(i);
            return _data[i].Int32;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetInt64/*' />
        override public Int64 GetInt64(int i)
        {
            ReadColumn(i);
            return _data[i].Int64;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlBoolean/*' />
        virtual public SqlBoolean GetSqlBoolean(int i)
        {
            ReadColumn(i);
            return _data[i].SqlBoolean;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlBinary/*' />
        virtual public SqlBinary GetSqlBinary(int i)
        {
            ReadColumn(i, setTimeout: true, allowPartiallyReadColumn: true);
            return _data[i].SqlBinary;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlByte/*' />
        virtual public SqlByte GetSqlByte(int i)
        {
            ReadColumn(i);
            return _data[i].SqlByte;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlBytes/*' />
        virtual public SqlBytes GetSqlBytes(int i)
        {
            ReadColumn(i);
            SqlBinary data = _data[i].SqlBinary;
            return new SqlBytes(data);
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlChars/*' />
        virtual public SqlChars GetSqlChars(int i)
        {
            ReadColumn(i);
            SqlString data;
            // Convert Katmai types to string
            if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && _metaData[i].IsNewKatmaiDateTimeType)
            {
                data = _data[i].KatmaiDateTimeSqlString;
            }
            else
            {
                data = _data[i].SqlString;
            }
            return new SqlChars(data);
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlDateTime/*' />
        virtual public SqlDateTime GetSqlDateTime(int i)
        {
            ReadColumn(i);
            return _data[i].SqlDateTime;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlDecimal/*' />
        virtual public SqlDecimal GetSqlDecimal(int i)
        {
            ReadColumn(i);
            return _data[i].SqlDecimal;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlGuid/*' />
        virtual public SqlGuid GetSqlGuid(int i)
        {
            ReadColumn(i);
            return _data[i].SqlGuid;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlDouble/*' />
        virtual public SqlDouble GetSqlDouble(int i)
        {
            ReadColumn(i);
            return _data[i].SqlDouble;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlInt16/*' />
        virtual public SqlInt16 GetSqlInt16(int i)
        {
            ReadColumn(i);
            return _data[i].SqlInt16;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlInt32/*' />
        virtual public SqlInt32 GetSqlInt32(int i)
        {
            ReadColumn(i);
            return _data[i].SqlInt32;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlInt64/*' />
        virtual public SqlInt64 GetSqlInt64(int i)
        {
            ReadColumn(i);
            return _data[i].SqlInt64;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlMoney/*' />
        virtual public SqlMoney GetSqlMoney(int i)
        {
            ReadColumn(i);
            return _data[i].SqlMoney;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlSingle/*' />
        virtual public SqlSingle GetSqlSingle(int i)
        {
            ReadColumn(i);
            return _data[i].SqlSingle;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlString/*' />
        // UNDONE: need non-unicode SqlString support
        virtual public SqlString GetSqlString(int i)
        {
            ReadColumn(i);

            if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && _metaData[i].IsNewKatmaiDateTimeType)
            {
                return _data[i].KatmaiDateTimeSqlString;
            }

            return _data[i].SqlString;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlXml/*' />
        virtual public SqlXml GetSqlXml(int i)
        {
            ReadColumn(i);
            SqlXml sx = null;

            if (_typeSystem != SqlConnectionString.TypeSystem.SQLServer2000)
            {
                // TypeSystem.SQLServer2005

                sx = _data[i].IsNull ? SqlXml.Null : _data[i].SqlCachedBuffer.ToSqlXml();
            }
            else
            {
                // TypeSystem.SQLServer2000

                // First, attempt to obtain SqlXml value.  If not SqlXml, we will throw the appropriate
                // cast exception.
                sx = _data[i].IsNull ? SqlXml.Null : _data[i].SqlCachedBuffer.ToSqlXml();

                // If the above succeeds, then we received a valid SqlXml instance, now we need to force
                // an InvalidCastException since SqlXml is not exposed with the version knob in this setting.
                // To do so, we simply force the exception by casting the string representation of the value
                // To SqlXml.
                object temp = (object)_data[i].String;
                sx = (SqlXml)temp;
            }

            return sx;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlValue/*' />
        virtual public object GetSqlValue(int i)
        {
            SqlStatistics statistics = null;
            try
            {
                statistics = SqlStatistics.StartTimer(Statistics);

                SetTimeout(_defaultTimeoutMilliseconds);
                return GetSqlValueInternal(i);
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
            }
        }

        private object GetSqlValueInternal(int i)
        {
            if (_currentTask != null)
            {
                throw ADP.AsyncOperationPending();
            }

            Debug.Assert(_stateObj == null || _stateObj._syncOverAsync, "Should not attempt pends in a synchronous call");
            bool result = TryReadColumn(i, setTimeout: false);
            if (!result)
            { throw SQL.SynchronousCallMayNotPend(); }

            return GetSqlValueFromSqlBufferInternal(_data[i], _metaData[i]);
        }

        // NOTE: This method is called by the fast-paths in Async methods and, therefore, should be resilient to the DataReader being closed
        //       Always make sure to take reference copies of anything set to null in TryCloseInternal()
        private object GetSqlValueFromSqlBufferInternal(SqlBuffer data, _SqlMetaData metaData)
        {
            // Dev11 Bug #336820, Dev10 Bug #479607 (SqlClient: IsDBNull always returns false for timestamp datatype)
            // Due to a bug in TdsParser.GetNullSqlValue, Timestamps' IsNull is not correctly set - so we need to bypass the following check
            Debug.Assert(!data.IsEmpty || data.IsNull || metaData.type == SqlDbType.Timestamp, "Data has been read, but the buffer is empty");

            // Convert Katmai types to string
            if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && metaData.IsNewKatmaiDateTimeType)
            {
                return data.KatmaiDateTimeSqlString;
            }
            else if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && metaData.IsLargeUdt)
            {
                return data.SqlValue;
            }
            else if (_typeSystem != SqlConnectionString.TypeSystem.SQLServer2000)
            {
                // TypeSystem.SQLServer2005

                if (metaData.type == SqlDbType.Udt)
                {
                    var connection = _connection;
                    if (connection != null)
                    {
                        connection.CheckGetExtendedUDTInfo(metaData, true);
                        return connection.GetUdtValue(data.Value, metaData, false);
                    }
                    else
                    {
                        throw ADP.DataReaderClosed("GetSqlValueFromSqlBufferInternal");
                    }
                }
                else
                {
                    return data.SqlValue;
                }
            }
            else
            {
                // TypeSystem.SQLServer2000

                if (metaData.type == SqlDbType.Xml)
                {
                    return data.SqlString;
                }
                else
                {
                    return data.SqlValue;
                }
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetSqlValues/*' />
        virtual public int GetSqlValues(object[] values)
        {
            SqlStatistics statistics = null;
            try
            {
                statistics = SqlStatistics.StartTimer(Statistics);
                CheckDataIsReady();
                if (null == values)
                {
                    throw ADP.ArgumentNull("values");
                }

                SetTimeout(_defaultTimeoutMilliseconds);

                int copyLen = (values.Length < _metaData.visibleColumns) ? values.Length : _metaData.visibleColumns;

                for (int i = 0; i < copyLen; i++)
                {
                    values[_metaData.indexMap[i]] = GetSqlValueInternal(i);
                }
                return copyLen;
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetString/*' />
        override public string GetString(int i)
        {
            ReadColumn(i);

            // Convert katmai value to string if type system knob is 2005 or earlier
            if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && _metaData[i].IsNewKatmaiDateTimeType)
            {
                return _data[i].KatmaiDateTimeString;
            }

            return _data[i].String;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetFieldValue/*' />
        override public T GetFieldValue<T>(int i)
        {
            SqlStatistics statistics = null;
            try
            {
                statistics = SqlStatistics.StartTimer(Statistics);

                SetTimeout(_defaultTimeoutMilliseconds);
                return GetFieldValueInternal<T>(i, isAsync: false);
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetValue/*' />
        override public object GetValue(int i)
        {
            SqlStatistics statistics = null;
            try
            {
                statistics = SqlStatistics.StartTimer(Statistics);

                SetTimeout(_defaultTimeoutMilliseconds);
                return GetValueInternal(i);
            }
            finally
            {
                SqlStatistics.StopTimer(statistics);
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetTimeSpan/*' />
        virtual public TimeSpan GetTimeSpan(int i)
        {
            ReadColumn(i);

            TimeSpan t = _data[i].Time;

            if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005)
            {
                // TypeSystem.SQLServer2005 or less

                // If the above succeeds, then we received a valid TimeSpan instance, now we need to force
                // an InvalidCastException since TimeSpan is not exposed with the version knob in this setting.
                // To do so, we simply force the exception by casting the string representation of the value
                // To TimeSpan.
                object temp = (object)_data[i].String;
                t = (TimeSpan)temp;
            }

            return t;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetDateTimeOffset/*' />
        virtual public DateTimeOffset GetDateTimeOffset(int i)
        {
            ReadColumn(i);

            DateTimeOffset dto = _data[i].DateTimeOffset;

            if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005)
            {
                // TypeSystem.SQLServer2005 or less

                // If the above succeeds, then we received a valid DateTimeOffset instance, now we need to force
                // an InvalidCastException since DateTime is not exposed with the version knob in this setting.
                // To do so, we simply force the exception by casting the string representation of the value
                // To DateTimeOffset.
                object temp = (object)_data[i].String;
                dto = (DateTimeOffset)temp;
            }

            return dto;
        }

        private object GetValueInternal(int i)
        {
            if (_currentTask != null)
            {
                throw ADP.AsyncOperationPending();
            }

            Debug.Assert(_stateObj == null || _stateObj._syncOverAsync, "Should not attempt pends in a synchronous call");
            bool result = TryReadColumn(i, setTimeout: false);
            if (!result)
            { throw SQL.SynchronousCallMayNotPend(); }

            return GetValueFromSqlBufferInternal(_data[i], _metaData[i]);
        }

        // NOTE: This method is called by the fast-paths in Async methods and, therefore, should be resilient to the DataReader being closed
        //       Always make sure to take reference copies of anything set to null in TryCloseInternal()
        private object GetValueFromSqlBufferInternal(SqlBuffer data, _SqlMetaData metaData)
        {
            // Dev11 Bug #336820, Dev10 Bug #479607 (SqlClient: IsDBNull always returns false for timestamp datatype)
            // Due to a bug in TdsParser.GetNullSqlValue, Timestamps' IsNull is not correctly set - so we need to bypass the following check
            Debug.Assert(!data.IsEmpty || data.IsNull || metaData.type == SqlDbType.Timestamp, "Data has been read, but the buffer is empty");

            if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && metaData.IsNewKatmaiDateTimeType)
            {
                if (data.IsNull)
                {
                    return DBNull.Value;
                }
                else
                {
                    return data.KatmaiDateTimeString;
                }
            }
            else if (_typeSystem <= SqlConnectionString.TypeSystem.SQLServer2005 && metaData.IsLargeUdt)
            {
                return data.Value;
            }
            else if (_typeSystem != SqlConnectionString.TypeSystem.SQLServer2000)
            {
                // TypeSystem.SQLServer2005

                if (metaData.type != SqlDbType.Udt)
                {
                    return data.Value;
                }
                else
                {
                    var connection = _connection;
                    if (connection != null)
                    {
                        connection.CheckGetExtendedUDTInfo(metaData, true);
                        return connection.GetUdtValue(data.Value, metaData, true);
                    }
                    else
                    {
                        throw ADP.DataReaderClosed("GetValueFromSqlBufferInternal");
                    }
                }
            }
            else
            {
                // TypeSystem.SQLServer2000
                return data.Value;
            }
        }

        private T GetFieldValueInternal<T>(int i, bool isAsync)
        {
            if (_currentTask != null)
            {
                throw ADP.AsyncOperationPending();
            }

            Debug.Assert(_stateObj == null || _stateObj._syncOverAsync, "Should not attempt pends in a synchronous call");
            bool forStreaming = typeof(T) == typeof(XmlReader) || typeof(T) == typeof(TextReader) || typeof(T) == typeof(Stream);
            bool result = TryReadColumn(i, setTimeout: false, forStreaming: forStreaming);
            if (!result)
            {
                throw SQL.SynchronousCallMayNotPend();
            }

            return GetFieldValueFromSqlBufferInternal<T>(_data[i], _metaData[i], isAsync: isAsync);
        }

        private T GetFieldValueFromSqlBufferInternal<T>(SqlBuffer data, _SqlMetaData metaData, bool isAsync)
        {
            if (typeof(T) == typeof(XmlReader))
            {
                // XmlReader only allowed on XML types
                if (metaData.metaType.SqlDbType != SqlDbType.Xml)
                {
                    throw SQL.XmlReaderNotSupportOnColumnType(metaData.column);
                }

                if (IsCommandBehavior(CommandBehavior.SequentialAccess))
                {
                    // Wrap the sequential stream in an XmlReader
                    _currentStream = new SqlSequentialStream(this, metaData.ordinal);
                    _lastColumnWithDataChunkRead = metaData.ordinal;
                    return (T)(object)SqlTypeWorkarounds.SqlXmlCreateSqlXmlReader(_currentStream, closeInput: true, async: isAsync);
                }
                else
                {
                    if (data.IsNull)
                    {
                        // A 'null' stream
                        return (T)(object)SqlTypeWorkarounds.SqlXmlCreateSqlXmlReader(new MemoryStream(Array.Empty<byte>(), writable: false), closeInput: true, async: isAsync);
                    }
                    else
                    {
                        // Grab already read data
                        return (T)(object)data.SqlXml.CreateReader();
                    }
                }
            }
            else if (typeof(T) == typeof(TextReader))
            {
                // Xml type is not supported
                MetaType metaType = metaData.metaType;
                if (metaData.cipherMD != null)
                {
                    Debug.Assert(metaData.baseTI != null, "_metaData[i].baseTI should not be null.");
                    metaType = metaData.baseTI.metaType;
                }

                if (
                    (!metaType.IsCharType && metaType.SqlDbType != SqlDbType.Variant) ||
                    (metaType.SqlDbType == SqlDbType.Xml)
                )
                {
                    throw SQL.TextReaderNotSupportOnColumnType(metaData.column);
                }

                // For non-variant types with sequential access, we support proper streaming
                if ((metaType.SqlDbType != SqlDbType.Variant) && IsCommandBehavior(CommandBehavior.SequentialAccess))
                {
                    if (metaData.cipherMD != null)
                    {
                        throw SQL.SequentialAccessNotSupportedOnEncryptedColumn(metaData.column);
                    }

                    System.Text.Encoding encoding = SqlUnicodeEncoding.SqlUnicodeEncodingInstance;
                    if (!metaType.IsNCharType)
                    {
                        encoding = metaData.encoding;
                    }

                    _currentTextReader = new SqlSequentialTextReader(this, metaData.ordinal, encoding);
                    _lastColumnWithDataChunkRead = metaData.ordinal;
                    return (T)(object)_currentTextReader;
                }
                else
                {
                    string value = data.IsNull ? string.Empty : data.SqlString.Value;
                    return (T)(object)new StringReader(value);
                }

            }
            else if (typeof(T) == typeof(Stream))
            {
                if (metaData != null && metaData.cipherMD != null)
                {
                    throw SQL.StreamNotSupportOnEncryptedColumn(metaData.column);
                }

                // Stream is only for Binary, Image, VarBinary, Udt, Xml and Timestamp(RowVersion) types
                MetaType metaType = metaData.metaType;
                if (
                    (!metaType.IsBinType || metaType.SqlDbType == SqlDbType.Timestamp) &&
                    metaType.SqlDbType != SqlDbType.Variant
                )
                {
                    throw SQL.StreamNotSupportOnColumnType(metaData.column);
                }

                if ((metaType.SqlDbType != SqlDbType.Variant) && (IsCommandBehavior(CommandBehavior.SequentialAccess)))
                {
                    _currentStream = new SqlSequentialStream(this, metaData.ordinal);
                    _lastColumnWithDataChunkRead = metaData.ordinal;
                    return (T)(object)_currentStream;
                }
                else
                {
                    byte[] value = data.IsNull ? Array.Empty<byte>() : data.SqlBinary.Value;
                    return (T)(object)new MemoryStream(value, writable: false);
                }
            }
            else if (typeof(INullable).IsAssignableFrom(typeof(T)))
            {
                // If its a SQL Type or Nullable UDT
                object rawValue = GetSqlValueFromSqlBufferInternal(data, metaData);

                if (typeof(T) == typeof(SqlString))
                {
                    // Special case: User wants SqlString, but we have a SqlXml
                    // SqlXml can not be typecast into a SqlString, but we need to support SqlString on XML Types - so do a manual conversion
                    SqlXml xmlValue = rawValue as SqlXml;
                    if (xmlValue != null)
                    {
                        if (xmlValue.IsNull)
                        {
                            rawValue = SqlString.Null;
                        }
                        else
                        {
                            rawValue = new SqlString(xmlValue.Value);
                        }
                    }
                }

                return (T)rawValue;
            }
            else
            {
                // Otherwise Its a CLR or non-Nullable UDT
                try
                {
                    return (T)GetValueFromSqlBufferInternal(data, metaData);
                }
                catch (InvalidCastException) when (data.IsNull)
                {
                    // If the value was actually null, then we should throw a SqlNullValue instead
                    throw SQL.SqlNullValue();
                }
                
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetValues/*' />
        override public int GetValues(object[] values)
        {
            SqlStatistics statistics = null;
            bool sequentialAccess = IsCommandBehavior(CommandBehavior.SequentialAccess);

            try
            {
                statistics = SqlStatistics.StartTimer(Statistics);

                if (null == values)
                {
                    throw ADP.ArgumentNull("values");
                }

                CheckMetaDataIsReady();

                int copyLen = (values.Length < _metaData.visibleColumns) ? values.Length : _metaData.visibleColumns;
                int maximumColumn = copyLen - 1;

                SetTimeout(_defaultTimeoutMilliseconds);

                // Temporarily disable sequential access
                _commandBehavior &= ~CommandBehavior.SequentialAccess;

                // Read in all of the columns in one TryReadColumn call
                bool result = TryReadColumn(maximumColumn, setTimeout: false);
                if (!result)
                { throw SQL.SynchronousCallMayNotPend(); }

                for (int i = 0; i < copyLen; i++)
                {
                    // Get the usable, TypeSystem-compatible value from the iternal buffer
                    values[_metaData.indexMap[i]] = GetValueFromSqlBufferInternal(_data[i], _metaData[i]);

                    // If this is sequential access, then we need to wipe the internal buffer
                    if ((sequentialAccess) && (i < maximumColumn))
                    {
                        _data[i].Clear();
                    }
                }

                return copyLen;
            }
            finally
            {
                // Restore sequential access
                if (sequentialAccess)
                {
                    _commandBehavior |= CommandBehavior.SequentialAccess;
                }

                SqlStatistics.StopTimer(statistics);
            }
        }

        private MetaType GetVersionedMetaType(MetaType actualMetaType)
        {
            Debug.Assert(_typeSystem == SqlConnectionString.TypeSystem.SQLServer2000, "Should not be in this function under anything else but SQLServer2000");

            MetaType metaType = null;

            if (actualMetaType == MetaType.MetaUdt)
            {
                metaType = MetaType.MetaVarBinary;
            }
            else if (actualMetaType == MetaType.MetaXml)
            {
                metaType = MetaType.MetaNText;
            }
            else if (actualMetaType == MetaType.MetaMaxVarBinary)
            {
                metaType = MetaType.MetaImage;
            }
            else if (actualMetaType == MetaType.MetaMaxVarChar)
            {
                metaType = MetaType.MetaText;
            }
            else if (actualMetaType == MetaType.MetaMaxNVarChar)
            {
                metaType = MetaType.MetaNText;
            }
            else
            {
                metaType = actualMetaType;
            }

            return metaType;
        }

        private bool TryHasMoreResults(out bool moreResults)
        {
            if (null != _parser)
            {
                bool moreRows;
                if (!TryHasMoreRows(out moreRows))
                {
                    moreResults = false;
                    return false;
                }
                if (moreRows)
                {
                    // When does this happen?  This is only called from NextResult(), which loops until Read() false.
                    moreResults = false;
                    return true;
                }

                Debug.Assert(null != _command, "unexpected null command from the data reader!");

                while (_stateObj._pendingData)
                {
                    byte token;
                    if (!_stateObj.TryPeekByte(out token))
                    {
                        moreResults = false;
                        return false;
                    }

                    switch (token)
                    {
                        case TdsEnums.SQLALTROW:
                            if (_altRowStatus == ALTROWSTATUS.Null)
                            {
                                // cache the regular metadata
                                _altMetaDataSetCollection.metaDataSet = _metaData;
                                _metaData = null;
                            }
                            else
                            {
                                Debug.Assert(_altRowStatus == ALTROWSTATUS.Done, "invalid AltRowStatus");
                            }
                            _altRowStatus = ALTROWSTATUS.AltRow;
                            _hasRows = true;
                            moreResults = true;
                            return true;
                        case TdsEnums.SQLROW:
                        case TdsEnums.SQLNBCROW:
                            // always happens if there is a row following an altrow
                            moreResults = true;
                            return true;

                        // VSTFDEVDIV 926281: DONEINPROC case is missing here; we have decided to reject this bug as it would result in breaking change
                        // from Orcas RTM/SP1 and Dev10 RTM. See the bug for more details.
                        // case TdsEnums.DONEINPROC:
                        case TdsEnums.SQLDONE:
                            Debug.Assert(_altRowStatus == ALTROWSTATUS.Done || _altRowStatus == ALTROWSTATUS.Null, "invalid AltRowStatus");
                            _altRowStatus = ALTROWSTATUS.Null;
                            _metaData = null;
                            _altMetaDataSetCollection = null;
                            moreResults = true;
                            return true;
                        case TdsEnums.SQLCOLMETADATA:
                            moreResults = true;
                            return true;
                    }

                    // Dev11 Bug 316483: Stuck at SqlDataReader::TryHasMoreResults using MARS
                    // http://vstfdevdiv:8080/web/wi.aspx?pcguid=22f9acc9-569a-41ff-b6ac-fac1b6370209&id=316483
                    // TryRun() will immediately return if the TdsParser is closed\broken, causing us to enter an infinite loop
                    // Instead, we will throw a closed connection exception
                    if (_parser.State == TdsParserState.Broken || _parser.State == TdsParserState.Closed)
                    {
                        throw ADP.ClosedConnectionError();
                    }

                    bool ignored;
                    if (!_parser.TryRun(RunBehavior.ReturnImmediately, _command, this, null, _stateObj, out ignored))
                    {
                        moreResults = false;
                        return false;
                    }
                }
            }
            moreResults = false;
            return true;
        }

        private bool TryHasMoreRows(out bool moreRows)
        {
            if (null != _parser)
            {
                if (_sharedState._dataReady)
                {
                    moreRows = true;
                    return true;
                }

                // NextResult: previous call to NextResult started to process the altrowpackage, can't peek anymore
                // Read: Read prepared for final processing of altrow package, No more Rows until NextResult ...
                // Done: Done processing the altrow, no more rows until NextResult ...
                switch (_altRowStatus)
                {
                    case ALTROWSTATUS.AltRow:
                        moreRows = true;
                        return true;
                    case ALTROWSTATUS.Done:
                        moreRows = false;
                        return true;
                }
                if (_stateObj._pendingData)
                {
                    // Consume error's, info's, done's on HasMoreRows, so user obtains error on Read.
                    // Previous bug where Read() would return false with error on the wire in the case
                    // of metadata and error immediately following.  See MDAC 78285 and 75225.

                    // BUGBUG - currently in V1 the if (_parser.PendingData) does not
                    // exist, so under certain conditions HasMoreRows can timeout.  However,
                    // this should only occur when executing as SqlBatch and returning a reader.
                    // Updated - SQL Bug: 20001249
                    // Modifed while loop and added parsedDoneToken, to revert a regression from everettfs.
                    // "Error Exceptions" are now only thown in read when a error occures in the exception, otherwise the exception will be thrown on the call to get the next result set.
                    // resultset.

                    // process any done, doneproc and doneinproc token streams and
                    // any order, error or info token preceeding the first done, doneproc or doneinproc token stream
                    byte b;
                    if (!_stateObj.TryPeekByte(out b))
                    {
                        moreRows = false;
                        return false;
                    }
                    bool ParsedDoneToken = false;

                    while (b == TdsEnums.SQLDONE ||
                            b == TdsEnums.SQLDONEPROC ||
                            b == TdsEnums.SQLDONEINPROC ||
                            !ParsedDoneToken && (
                                b == TdsEnums.SQLSESSIONSTATE ||
                                b == TdsEnums.SQLENVCHANGE ||
                                b == TdsEnums.SQLORDER ||
                                b == TdsEnums.SQLERROR ||
                                b == TdsEnums.SQLINFO))
                    {

                        if (b == TdsEnums.SQLDONE ||
                            b == TdsEnums.SQLDONEPROC ||
                            b == TdsEnums.SQLDONEINPROC)
                        {
                            ParsedDoneToken = true;
                        }

                        // Dev11 Bug 316483: Stuck at SqlDataReader::TryHasMoreResults when using MARS
                        // http://vstfdevdiv:8080/web/wi.aspx?pcguid=22f9acc9-569a-41ff-b6ac-fac1b6370209&id=316483
                        // TryRun() will immediately return if the TdsParser is closed\broken, causing us to enter an infinite loop
                        // Instead, we will throw a closed connection exception
                        if (_parser.State == TdsParserState.Broken || _parser.State == TdsParserState.Closed)
                        {
                            throw ADP.ClosedConnectionError();
                        }

                        bool ignored;
                        if (!_parser.TryRun(RunBehavior.ReturnImmediately, _command, this, null, _stateObj, out ignored))
                        {
                            moreRows = false;
                            return false;
                        }
                        if (_stateObj._pendingData)
                        {
                            if (!_stateObj.TryPeekByte(out b))
                            {
                                moreRows = false;
                                return false;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }

                    // Only return true when we are positioned on a row token.
                    if (IsRowToken(b))
                    {
                        moreRows = true;
                        return true;
                    }
                }
            }
            moreRows = false;
            return true;
        }

        private bool IsRowToken(byte token)
        {
            return TdsEnums.SQLROW == token || TdsEnums.SQLNBCROW == token;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/IsDBNull/*' />
        override public bool IsDBNull(int i)
        {
            if ((IsCommandBehavior(CommandBehavior.SequentialAccess)) && ((_sharedState._nextColumnHeaderToRead > i + 1) || (_lastColumnWithDataChunkRead > i)))
            {
                // Bug 447026 : A breaking change in System.Data .NET 4.5 for calling IsDBNull on commands in SequentialAccess mode
                // http://vstfdevdiv:8080/web/wi.aspx?pcguid=22f9acc9-569a-41ff-b6ac-fac1b6370209&id=447026
                // In .NET 4.0 and previous, it was possible to read a previous column using IsDBNull when in sequential mode
                // However, since it had already gone past the column, the current IsNull value is simply returned

                // To replicate this behavior we will skip CheckHeaderIsReady\ReadColumnHeader and instead just check that the reader is ready and the column is valid
                CheckMetaDataIsReady(columnIndex: i);
            }
            else
            {
                CheckHeaderIsReady(columnIndex: i, methodName: "IsDBNull");

                SetTimeout(_defaultTimeoutMilliseconds);

                ReadColumnHeader(i);    // header data only
            }

            return _data[i].IsNull;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/IsCommandBehavior/*' />
        protected internal bool IsCommandBehavior(CommandBehavior condition)
        {
            return (condition == (condition & _commandBehavior));
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/NextResult/*' />
        override public bool NextResult()
        {
            if (_currentTask != null)
            {
                throw SQL.PendingBeginXXXExists();
            }

            bool more;
            bool result;

            Debug.Assert(_stateObj == null || _stateObj._syncOverAsync, "Should not attempt pends in a synchronous call");
            result = TryNextResult(out more);

            if (!result)
            { throw SQL.SynchronousCallMayNotPend(); }
            return more;
        }

        // recordset is automatically positioned on the first result set
        private bool TryNextResult(out bool more)
        {
            SqlStatistics statistics = null;
            using (TryEventScope.Create("<sc.SqlDataReader.NextResult|API> {0}", ObjectID))
            {
                RuntimeHelpers.PrepareConstrainedRegions();

                try
                {
#if DEBUG
                    TdsParser.ReliabilitySection tdsReliabilitySection = new TdsParser.ReliabilitySection();

                    RuntimeHelpers.PrepareConstrainedRegions();
                    try
                    {
                        tdsReliabilitySection.Start();
#else
                {
#endif //DEBUG
                        statistics = SqlStatistics.StartTimer(Statistics);

                        SetTimeout(_defaultTimeoutMilliseconds);

                        if (IsClosed)
                        {
                            throw ADP.DataReaderClosed("NextResult");
                        }
                        _fieldNameLookup = null;

                        bool success = false; // WebData 100390
                        _hasRows = false; // reset HasRows

                        // if we are specifically only processing a single result, then read all the results off the wire and detach
                        if (IsCommandBehavior(CommandBehavior.SingleResult))
                        {
                            if (!TryCloseInternal(false /*closeReader*/))
                            {
                                more = false;
                                return false;
                            }

                            // In the case of not closing the reader, null out the metadata AFTER
                            // CloseInternal finishes - since CloseInternal may go to the wire
                            // and use the metadata.
                            ClearMetaData();
                            more = success;
                            return true;
                        }

                        if (null != _parser)
                        {
                            // if there are more rows, then skip them, the user wants the next result
                            bool moreRows = true;
                            while (moreRows)
                            {
                                if (!TryReadInternal(false, out moreRows))
                                { // don't reset set the timeout value
                                    more = false;
                                    return false;
                                }
                            }
                        }

                        // we may be done, so continue only if we have not detached ourselves from the parser
                        if (null != _parser)
                        {
                            bool moreResults;
                            if (!TryHasMoreResults(out moreResults))
                            {
                                more = false;
                                return false;
                            }
                            if (moreResults)
                            {
                                _metaDataConsumed = false;
                                _browseModeInfoConsumed = false;

                                switch (_altRowStatus)
                                {
                                    case ALTROWSTATUS.AltRow:
                                        int altRowId;
                                        if (!_parser.TryGetAltRowId(_stateObj, out altRowId))
                                        {
                                            more = false;
                                            return false;
                                        }
                                        _SqlMetaDataSet altMetaDataSet = _altMetaDataSetCollection.GetAltMetaData(altRowId);
                                        if (altMetaDataSet != null)
                                        {
                                            _metaData = altMetaDataSet;
                                        }
                                        Debug.Assert((_metaData != null), "Can't match up altrowmetadata");
                                        break;
                                    case ALTROWSTATUS.Done:
                                        // restore the row-metaData
                                        _metaData = _altMetaDataSetCollection.metaDataSet;
                                        Debug.Assert(_altRowStatus == ALTROWSTATUS.Done, "invalid AltRowStatus");
                                        _altRowStatus = ALTROWSTATUS.Null;
                                        break;
                                    default:
                                        if (!TryConsumeMetaData())
                                        {
                                            more = false;
                                            return false;
                                        }
                                        if (_metaData == null)
                                        {
                                            more = false;
                                            return true;
                                        }
                                        break;
                                }

                                success = true;
                            }
                            else
                            {
                                // detach the parser from this reader now
                                if (!TryCloseInternal(false /*closeReader*/))
                                {
                                    more = false;
                                    return false;
                                }

                                // In the case of not closing the reader, null out the metadata AFTER
                                // CloseInternal finishes - since CloseInternal may go to the wire
                                // and use the metadata.
                                if (!TrySetMetaData(null, false))
                                {
                                    more = false;
                                    return false;
                                }
                            }
                        }
                        else
                        {
                            // Clear state in case of Read calling CloseInternal() then user calls NextResult()
                            // MDAC 81986.  Or, also the case where the Read() above will do essentially the same
                            // thing.
                            ClearMetaData();
                        }

                        more = success;
                        return true;
                    }
#if DEBUG
                    finally
                    {
                        tdsReliabilitySection.Stop();
                    }
#endif //DEBUG
                }
                catch (System.OutOfMemoryException e)
                {
                    _isClosed = true;
                    if (null != _connection)
                    {
                        _connection.Abort(e);
                    }
                    throw;
                }
                catch (System.StackOverflowException e)
                {
                    _isClosed = true;
                    if (null != _connection)
                    {
                        _connection.Abort(e);
                    }
                    throw;
                }
                catch (System.Threading.ThreadAbortException e)
                {
                    _isClosed = true;
                    if (null != _connection)
                    {
                        _connection.Abort(e);
                    }
                    throw;
                }
                finally
                {
                    SqlStatistics.StopTimer(statistics);
                }
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/Read/*' />
        // user must call Read() to position on the first row
        override public bool Read()
        {
            if (_currentTask != null)
            {
                throw SQL.PendingBeginXXXExists();
            }

            bool more;
            bool result;

            Debug.Assert(_stateObj == null || _stateObj._syncOverAsync, "Should not attempt pends in a synchronous call");
            result = TryReadInternal(true, out more);

            if (!result)
            { throw SQL.SynchronousCallMayNotPend(); }
            return more;
        }

        // user must call Read() to position on the first row
        private bool TryReadInternal(bool setTimeout, out bool more)
        {
            SqlStatistics statistics = null;
            using (TryEventScope.Create("<sc.SqlDataReader.Read|API> {0}", ObjectID))
            {
                RuntimeHelpers.PrepareConstrainedRegions();

                try
                {
#if DEBUG
                    TdsParser.ReliabilitySection tdsReliabilitySection = new TdsParser.ReliabilitySection();

                    RuntimeHelpers.PrepareConstrainedRegions();
                    try
                    {
                        tdsReliabilitySection.Start();
#else
                {
#endif //DEBUG
                        statistics = SqlStatistics.StartTimer(Statistics);

                        if (null != _parser)
                        {
                            if (setTimeout)
                            {
                                SetTimeout(_defaultTimeoutMilliseconds);
                            }
                            if (_sharedState._dataReady)
                            {
                                if (!TryCleanPartialRead())
                                {
                                    more = false;
                                    return false;
                                }
                            }

                            // clear out our buffers
                            SqlBuffer.Clear(_data);

                            _sharedState._nextColumnHeaderToRead = 0;
                            _sharedState._nextColumnDataToRead = 0;
                            _sharedState._columnDataBytesRemaining = -1; // unknown
                            _lastColumnWithDataChunkRead = -1;

                            if (!_haltRead)
                            {
                                bool moreRows;
                                if (!TryHasMoreRows(out moreRows))
                                {
                                    more = false;
                                    return false;
                                }
                                if (moreRows)
                                {
                                    // read the row from the backend (unless it's an altrow were the marker is already inside the altrow ...)
                                    while (_stateObj._pendingData)
                                    {
                                        if (_altRowStatus != ALTROWSTATUS.AltRow)
                                        {
                                            // if this is an ordinary row we let the run method consume the ROW token
                                            if (!_parser.TryRun(RunBehavior.ReturnImmediately, _command, this, null, _stateObj, out _sharedState._dataReady))
                                            {
                                                more = false;
                                                return false;
                                            }
                                            if (_sharedState._dataReady)
                                            {
                                                break;
                                            }
                                        }
                                        else
                                        {
                                            // ALTROW token and AltrowId are already consumed ...
                                            Debug.Assert(_altRowStatus == ALTROWSTATUS.AltRow, "invalid AltRowStatus");
                                            _altRowStatus = ALTROWSTATUS.Done;
                                            _sharedState._dataReady = true;
                                            break;
                                        }
                                    }
                                    if (_sharedState._dataReady)
                                    {
                                        _haltRead = IsCommandBehavior(CommandBehavior.SingleRow);
                                        more = true;
                                        return true;
                                    }
                                }

                                if (!_stateObj._pendingData)
                                {
                                    if (!TryCloseInternal(false /*closeReader*/))
                                    {
                                        more = false;
                                        return false;
                                    }
                                }
                            }
                            else
                            {
                                // if we did not get a row and halt is true, clean off rows of result
                                // success must be false - or else we could have just read off row and set
                                // halt to true
                                bool moreRows;
                                if (!TryHasMoreRows(out moreRows))
                                {
                                    more = false;
                                    return false;
                                }
                                while (moreRows)
                                {
                                    // if we are in SingleRow mode, and we've read the first row,
                                    // read the rest of the rows, if any
                                    while (_stateObj._pendingData && !_sharedState._dataReady)
                                    {
                                        if (!_parser.TryRun(RunBehavior.ReturnImmediately, _command, this, null, _stateObj, out _sharedState._dataReady))
                                        {
                                            more = false;
                                            return false;
                                        }
                                    }

                                    if (_sharedState._dataReady)
                                    {
                                        if (!TryCleanPartialRead())
                                        {
                                            more = false;
                                            return false;
                                        }
                                    }

                                    // clear out our buffers
                                    SqlBuffer.Clear(_data);

                                    _sharedState._nextColumnHeaderToRead = 0;

                                    if (!TryHasMoreRows(out moreRows))
                                    {
                                        more = false;
                                        return false;
                                    }
                                }

                                // reset haltRead
                                _haltRead = false;
                            }
                        }
                        else if (IsClosed)
                        {
                            throw ADP.DataReaderClosed("Read");
                        }
                        more = false;

#if DEBUG
                        if ((!_sharedState._dataReady) && (_stateObj._pendingData))
                        {
                            byte token;
                            if (!_stateObj.TryPeekByte(out token))
                            {
                                return false;
                            }

                            Debug.Assert(TdsParser.IsValidTdsToken(token), $"DataReady is false, but next token is invalid: {token,-2:X2}");
                        }
#endif

                        return true;
                    }
#if DEBUG
                    finally
                    {
                        tdsReliabilitySection.Stop();
                    }
#endif //DEBUG
                }
                catch (System.OutOfMemoryException e)
                {
                    _isClosed = true;
                    SqlConnection con = _connection;
                    if (con != null)
                    {
                        con.Abort(e);
                    }
                    throw;
                }
                catch (System.StackOverflowException e)
                {
                    _isClosed = true;
                    SqlConnection con = _connection;
                    if (con != null)
                    {
                        con.Abort(e);
                    }
                    throw;
                }
                catch (System.Threading.ThreadAbortException e)
                {
                    _isClosed = true;
                    SqlConnection con = _connection;
                    if (con != null)
                    {
                        con.Abort(e);
                    }
                    throw;
                }
                finally
                {
                    SqlStatistics.StopTimer(statistics);
                }
            }
        }

        private void ReadColumn(int i, bool setTimeout = true, bool allowPartiallyReadColumn = false)
        {
            if (_currentTask != null)
            {
                throw ADP.AsyncOperationPending();
            }

            Debug.Assert(_stateObj == null || _stateObj._syncOverAsync, "Should not attempt pends in a synchronous call");
            bool result = TryReadColumn(i, setTimeout, allowPartiallyReadColumn);
            if (!result)
            {
                throw SQL.SynchronousCallMayNotPend();
            }
        }

        private bool TryReadColumn(int i, bool setTimeout, bool allowPartiallyReadColumn = false, bool forStreaming = false)
        {
            CheckDataIsReady(columnIndex: i, permitAsync: true, allowPartiallyReadColumn: allowPartiallyReadColumn);

            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
#if DEBUG
                TdsParser.ReliabilitySection tdsReliabilitySection = new TdsParser.ReliabilitySection();

                RuntimeHelpers.PrepareConstrainedRegions();
                try
                {
                    tdsReliabilitySection.Start();
#else
                {
#endif //DEBUG
                    Debug.Assert(_sharedState._nextColumnHeaderToRead <= _metaData.Length, "_sharedState._nextColumnHeaderToRead too large");
                    Debug.Assert(_sharedState._nextColumnDataToRead <= _metaData.Length, "_sharedState._nextColumnDataToRead too large");

                    if (setTimeout)
                    {
                        SetTimeout(_defaultTimeoutMilliseconds);
                    }

                    if (!TryReadColumnInternal(i, readHeaderOnly: false, forStreaming: forStreaming))
                    {
                        return false;
                    }

                    Debug.Assert(null != _data[i], " data buffer is null?");
                }
#if DEBUG
                finally
                {
                    tdsReliabilitySection.Stop();
                }
#endif //DEBUG
            }
            catch (System.OutOfMemoryException e)
            {
                _isClosed = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _isClosed = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _isClosed = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }

            return true;
        }

        private bool TryReadColumnData()
        {
            // If we've already read the value (because it was NULL) we don't
            // bother to read here.
            if (!_data[_sharedState._nextColumnDataToRead].IsNull)
            {
                _SqlMetaData columnMetaData = _metaData[_sharedState._nextColumnDataToRead];

                if (!_parser.TryReadSqlValue(_data[_sharedState._nextColumnDataToRead], columnMetaData, (int)_sharedState._columnDataBytesRemaining, _stateObj,
                                             _command != null ? _command.ColumnEncryptionSetting : SqlCommandColumnEncryptionSetting.UseConnectionSetting,
                                             columnMetaData.column, _command))
                { // will read UDTs as VARBINARY.
                    return false;
                }
                _sharedState._columnDataBytesRemaining = 0;
            }
            _sharedState._nextColumnDataToRead++;
            return true;
        }

        private void ReadColumnHeader(int i)
        {
            Debug.Assert(_stateObj == null || _stateObj._syncOverAsync, "Should not attempt pends in a synchronous call");
            bool result = TryReadColumnHeader(i);
            if (!result)
            { throw SQL.SynchronousCallMayNotPend(); }
        }

        private bool TryReadColumnHeader(int i)
        {
            if (!_sharedState._dataReady)
            {
                throw SQL.InvalidRead();
            }
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
#if DEBUG
                TdsParser.ReliabilitySection tdsReliabilitySection = new TdsParser.ReliabilitySection();

                RuntimeHelpers.PrepareConstrainedRegions();
                try
                {
                    tdsReliabilitySection.Start();
#endif //DEBUG
                    return TryReadColumnInternal(i, readHeaderOnly: true, forStreaming: false);
#if DEBUG
                }
                finally
                {
                    tdsReliabilitySection.Stop();
                }
#endif //DEBUG
            }
            catch (System.OutOfMemoryException e)
            {
                _isClosed = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
            catch (System.StackOverflowException e)
            {
                _isClosed = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
            catch (System.Threading.ThreadAbortException e)
            {
                _isClosed = true;
                if (null != _connection)
                {
                    _connection.Abort(e);
                }
                throw;
            }
        }

        internal bool TryReadColumnInternal(int i, bool readHeaderOnly/* = false*/, bool forStreaming)
        {
            AssertReaderState(requireData: true, permitAsync: true, columnIndex: i);

            // Check if we've already read the header already
            if (i < _sharedState._nextColumnHeaderToRead)
            {
                // Read the header, but we need to read the data
                if ((i == _sharedState._nextColumnDataToRead) && (!readHeaderOnly))
                {
                    return TryReadColumnData();
                }
                // Else we've already read the data, or we're reading the header only
                else
                {
                    // Ensure that, if we've read past the column, then we did store its data
                    Debug.Assert(i == _sharedState._nextColumnDataToRead ||                                                          // Either we haven't read the column yet
                        ((i + 1 < _sharedState._nextColumnDataToRead) && (IsCommandBehavior(CommandBehavior.SequentialAccess))) ||   // Or we're in sequential mode and we've read way past the column (i.e. it was not the last column we read)
                        (!_data[i].IsEmpty || _data[i].IsNull) ||                                                       // Or we should have data stored for the column (unless the column was null)
                        (_metaData[i].type == SqlDbType.Timestamp),                                                     // Or Dev11 Bug #336820, Dev10 Bug #479607 (SqlClient: IsDBNull always returns false for timestamp datatype)
                                                                                                                        //    Due to a bug in TdsParser.GetNullSqlValue, Timestamps' IsNull is not correctly set - so we need to bypass the check
                        "Gone past column, be we have no data stored for it");
                    return true;
                }
            }

            Debug.Assert(_data[i].IsEmpty || _data[i].IsNull, "re-reading column value?");

            // If we're in sequential access mode, we can safely clear out any
            // data from the previous column.
            bool isSequentialAccess = IsCommandBehavior(CommandBehavior.SequentialAccess);
            if (isSequentialAccess)
            {
                if (0 < _sharedState._nextColumnDataToRead)
                {
                    _data[_sharedState._nextColumnDataToRead - 1].Clear();
                }

                // Only wipe out the blob objects if they aren't for a 'future' column (i.e. we haven't read up to them yet)
                if ((_lastColumnWithDataChunkRead > -1) && (i > _lastColumnWithDataChunkRead))
                {
                    CloseActiveSequentialStreamAndTextReader();
                }
            }
            else if (_sharedState._nextColumnDataToRead < _sharedState._nextColumnHeaderToRead)
            {
                // We read the header but not the column for the previous column
                if (!TryReadColumnData())
                {
                    return false;
                }
                Debug.Assert(_sharedState._nextColumnDataToRead == _sharedState._nextColumnHeaderToRead);
            }

            // if we still have bytes left from the previous blob read, clear the wire and reset
            if (!TryResetBlobState())
            {
                return false;
            }

            do
            {
                _SqlMetaData columnMetaData = _metaData[_sharedState._nextColumnHeaderToRead];

                if (isSequentialAccess)
                {
                    if (_sharedState._nextColumnHeaderToRead < i)
                    {
                        // SkipValue is no-op if the column appears in NBC bitmask
                        // if not, it skips regular and PLP types
                        if (!_parser.TrySkipValue(columnMetaData, _sharedState._nextColumnHeaderToRead, _stateObj))
                        {
                            return false;
                        }

                        _sharedState._nextColumnDataToRead = _sharedState._nextColumnHeaderToRead;
                        _sharedState._nextColumnHeaderToRead++;
                    }
                    else if (_sharedState._nextColumnHeaderToRead == i)
                    {
                        bool isNull;
                        ulong dataLength;
                        if (
                            !_parser.TryProcessColumnHeader(
                                columnMetaData, 
                                _stateObj, 
                                _sharedState._nextColumnHeaderToRead, 
                                out isNull, 
                                out dataLength
                            )
                        )
                        {
                            return false;
                        }

                        _sharedState._nextColumnDataToRead = _sharedState._nextColumnHeaderToRead;
                        _sharedState._nextColumnHeaderToRead++;  // We read this one
                        _sharedState._columnDataBytesRemaining = (long)dataLength;

                        if (isNull)
                        {
                            if (columnMetaData.type != SqlDbType.Timestamp)
                            {
                                TdsParser.GetNullSqlValue(
                                    _data[_sharedState._nextColumnDataToRead],
                                    columnMetaData,
                                    _command != null ? _command.ColumnEncryptionSetting : SqlCommandColumnEncryptionSetting.UseConnectionSetting,
                                    _parser.Connection
                                );
                            }
                        }
                        else
                        {
                            if (!readHeaderOnly && !forStreaming)
                            {
                                // If we're in sequential mode try to read the data and then if it succeeds update shared
                                // state so there are no remaining bytes and advance the next column to read
                                if (
                                    !_parser.TryReadSqlValue(
                                        _data[_sharedState._nextColumnDataToRead], 
                                        columnMetaData, 
                                        (int)dataLength, _stateObj,
                                        _command != null ? _command.ColumnEncryptionSetting : SqlCommandColumnEncryptionSetting.UseConnectionSetting,
                                        columnMetaData.column
                                    )
                                )
                                { // will read UDTs as VARBINARY.
                                    return false;
                                }
                                _sharedState._columnDataBytesRemaining = 0;
                                _sharedState._nextColumnDataToRead++;
                            }
                            else
                            {
                                _sharedState._columnDataBytesRemaining = (long)dataLength;
                            }
                        }
                    }
                    else
                    {
                        Debug.Assert(false, "we have read past the column somehow, this is an error");
                    }
                }
                else
                {
                    bool isNull;
                    ulong dataLength;
                    if (!_parser.TryProcessColumnHeader(columnMetaData, _stateObj, _sharedState._nextColumnHeaderToRead, out isNull, out dataLength))
                    {
                        return false;
                    }

                    _sharedState._nextColumnDataToRead = _sharedState._nextColumnHeaderToRead;
                    _sharedState._nextColumnHeaderToRead++;  // We read this one

                    // Trigger new behavior for RowVersion to send DBNull.Value by allowing entry for Timestamp or discard entry for Timestamp for legacy support.
                    // if LegacyRowVersionNullBehavior is enabled, Timestamp type must enter "else" block.
                    if (isNull && (!LocalAppContextSwitches.LegacyRowVersionNullBehavior || columnMetaData.type != SqlDbType.Timestamp))
                    {
                        TdsParser.GetNullSqlValue(
                            _data[_sharedState._nextColumnDataToRead],
                            columnMetaData,
                            _command != null ? _command.ColumnEncryptionSetting : SqlCommandColumnEncryptionSetting.UseConnectionSetting,
                            _parser.Connection
                        );

                        if (!readHeaderOnly)
                        {
                            _sharedState._nextColumnDataToRead++;
                        }
                    }
                    else
                    {
                        if ((i > _sharedState._nextColumnDataToRead) || (!readHeaderOnly))
                        {
                            // If we're not in sequential access mode, we have to
                            // save the data we skip over so that the consumer
                            // can read it out of order
                            if (!_parser.TryReadSqlValue(_data[_sharedState._nextColumnDataToRead], columnMetaData, (int)dataLength, _stateObj,
                                                         _command != null ? _command.ColumnEncryptionSetting : SqlCommandColumnEncryptionSetting.UseConnectionSetting,
                                                         columnMetaData.column))
                            { // will read UDTs as VARBINARY.
                                return false;
                            }
                            _sharedState._nextColumnDataToRead++;
                        }
                        else
                        {
                            _sharedState._columnDataBytesRemaining = (long)dataLength;
                        }
                    }
                }

                if (_snapshot != null)
                {
                    // reset snapshot to save memory use.  We can safely do that here because all SqlDataReader values are stable.
                    // The retry logic can use the current values to get back to the right state.
                    _snapshot = null;
                    PrepareAsyncInvocation(useSnapshot: true);
                }
            } while (_sharedState._nextColumnHeaderToRead <= i);

            return true;
        }

        // Estimates if there is enough data available to read the number of columns requested
        private bool WillHaveEnoughData(int targetColumn, bool headerOnly = false)
        {
            AssertReaderState(requireData: true, permitAsync: true, columnIndex: targetColumn);

            if ((_lastColumnWithDataChunkRead == _sharedState._nextColumnDataToRead) && (_metaData[_lastColumnWithDataChunkRead].metaType.IsPlp))
            {
                // In the middle of reading a Plp - no idea how much is left
                return false;
            }

            int bytesRemaining = Math.Min(checked(_stateObj._inBytesRead - _stateObj._inBytesUsed), _stateObj._inBytesPacket);

            // There are some parts of our code that peeks at the next token after doing its read
            // So we will make sure that there is always a spare byte for it to look at
            bytesRemaining--;

            if ((targetColumn >= _sharedState._nextColumnDataToRead) && (_sharedState._nextColumnDataToRead < _sharedState._nextColumnHeaderToRead))
            {
                if (_sharedState._columnDataBytesRemaining > bytesRemaining)
                {
                    // The current column needs more data than we currently have
                    // NOTE: Since the Long data types (TEXT, IMAGE, NTEXT) can have a size of Int32.MaxValue we cannot simply subtract
                    // _columnDataBytesRemaining from bytesRemaining and then compare it to zero as this may lead to an overflow
                    return false;
                }
                else
                {
                    // Already read the header, so subtract actual data size
                    bytesRemaining = checked(bytesRemaining - (int)_sharedState._columnDataBytesRemaining);
                }
            }

            // For each column that we need to read, subtract the size of its header and the size of its data
            int currentColumn = _sharedState._nextColumnHeaderToRead;
            while ((bytesRemaining >= 0) && (currentColumn <= targetColumn))
            {
                // Check NBC first
                if (!_stateObj.IsNullCompressionBitSet(currentColumn))
                {

                    // NOTE: This is mostly duplicated from TryProcessColumnHeaderNoNBC and TryGetTokenLength
                    var metaType = _metaData[currentColumn].metaType;
                    if ((metaType.IsLong) || (metaType.IsPlp) || (metaType.SqlDbType == SqlDbType.Udt) || (metaType.SqlDbType == SqlDbType.Structured))
                    {
                        // Plp, Udt and TVP types have an unknownable size - so return that the estimate failed
                        return false;
                    }
                    int maxHeaderSize;
                    byte typeAndMask = (byte)(_metaData[currentColumn].tdsType & TdsEnums.SQLLenMask);
                    if ((typeAndMask == TdsEnums.SQLVarLen) || (typeAndMask == TdsEnums.SQLVarCnt))
                    {
                        if (0 != (_metaData[currentColumn].tdsType & 0x80))
                        {
                            // UInt16 represents size
                            maxHeaderSize = 2;
                        }
                        else if (0 == (_metaData[currentColumn].tdsType & 0x0c))
                        {
                            // UInt32 represents size
                            maxHeaderSize = 4;
                        }
                        else
                        {
                            // Byte represents size
                            maxHeaderSize = 1;
                        }
                    }
                    else
                    {
                        maxHeaderSize = 0;
                    }

                    bytesRemaining = checked(bytesRemaining - maxHeaderSize);
                    if ((currentColumn < targetColumn) || (!headerOnly))
                    {
                        bytesRemaining = checked(bytesRemaining - _metaData[currentColumn].length);
                    }
                }

                currentColumn++;
            }

            return (bytesRemaining >= 0);
        }

        // clean remainder bytes for the column off the wire
        private bool TryResetBlobState()
        {
            Debug.Assert(null != _stateObj, "null state object"); // _parser may be null at this point
            AssertReaderState(requireData: true, permitAsync: true);
            Debug.Assert(_sharedState._nextColumnHeaderToRead <= _metaData.Length, "_sharedState._nextColumnHeaderToRead too large");

            // If we haven't already entirely read the column
            if (_sharedState._nextColumnDataToRead < _sharedState._nextColumnHeaderToRead)
            {
                if ((_sharedState._nextColumnHeaderToRead > 0) && (_metaData[_sharedState._nextColumnHeaderToRead - 1].metaType.IsPlp))
                {
                    if (_stateObj._longlen != 0)
                    {
                        ulong ignored;
                        if (!_stateObj.Parser.TrySkipPlpValue(UInt64.MaxValue, _stateObj, out ignored))
                        {
                            return false;
                        }
                    }
                    if (_streamingXml != null)
                    {
                        SqlStreamingXml localSXml = _streamingXml;
                        _streamingXml = null;
                        localSXml.Close();
                    }
                }
                else if (0 < _sharedState._columnDataBytesRemaining)
                {
                    if (!_stateObj.TrySkipLongBytes(_sharedState._columnDataBytesRemaining))
                    {
                        return false;
                    }
                }
            }
#if DEBUG
            else
            {
                Debug.Assert((_sharedState._columnDataBytesRemaining == 0 || _sharedState._columnDataBytesRemaining == -1) && _stateObj._longlen == 0, "Haven't read header yet, but column is partially read?");
            }
#endif

            _sharedState._columnDataBytesRemaining = 0;
            _columnDataBytesRead = 0;
            _columnDataCharsRead = 0;
            _columnDataChars = null;
            _columnDataCharsIndex = -1;
            _stateObj._plpdecoder = null;

            return true;
        }

        private void CloseActiveSequentialStreamAndTextReader()
        {
            if (_currentStream != null)
            {
                _currentStream.SetClosed();
                _currentStream = null;
            }
            if (_currentTextReader != null)
            {
                _currentTextReader.SetClosed();
                _currentStream = null;
            }
        }

        private void RestoreServerSettings(TdsParser parser, TdsParserStateObject stateObj)
        {
            // turn off any set options
            if (null != parser && null != _resetOptionsString)
            {
                // It is possible for this to be called during connection close on a
                // broken connection, so check state first.
                if (parser.State == TdsParserState.OpenLoggedIn)
                {
                    SqlClientEventSource.Log.TryCorrelationTraceEvent("<sc.SqlDataReader.RestoreServerSettings|Info|Correlation> ObjectID {0}, ActivityID '{1}'", ObjectID, ActivityCorrelator.Current);
                    Task executeTask = parser.TdsExecuteSQLBatch(_resetOptionsString, (_command != null) ? _command.CommandTimeout : 0, null, stateObj, sync: true);
                    Debug.Assert(executeTask == null, "Shouldn't get a task when doing sync writes");

                    // must execute this one synchronously as we can't retry
                    parser.Run(RunBehavior.UntilDone, _command, this, null, stateObj);
                }
                _resetOptionsString = null;
            }
        }

        internal bool TrySetAltMetaDataSet(_SqlMetaDataSet metaDataSet, bool metaDataConsumed)
        {
            if (_altMetaDataSetCollection == null)
            {
                _altMetaDataSetCollection = new _SqlMetaDataSetCollection();
            }
            else if (_snapshot != null && object.ReferenceEquals(_snapshot._altMetaDataSetCollection, _altMetaDataSetCollection))
            {
                _altMetaDataSetCollection = (_SqlMetaDataSetCollection)_altMetaDataSetCollection.Clone();
            }
            _altMetaDataSetCollection.SetAltMetaData(metaDataSet);
            _metaDataConsumed = metaDataConsumed;
            if (_metaDataConsumed && null != _parser)
            {
                byte b;
                if (!_stateObj.TryPeekByte(out b))
                {
                    return false;
                }
                if (TdsEnums.SQLORDER == b)
                {
                    bool ignored;
                    if (!_parser.TryRun(RunBehavior.ReturnImmediately, _command, this, null, _stateObj, out ignored))
                    {
                        return false;
                    }
                    if (!_stateObj.TryPeekByte(out b))
                    {
                        return false;
                    }
                }
                if (b == TdsEnums.SQLINFO)
                {
                    try
                    {
                        _stateObj._accumulateInfoEvents = true;
                        bool ignored;
                        if (!_parser.TryRun(RunBehavior.ReturnImmediately, _command, null, null, _stateObj, out ignored))
                        {
                            return false;
                        }
                    }
                    finally
                    {
                        _stateObj._accumulateInfoEvents = false;
                    }
                    if (!_stateObj.TryPeekByte(out b))
                    {
                        return false;
                    }
                }
                _hasRows = IsRowToken(b);
            }
            if (metaDataSet != null)
            {
                if (_data == null || _data.Length < metaDataSet.Length)
                {
                    _data = SqlBuffer.CreateBufferArray(metaDataSet.Length);
                }
            }
            return true;
        }

        private void ClearMetaData()
        {
            _metaData = null;
            _tableNames = null;
            _fieldNameLookup = null;
            _metaDataConsumed = false;
            _browseModeInfoConsumed = false;
        }

        internal bool TrySetSensitivityClassification(SensitivityClassification sensitivityClassification)
        {
            SensitivityClassification = sensitivityClassification;
            return true;
        }

        internal bool TrySetMetaData(_SqlMetaDataSet metaData, bool moreInfo)
        {
            _metaData = metaData;

            // get rid of cached metadata info as well
            _tableNames = null;
            if (_metaData != null)
            {
                _metaData.schemaTable = null;
                _data = SqlBuffer.CreateBufferArray(metaData.Length);
            }

            _fieldNameLookup = null;

            if (null != metaData)
            {
                // we are done consuming metadata only if there is no moreInfo
                if (!moreInfo)
                {
                    _metaDataConsumed = true;

                    if (_parser != null)
                    { // There is a valid case where parser is null
                        // Peek, and if row token present, set _hasRows true since there is a
                        // row in the result
                        byte b;
                        if (!_stateObj.TryPeekByte(out b))
                        {
                            return false;
                        }

                        // UNDONE - should we be consuming tokens here??? Maybe we should be calling HasMoreRows?
                        // Would that have other side effects?

                        // simply rip the order token off the wire
                        if (b == TdsEnums.SQLORDER)
                        {                     //  same logic as SetAltMetaDataSet
                                              // Devnote: That's not the right place to process TDS
                                              // Can this result in Reentrance to Run?
                                              //
                            bool ignored;
                            if (!_parser.TryRun(RunBehavior.ReturnImmediately, null, null, null, _stateObj, out ignored))
                            {
                                return false;
                            }
                            if (!_stateObj.TryPeekByte(out b))
                            {
                                return false;
                            }
                        }
                        if (b == TdsEnums.SQLINFO)
                        {
                            // VSTFDEVDIV713926
                            // We are accumulating informational events and fire them at next
                            // TdsParser.Run purely to avoid breaking change
                            try
                            {
                                _stateObj._accumulateInfoEvents = true;
                                bool ignored;
                                if (!_parser.TryRun(RunBehavior.ReturnImmediately, null, null, null, _stateObj, out ignored))
                                {
                                    return false;
                                }
                            }
                            finally
                            {
                                _stateObj._accumulateInfoEvents = false;
                            }
                            if (!_stateObj.TryPeekByte(out b))
                            {
                                return false;
                            }
                        }
                        _hasRows = IsRowToken(b);
                        if (TdsEnums.SQLALTMETADATA == b)
                        {
                            _metaDataConsumed = false;
                        }
                    }
                }
            }
            else
            {
                _metaDataConsumed = false;
            }

            _browseModeInfoConsumed = false;
            return true;
        }

        private void SetTimeout(long timeoutMilliseconds)
        {
            // WebData 111653,112003 -- we now set timeouts per operation, not
            // per command (it's not supposed to be a cumulative per command).
            TdsParserStateObject stateObj = _stateObj;
            if (null != stateObj)
            {
                stateObj.SetTimeoutMilliseconds(timeoutMilliseconds);
            }
        }

        private bool HasActiveStreamOrTextReaderOnColumn(int columnIndex)
        {
            bool active = false;

            active |= ((_currentStream != null) && (_currentStream.ColumnIndex == columnIndex));
            active |= ((_currentTextReader != null) && (_currentTextReader.ColumnIndex == columnIndex));

            return active;
        }

        private void CheckMetaDataIsReady()
        {
            if (_currentTask != null)
            {
                throw ADP.AsyncOperationPending();
            }
            if (MetaData == null)
            {
                throw SQL.InvalidRead();
            }
        }

        private void CheckMetaDataIsReady(int columnIndex, bool permitAsync = false)
        {
            if ((!permitAsync) && (_currentTask != null))
            {
                throw ADP.AsyncOperationPending();
            }
            if (MetaData == null)
            {
                throw SQL.InvalidRead();
            }
            if ((columnIndex < 0) || (columnIndex >= _metaData.Length))
            {
                throw ADP.IndexOutOfRange();
            }
        }

        private void CheckDataIsReady()
        {
            if (_currentTask != null)
            {
                throw ADP.AsyncOperationPending();
            }
            Debug.Assert(!_sharedState._dataReady || _metaData != null, "Data is ready, but there is no metadata?");
            if ((!_sharedState._dataReady) || (_metaData == null))
            {
                throw SQL.InvalidRead();
            }
        }

        private void CheckHeaderIsReady(int columnIndex, bool permitAsync = false, string methodName = null)
        {
            if (_isClosed)
            {
                throw ADP.DataReaderClosed(methodName ?? "CheckHeaderIsReady");
            }
            if ((!permitAsync) && (_currentTask != null))
            {
                throw ADP.AsyncOperationPending();
            }
            Debug.Assert(!_sharedState._dataReady || _metaData != null, "Data is ready, but there is no metadata?");
            if ((!_sharedState._dataReady) || (_metaData == null))
            {
                throw SQL.InvalidRead();
            }
            if ((columnIndex < 0) || (columnIndex >= _metaData.Length))
            {
                throw ADP.IndexOutOfRange();
            }
            if ((IsCommandBehavior(CommandBehavior.SequentialAccess)) &&                                          // Only for sequential access
                ((_sharedState._nextColumnHeaderToRead > columnIndex + 1) || (_lastColumnWithDataChunkRead > columnIndex)))
            {  // Read past column
                throw ADP.NonSequentialColumnAccess(columnIndex, Math.Max(_sharedState._nextColumnHeaderToRead - 1, _lastColumnWithDataChunkRead));
            }
        }

        private void CheckDataIsReady(int columnIndex, bool allowPartiallyReadColumn = false, bool permitAsync = false, string methodName = null)
        {
            if (_isClosed)
            {
                throw ADP.DataReaderClosed(methodName ?? "CheckDataIsReady");
            }
            if ((!permitAsync) && (_currentTask != null))
            {
                throw ADP.AsyncOperationPending();
            }
            Debug.Assert(!_sharedState._dataReady || _metaData != null, "Data is ready, but there is no metadata?");
            if ((!_sharedState._dataReady) || (_metaData == null))
            {
                throw SQL.InvalidRead();
            }
            if ((columnIndex < 0) || (columnIndex >= _metaData.Length))
            {
                throw ADP.IndexOutOfRange();
            }
            if ((IsCommandBehavior(CommandBehavior.SequentialAccess)) &&                                    // Only for sequential access
                ((_sharedState._nextColumnDataToRead > columnIndex) || (_lastColumnWithDataChunkRead > columnIndex) ||   // Read past column
                ((!allowPartiallyReadColumn) && (_lastColumnWithDataChunkRead == columnIndex)) ||           // Partially read column
                ((allowPartiallyReadColumn) && (HasActiveStreamOrTextReaderOnColumn(columnIndex)))))
            {      // Has a Stream or TextReader on a partially-read column
                throw ADP.NonSequentialColumnAccess(columnIndex, Math.Max(_sharedState._nextColumnDataToRead, _lastColumnWithDataChunkRead + 1));
            }
        }

        [Conditional("DEBUG")]
        private void AssertReaderState(bool requireData, bool permitAsync, int? columnIndex = null, bool enforceSequentialAccess = false)
        {
            Debug.Assert(!_sharedState._dataReady || _metaData != null, "Data is ready, but there is no metadata?");
            Debug.Assert(permitAsync || _currentTask == null, "Call while async operation is pending");
            Debug.Assert(_metaData != null, "_metaData is null, check MetaData before calling this method");
            Debug.Assert(!requireData || _sharedState._dataReady, "No data is ready to be read");
            if (columnIndex.HasValue)
            {
                Debug.Assert(columnIndex.Value >= 0 && columnIndex.Value < _metaData.Length, "Invalid column index");
                Debug.Assert((!enforceSequentialAccess) || (!IsCommandBehavior(CommandBehavior.SequentialAccess)) || ((_sharedState._nextColumnDataToRead <= columnIndex) && (_lastColumnWithDataChunkRead <= columnIndex)), "Already read past column");
            }
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/NextResultAsync/*' />
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
        {
            using (TryEventScope.Create("<sc.SqlDataReader.NextResultAsync|API> {0}", ObjectID))
            {

                TaskCompletionSource<bool> source = new TaskCompletionSource<bool>();

                if (IsClosed)
                {
                    source.SetException(ADP.ExceptionWithStackTrace(ADP.DataReaderClosed("NextResultAsync")));
                    return source.Task;
                }

                IDisposable registration = null;
                if (cancellationToken.CanBeCanceled)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        source.SetCanceled();
                        return source.Task;
                    }
                    registration = cancellationToken.Register(SqlCommand.s_cancelIgnoreFailure, _command);
                }

                Task original = Interlocked.CompareExchange(ref _currentTask, source.Task, null);
                if (original != null)
                {
                    source.SetException(ADP.ExceptionWithStackTrace(SQL.PendingBeginXXXExists()));
                    return source.Task;
                }

                // Check if cancellation due to close is requested (this needs to be done after setting _currentTask)
                if (_cancelAsyncOnCloseToken.IsCancellationRequested)
                {
                    source.SetCanceled();
                    _currentTask = null;
                    return source.Task;
                }

                return InvokeAsyncCall(new HasNextResultAsyncCallContext(this, source, registration));
            }
        }

        private static Task<bool> NextResultAsyncExecute(Task task, object state)
        {
            HasNextResultAsyncCallContext context = (HasNextResultAsyncCallContext)state;
            if (task != null)
            {
                SqlClientEventSource.Log.TryTraceEvent("<sc.SqlDataReader.NextResultAsyncExecute> attempt retry {0}", context._reader.ObjectID);
                context._reader.PrepareForAsyncContinuation();
            }

            if (context._reader.TryNextResult(out bool more))
            {
                // completed 
                return more ? ADP.TrueTask : ADP.FalseTask;
            }

            return context._reader.ExecuteAsyncCall(context);
        }

        // NOTE: This will return null if it completed sequentially
        // If this returns null, then you can use bytesRead to see how many bytes were read - otherwise bytesRead should be ignored
        internal Task<int> GetBytesAsync(int columnIndex, byte[] buffer, int index, int length, int timeout, CancellationToken cancellationToken, out int bytesRead)
        {
            AssertReaderState(requireData: true, permitAsync: true, columnIndex: columnIndex, enforceSequentialAccess: true);
            Debug.Assert(IsCommandBehavior(CommandBehavior.SequentialAccess));

            bytesRead = 0;
            if (IsClosed)
            {
                TaskCompletionSource<int> source = new TaskCompletionSource<int>();
                source.SetException(ADP.ExceptionWithStackTrace(ADP.DataReaderClosed("GetBytesAsync")));
                return source.Task;
            }

            if (_currentTask != null)
            {
                TaskCompletionSource<int> source = new TaskCompletionSource<int>();
                source.SetException(ADP.ExceptionWithStackTrace(ADP.AsyncOperationPending()));
                return source.Task;
            }

            if (cancellationToken.CanBeCanceled)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
            }

            var context = new GetBytesAsyncCallContext(this)
            {
                columnIndex = columnIndex,
                buffer = buffer,
                index = index,
                length = length,
                timeout = timeout,
                cancellationToken = cancellationToken,
            };

            // Check if we need to skip columns
            Debug.Assert(_sharedState._nextColumnDataToRead <= _lastColumnWithDataChunkRead, "Non sequential access");
            if ((_sharedState._nextColumnHeaderToRead <= _lastColumnWithDataChunkRead) || (_sharedState._nextColumnDataToRead < _lastColumnWithDataChunkRead))
            {
                TaskCompletionSource<int> source = new TaskCompletionSource<int>();
                Task original = Interlocked.CompareExchange(ref _currentTask, source.Task, null);
                if (original != null)
                {
                    source.SetException(ADP.ExceptionWithStackTrace(ADP.AsyncOperationPending()));
                    return source.Task;
                }

                // Timeout
                CancellationToken timeoutToken = CancellationToken.None;
                CancellationTokenSource timeoutCancellationSource = null;
                if (timeout > 0)
                {
                    timeoutCancellationSource = new CancellationTokenSource();
                    timeoutCancellationSource.CancelAfter(timeout);
                    timeoutToken = timeoutCancellationSource.Token;
                }

                context._disposable = timeoutCancellationSource;
                context.timeoutToken = timeoutToken;
                context._source = source;

                PrepareAsyncInvocation(useSnapshot: true);

                return InvokeAsyncCall(context);
            }
            else
            {
                // We're already at the correct column, just read the data
                context.mode = GetBytesAsyncCallContext.OperationMode.Read;

                // Switch to async
                PrepareAsyncInvocation(useSnapshot: false);

                try
                {
                    return GetBytesAsyncReadDataStage(context, false, out bytesRead);
                }
                catch
                {
                    CleanupAfterAsyncInvocation();
                    throw;
                }
            }
        }

        private static Task<int> GetBytesAsyncSeekExecute(Task task, object state)
        {
            GetBytesAsyncCallContext context = (GetBytesAsyncCallContext)state;
            SqlDataReader reader = context._reader;

            Debug.Assert(context.mode == GetBytesAsyncCallContext.OperationMode.Seek, "context.mode must be Seek to check if seeking can resume");

            if (task != null)
            {
                reader.PrepareForAsyncContinuation();
            }
            // Prepare for stateObj timeout
            reader.SetTimeout(reader._defaultTimeoutMilliseconds);

            if (reader.TryReadColumnHeader(context.columnIndex))
            {
                // Only once we have read upto where we need to be can we check the cancellation tokens (otherwise we will be in an unknown state)

                if (context.cancellationToken.IsCancellationRequested)
                {
                    // User requested cancellation
                    return Task.FromCanceled<int>(context.cancellationToken);
                }
                else if (context.timeoutToken.IsCancellationRequested)
                {
                    // Timeout
                    return ADP.CreatedTaskWithException<int>(ADP.ExceptionWithStackTrace(ADP.IO(SQLMessage.Timeout())));
                }
                else
                {
                    // Upto the correct column - continue to read
                    context.mode = GetBytesAsyncCallContext.OperationMode.Read;
                    reader.SwitchToAsyncWithoutSnapshot();
                    int totalBytesRead;
                    var readTask = reader.GetBytesAsyncReadDataStage(context, true, out totalBytesRead);
                    if (readTask == null)
                    {
                        // Completed synchronously
                        return Task.FromResult<int>(totalBytesRead);
                    }
                    else
                    {
                        return readTask;
                    }
                }
            }
            else
            {
                return reader.ExecuteAsyncCall(context);
            }
        }

        private static Task<int> GetBytesAsyncReadExecute(Task task, object state)
        {
            var context = (GetBytesAsyncCallContext)state;
            SqlDataReader reader = context._reader;

            Debug.Assert(context.mode == GetBytesAsyncCallContext.OperationMode.Read, "context.mode must be Read to check if read can resume");

            reader.PrepareForAsyncContinuation();

            if (context.cancellationToken.IsCancellationRequested)
            {
                // User requested cancellation
                return Task.FromCanceled<int>(context.cancellationToken);
            }
            else if (context.timeoutToken.IsCancellationRequested)
            {
                // Timeout
                return Task.FromException<int>(ADP.ExceptionWithStackTrace(ADP.IO(SQLMessage.Timeout())));
            }
            else
            {
                // Prepare for stateObj timeout
                reader.SetTimeout(reader._defaultTimeoutMilliseconds);

                int bytesReadThisIteration;
                bool result = reader.TryGetBytesInternalSequential(
                    context.columnIndex,
                    context.buffer,
                    context.index + context.totalBytesRead,
                    context.length - context.totalBytesRead,
                    out bytesReadThisIteration
                );
                context.totalBytesRead += bytesReadThisIteration;
                Debug.Assert(context.totalBytesRead <= context.length, "Read more bytes than required");

                if (result)
                {
                    return Task.FromResult<int>(context.totalBytesRead);
                }
                else
                {
                    return reader.ExecuteAsyncCall(context);
                }
            }
        }

        private Task<int> GetBytesAsyncReadDataStage(GetBytesAsyncCallContext context, bool isContinuation, out int bytesRead)
        {
            Debug.Assert(context.mode == GetBytesAsyncCallContext.OperationMode.Read, "context.Mode must be Read to read data");

            _lastColumnWithDataChunkRead = context.columnIndex;
            TaskCompletionSource<int> source = null;

            // Prepare for stateObj timeout
            SetTimeout(_defaultTimeoutMilliseconds);

            // Try to read without any continuations (all the data may already be in the stateObj's buffer)
            bool filledBuffer = context._reader.TryGetBytesInternalSequential(
                context.columnIndex,
                context.buffer,
                context.index + context.totalBytesRead,
                context.length - context.totalBytesRead,
                out bytesRead
            );
            context.totalBytesRead += bytesRead;
            Debug.Assert(context.totalBytesRead <= context.length, "Read more bytes than required");

            if (!filledBuffer)
            {
                // This will be the 'state' for the callback
                int totalBytesRead = bytesRead;

                if (!isContinuation)
                {
                    // This is the first async operation which is happening - setup the _currentTask and timeout
                    Debug.Assert(context._source == null, "context._source should not be non-null when trying to change to async");
                    source = new TaskCompletionSource<int>();
                    Task original = Interlocked.CompareExchange(ref _currentTask, source.Task, null);
                    if (original != null)
                    {
                        source.SetException(ADP.ExceptionWithStackTrace(ADP.AsyncOperationPending()));
                        return source.Task;
                    }

                    context._source = source;
                    // Check if cancellation due to close is requested (this needs to be done after setting _currentTask)
                    if (_cancelAsyncOnCloseToken.IsCancellationRequested)
                    {
                        source.SetCanceled();
                        _currentTask = null;
                        return source.Task;
                    }

                    // Timeout
                    Debug.Assert(context.timeoutToken == CancellationToken.None, "TimeoutToken is set when GetBytesAsyncReadDataStage is not a continuation");
                    if (context.timeout > 0)
                    {
                        CancellationTokenSource timeoutCancellationSource = new CancellationTokenSource();
                        timeoutCancellationSource.CancelAfter(context.timeout);
                        Debug.Assert(context._disposable is null, "setting context.disposable would lose the previous dispoable");
                        context._disposable = timeoutCancellationSource;
                        context.timeoutToken = timeoutCancellationSource.Token;
                    }
                }

                Task<int> retryTask = ExecuteAsyncCall(context);
                if (isContinuation)
                {
                    // Let the caller handle cleanup\completing
                    return retryTask;
                }
                else
                {
                    Debug.Assert(context._source != null, "context._source shuld not be null when continuing");
                    // setup for cleanup\completing
                    retryTask.ContinueWith(
                        continuationAction: AAsyncCallContext<int>.s_completeCallback,
                        state: context,
                        TaskScheduler.Default
                    );
                    return source.Task;
                }
            }

            if (!isContinuation)
            {
                // If this is the first async op, we need to cleanup
                CleanupAfterAsyncInvocation();
            }
            // Completed synchronously, return null
            return null;
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/ReadAsync/*' />
        public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            using (TryEventScope.Create("<sc.SqlDataReader.ReadAsync|API> {0}", ObjectID))
            {
                if (IsClosed)
                {
                    return ADP.CreatedTaskWithException<bool>(ADP.ExceptionWithStackTrace(ADP.DataReaderClosed("ReadAsync")));
                }

                // If user's token is canceled, return a canceled task
                if (cancellationToken.IsCancellationRequested)
                {
                    return ADP.CreatedTaskWithCancellation<bool>();
                }

                // Check for existing async
                if (_currentTask != null)
                {
                    return ADP.CreatedTaskWithException<bool>(ADP.ExceptionWithStackTrace(SQL.PendingBeginXXXExists()));
                }

                // These variables will be captured in moreFunc so that we can skip searching for a row token once one has been read
                bool rowTokenRead = false;
                bool more = false;

                // Shortcut, do we have enough data to immediately do the ReadAsync?
                try
                {
                    // First, check if we can finish reading the current row
                    // NOTE: If we are in SingleRow mode and we've read that single row (i.e. _haltRead == true), then skip the shortcut
                    if ((!_haltRead) && ((!_sharedState._dataReady) || (WillHaveEnoughData(_metaData.Length - 1))))
                    {

#if DEBUG
                        try
                        {
                            _stateObj._shouldHaveEnoughData = true;
#endif
                        if (_sharedState._dataReady)
                        {
                            // Clean off current row
                            CleanPartialReadReliable();
                        }

                        // If there a ROW token ready (as well as any metadata for the row)
                        if (_stateObj.IsRowTokenReady())
                        {
                            // Read the ROW token
                            bool result = TryReadInternal(true, out more);
                            Debug.Assert(result, "Should not have run out of data");

                            rowTokenRead = true;
                            if (more)
                            {
                                // Sequential mode, nothing left to do
                                if (IsCommandBehavior(CommandBehavior.SequentialAccess))
                                {
                                    return ADP.TrueTask;
                                }
                                // For non-sequential, check if we can read the row data now
                                else if (WillHaveEnoughData(_metaData.Length - 1))
                                {
                                    // Read row data
                                    result = TryReadColumn(_metaData.Length - 1, setTimeout: true);
                                    Debug.Assert(result, "Should not have run out of data");
                                    return ADP.TrueTask;
                                }
                            }
                            else
                            {
                                // No data left, return
                                return ADP.FalseTask;
                            }
                        }
#if DEBUG
                        }
                        finally
                        {
                            _stateObj._shouldHaveEnoughData = false;
                        }
#endif
                    }
                }
                catch (Exception ex)
                {
                    if (!ADP.IsCatchableExceptionType(ex))
                    {
                        throw;
                    }
                    return ADP.CreatedTaskWithException<bool>(ex);
                }

                TaskCompletionSource<bool> source = new TaskCompletionSource<bool>();
                Task original = Interlocked.CompareExchange(ref _currentTask, source.Task, null);
                if (original != null)
                {
                    source.SetException(ADP.ExceptionWithStackTrace(SQL.PendingBeginXXXExists()));
                    return source.Task;
                }

                // Check if cancellation due to close is requested (this needs to be done after setting _currentTask)
                if (_cancelAsyncOnCloseToken.IsCancellationRequested)
                {
                    source.SetCanceled();
                    _currentTask = null;
                    return source.Task;
                }

                IDisposable registration = null;
                if (cancellationToken.CanBeCanceled)
                {
                    registration = cancellationToken.Register(SqlCommand.s_cancelIgnoreFailure, _command);
                }

                var context = Interlocked.Exchange(ref _cachedReadAsyncContext, null) ?? new ReadAsyncCallContext();

                Debug.Assert(context._reader == null && context._source == null && context._disposable == null, "cached ReadAsyncCallContext was not properly disposed");

                context.Set(this, source, registration);
                context._hasMoreData = more;
                context._hasReadRowToken = rowTokenRead;

                PrepareAsyncInvocation(useSnapshot: true);

                return InvokeAsyncCall(context);
            }
        }

        private static Task<bool> ReadAsyncExecute(Task task, object state)
        {
            var context = (ReadAsyncCallContext)state;
            SqlDataReader reader = context._reader;
            ref bool hasMoreData = ref context._hasMoreData;
            ref bool hasReadRowToken = ref context._hasReadRowToken;

            if (task != null)
            {
                reader.PrepareForAsyncContinuation();
            }

            if (hasReadRowToken || reader.TryReadInternal(true, out hasMoreData))
            {
                // If there are no more rows, or this is Sequential Access, then we are done
                if (!hasMoreData || (reader._commandBehavior & CommandBehavior.SequentialAccess) == CommandBehavior.SequentialAccess)
                {
                    // completed 
                    return hasMoreData ? ADP.TrueTask : ADP.FalseTask;
                }
                else
                {
                    // First time reading the row token - update the snapshot
                    if (!hasReadRowToken)
                    {
                        hasReadRowToken = true;
                        reader._snapshot = null;
                        reader.PrepareAsyncInvocation(useSnapshot: true);
                    }

                    // if non-sequentialaccess then read entire row before returning
                    if (reader.TryReadColumn(reader._metaData.Length - 1, true))
                    {
                        // completed 
                        return ADP.TrueTask;
                    }
                }
            }

            return reader.ExecuteAsyncCall(context);
        }

        private void SetCachedReadAsyncCallContext(ReadAsyncCallContext instance)
        {
            Interlocked.CompareExchange(ref _cachedReadAsyncContext, instance, null);
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/IsDBNullAsync/*' />
        override public Task<bool> IsDBNullAsync(int i, CancellationToken cancellationToken)
        {

            try
            {
                CheckHeaderIsReady(columnIndex: i, methodName: "IsDBNullAsync");
            }
            catch (Exception ex)
            {
                if (!ADP.IsCatchableExceptionType(ex))
                {
                    throw;
                }
                return ADP.CreatedTaskWithException<bool>(ex);
            }

            // Shortcut - if there are no issues and the data is already read, then just return the value
            if ((_sharedState._nextColumnHeaderToRead > i) && (!cancellationToken.IsCancellationRequested) && (_currentTask == null))
            {
                var data = _data;
                if (data != null)
                {
                    return data[i].IsNull ? ADP.TrueTask : ADP.FalseTask;
                }
                else
                {
                    // Reader was closed between the CheckHeaderIsReady and accessing _data - throw closed exception
                    return ADP.CreatedTaskWithException<bool>(ADP.ExceptionWithStackTrace(ADP.DataReaderClosed("IsDBNullAsync")));
                }
            }
            else
            {
                // Throw if there is any current task
                if (_currentTask != null)
                {
                    return ADP.CreatedTaskWithException<bool>(ADP.ExceptionWithStackTrace(ADP.AsyncOperationPending()));
                }

                // If user's token is canceled, return a canceled task
                if (cancellationToken.IsCancellationRequested)
                {
                    return ADP.CreatedTaskWithCancellation<bool>();
                }

                // Shortcut - if we have enough data, then run sync
                try
                {
                    if (WillHaveEnoughData(i, headerOnly: true))
                    {
#if DEBUG
                        try
                        {
                            _stateObj._shouldHaveEnoughData = true;
#endif
                        ReadColumnHeader(i);
                        return _data[i].IsNull ? ADP.TrueTask : ADP.FalseTask;
#if DEBUG
                        }
                        finally
                        {
                            _stateObj._shouldHaveEnoughData = false;
                        }
#endif
                    }
                }
                catch (Exception ex)
                {
                    if (!ADP.IsCatchableExceptionType(ex))
                    {
                        throw;
                    }
                    return ADP.CreatedTaskWithException<bool>(ex);
                }

                // Setup and check for pending task
                TaskCompletionSource<bool> source = new TaskCompletionSource<bool>();
                Task original = Interlocked.CompareExchange(ref _currentTask, source.Task, null);
                if (original != null)
                {
                    source.SetException(ADP.ExceptionWithStackTrace(ADP.AsyncOperationPending()));
                    return source.Task;
                }

                // Check if cancellation due to close is requested (this needs to be done after setting _currentTask)
                if (_cancelAsyncOnCloseToken.IsCancellationRequested)
                {
                    source.SetCanceled();
                    _currentTask = null;
                    return source.Task;
                }

                // Setup cancellations
                IDisposable registration = null;
                if (cancellationToken.CanBeCanceled)
                {
                    registration = cancellationToken.Register(SqlCommand.s_cancelIgnoreFailure, _command);
                }

                IsDBNullAsyncCallContext context = Interlocked.Exchange(ref _cachedIsDBNullContext, null) ?? new IsDBNullAsyncCallContext();

                Debug.Assert(context._reader == null && context._source == null && context._disposable == null, "cached ISDBNullAsync context not properly disposed");

                context.Set(this, source, registration);
                context._columnIndex = i;

                // Setup async
                PrepareAsyncInvocation(useSnapshot: true);

                return InvokeAsyncCall(context);
            }
        }

        private static Task<bool> IsDBNullAsyncExecute(Task task, object state)
        {
            IsDBNullAsyncCallContext context = (IsDBNullAsyncCallContext)state;
            SqlDataReader reader = context._reader;

            if (task != null)
            {
                reader.PrepareForAsyncContinuation();
            }

            if (reader.TryReadColumnHeader(context._columnIndex))
            {
                return reader._data[context._columnIndex].IsNull ? ADP.TrueTask : ADP.FalseTask;
            }
            else
            {
                return reader.ExecuteAsyncCall(context);
            }
        }

        private void SetCachedIDBNullAsyncCallContext(IsDBNullAsyncCallContext instance)
        {
            Interlocked.CompareExchange(ref _cachedIsDBNullContext, instance, null);
        }

        /// <include file='..\..\..\..\..\..\..\doc\snippets\Microsoft.Data.SqlClient\SqlDataReader.xml' path='docs/members[@name="SqlDataReader"]/GetFieldValueAsync/*' />
        override public Task<T> GetFieldValueAsync<T>(int i, CancellationToken cancellationToken)
        {
            try
            {
                CheckDataIsReady(columnIndex: i, methodName: "GetFieldValueAsync");

                // Shortcut - if there are no issues and the data is already read, then just return the value
                if ((!IsCommandBehavior(CommandBehavior.SequentialAccess)) && (_sharedState._nextColumnDataToRead > i) && (!cancellationToken.IsCancellationRequested) && (_currentTask == null))
                {
                    var data = _data;
                    var metaData = _metaData;
                    if ((data != null) && (metaData != null))
                    {
                        return Task.FromResult<T>(GetFieldValueFromSqlBufferInternal<T>(data[i], metaData[i], isAsync: false));
                    }
                    else
                    {
                        // Reader was closed between the CheckDataIsReady and accessing _data\_metaData - throw closed exception
                        return ADP.CreatedTaskWithException<T>(ADP.ExceptionWithStackTrace(ADP.DataReaderClosed("GetFieldValueAsync")));
                    }
                }
            }
            catch (Exception ex)
            {
                if (!ADP.IsCatchableExceptionType(ex))
                {
                    throw;
                }
                return ADP.CreatedTaskWithException<T>(ex);
            }

            // Throw if there is any current task
            if (_currentTask != null)
            {
                return ADP.CreatedTaskWithException<T>(ADP.ExceptionWithStackTrace(ADP.AsyncOperationPending()));
            }

            // If user's token is canceled, return a canceled task
            if (cancellationToken.IsCancellationRequested)
            {
                return ADP.CreatedTaskWithCancellation<T>();
            }

            // Shortcut - if we have enough data, then run sync
            try
            {
                if (WillHaveEnoughData(i))
                {
#if DEBUG
                    try
                    {
                        _stateObj._shouldHaveEnoughData = true;
#endif
                    return Task.FromResult(GetFieldValueInternal<T>(i, isAsync: true));
#if DEBUG
                    }
                    finally
                    {
                        _stateObj._shouldHaveEnoughData = false;
                    }
#endif
                }
            }
            catch (Exception ex)
            {
                if (!ADP.IsCatchableExceptionType(ex))
                {
                    throw;
                }
                return ADP.CreatedTaskWithException<T>(ex);
            }

            // Setup and check for pending task
            TaskCompletionSource<T> source = new TaskCompletionSource<T>();
            Task original = Interlocked.CompareExchange(ref _currentTask, source.Task, null);
            if (original != null)
            {
                source.SetException(ADP.ExceptionWithStackTrace(ADP.AsyncOperationPending()));
                return source.Task;
            }

            // Check if cancellation due to close is requested (this needs to be done after setting _currentTask)
            if (_cancelAsyncOnCloseToken.IsCancellationRequested)
            {
                source.SetCanceled();
                _currentTask = null;
                return source.Task;
            }

            // Setup cancellations
            IDisposable registration = null;
            if (cancellationToken.CanBeCanceled)
            {
                registration = cancellationToken.Register(SqlCommand.s_cancelIgnoreFailure, _command);
            }

            return InvokeAsyncCall(new GetFieldValueAsyncCallContext<T>(this, source, registration, i));
        }

        private static Task<T> GetFieldValueAsyncExecute<T>(Task task, object state)
        {
            GetFieldValueAsyncCallContext<T> context = (GetFieldValueAsyncCallContext<T>)state;
            SqlDataReader reader = context._reader;
            int columnIndex = context._columnIndex;
            if (task != null)
            {
                reader.PrepareForAsyncContinuation();
            }

            if (typeof(T) == typeof(Stream) || typeof(T) == typeof(TextReader) || typeof(T) == typeof(XmlReader))
            {
                if (reader.IsCommandBehavior(CommandBehavior.SequentialAccess) && reader._sharedState._dataReady)
                {
                    bool internalReadSuccess = false;
                    TdsParser.ReliabilitySection tdsReliabilitySection = new TdsParser.ReliabilitySection();
                    RuntimeHelpers.PrepareConstrainedRegions();
                    try
                    {
                        tdsReliabilitySection.Start();
                        internalReadSuccess = reader.TryReadColumnInternal(context._columnIndex, readHeaderOnly: true, forStreaming: false);
                    }
                    finally
                    {
                        tdsReliabilitySection.Stop();
                    }

                    if (internalReadSuccess)
                    {
                        return Task.FromResult<T>(reader.GetFieldValueFromSqlBufferInternal<T>(reader._data[columnIndex], reader._metaData[columnIndex], isAsync: true));
                    }
                }
            }

            if (reader.TryReadColumn(columnIndex, setTimeout: false))
            {
                return Task.FromResult<T>(reader.GetFieldValueFromSqlBufferInternal<T>(reader._data[columnIndex], reader._metaData[columnIndex], isAsync: false));
            }
            else
            {
                return reader.ExecuteAsyncCall(context);
            }
        }

#if DEBUG

        internal void CompletePendingReadWithSuccess(bool resetForcePendingReadsToWait)
        {
            var stateObj = _stateObj;
            if (stateObj != null)
            {
                stateObj.CompletePendingReadWithSuccess(resetForcePendingReadsToWait);
            }
        }

        internal void CompletePendingReadWithFailure(int errorCode, bool resetForcePendingReadsToWait)
        {
            var stateObj = _stateObj;
            if (stateObj != null)
            {
                stateObj.CompletePendingReadWithFailure(errorCode, resetForcePendingReadsToWait);
            }
        }

#endif

        class Snapshot
        {
            public bool _dataReady;
            public bool _haltRead;
            public bool _metaDataConsumed;
            public bool _browseModeInfoConsumed;
            public bool _hasRows;
            public ALTROWSTATUS _altRowStatus;
            public int _nextColumnDataToRead;
            public int _nextColumnHeaderToRead;
            public long _columnDataBytesRead;
            public long _columnDataBytesRemaining;

            public _SqlMetaDataSet _metadata;
            public _SqlMetaDataSetCollection _altMetaDataSetCollection;
            public MultiPartTableName[] _tableNames;

            public SqlSequentialStream _currentStream;
            public SqlSequentialTextReader _currentTextReader;
        }

        private abstract class AAsyncCallContext<T> : IDisposable
        {
            internal static readonly Action<Task<T>, object> s_completeCallback = SqlDataReader.CompleteAsyncCallCallback<T>;

            internal static readonly Func<Task, object, Task<T>> s_executeCallback = SqlDataReader.ExecuteAsyncCallCallback<T>;

            internal SqlDataReader _reader;
            internal TaskCompletionSource<T> _source;
            internal IDisposable _disposable;

            protected AAsyncCallContext()
            {
            }

            protected AAsyncCallContext(SqlDataReader reader, TaskCompletionSource<T> source, IDisposable disposable = null)
            {
                Set(reader, source, disposable);
            }

            internal void Set(SqlDataReader reader, TaskCompletionSource<T> source, IDisposable disposable = null)
            {
                this._reader = reader ?? throw new ArgumentNullException(nameof(reader));
                this._source = source ?? throw new ArgumentNullException(nameof(source));
                this._disposable = disposable;
            }

            internal void Clear()
            {
                _source = null;
                _reader = null;
                IDisposable copyDisposable = _disposable;
                _disposable = null;
                copyDisposable?.Dispose();
            }

            internal abstract Func<Task, object, Task<T>> Execute { get; }

            public virtual void Dispose()
            {
                Clear();
            }
        }

        private sealed class ReadAsyncCallContext : AAsyncCallContext<bool>
        {
            internal static readonly Func<Task, object, Task<bool>> s_execute = SqlDataReader.ReadAsyncExecute;

            internal bool _hasMoreData;
            internal bool _hasReadRowToken;

            internal ReadAsyncCallContext()
            {
            }

            internal override Func<Task, object, Task<bool>> Execute => s_execute;

            public override void Dispose()
            {
                SqlDataReader reader = this._reader;
                base.Dispose();
                reader.SetCachedReadAsyncCallContext(this);
            }
        }

        private sealed class IsDBNullAsyncCallContext : AAsyncCallContext<bool>
        {
            internal static readonly Func<Task, object, Task<bool>> s_execute = SqlDataReader.IsDBNullAsyncExecute;

            internal int _columnIndex;

            internal IsDBNullAsyncCallContext() { }

            internal override Func<Task, object, Task<bool>> Execute => s_execute;

            public override void Dispose()
            {
                SqlDataReader reader = this._reader;
                base.Dispose();
                reader.SetCachedIDBNullAsyncCallContext(this);
            }
        }

        private sealed class HasNextResultAsyncCallContext : AAsyncCallContext<bool>
        {
            private static readonly Func<Task, object, Task<bool>> s_execute = SqlDataReader.NextResultAsyncExecute;

            public HasNextResultAsyncCallContext(SqlDataReader reader, TaskCompletionSource<bool> source, IDisposable disposable)
                : base(reader, source, disposable)
            {
            }

            internal override Func<Task, object, Task<bool>> Execute => s_execute;
        }

        private sealed class GetBytesAsyncCallContext : AAsyncCallContext<int>
        {
            internal enum OperationMode
            {
                Seek = 0,
                Read = 1
            }

            private static readonly Func<Task, object, Task<int>> s_executeSeek = SqlDataReader.GetBytesAsyncSeekExecute;
            private static readonly Func<Task, object, Task<int>> s_executeRead = SqlDataReader.GetBytesAsyncReadExecute;

            internal int columnIndex;
            internal byte[] buffer;
            internal int index;
            internal int length;
            internal int timeout;
            internal CancellationToken cancellationToken;
            internal CancellationToken timeoutToken;
            internal int totalBytesRead;

            internal OperationMode mode;

            internal GetBytesAsyncCallContext(SqlDataReader reader)
            {
                this._reader = reader ?? throw new ArgumentNullException(nameof(reader));
            }

            internal override Func<Task, object, Task<int>> Execute => mode == OperationMode.Seek ? s_executeSeek : s_executeRead;

            public override void Dispose()
            {
                buffer = null;
                cancellationToken = default;
                timeoutToken = default;
                base.Dispose();
            }
        }

        private sealed class GetFieldValueAsyncCallContext<T> : AAsyncCallContext<T>
        {
            private static readonly Func<Task, object, Task<T>> s_execute = SqlDataReader.GetFieldValueAsyncExecute<T>;

            internal readonly int _columnIndex;

            internal GetFieldValueAsyncCallContext(SqlDataReader reader, TaskCompletionSource<T> source, IDisposable disposable, int columnIndex)
                : base(reader, source, disposable)
            {
                _columnIndex = columnIndex;
            }

            internal override Func<Task, object, Task<T>> Execute => s_execute;
        }

        private static Task<T> ExecuteAsyncCallCallback<T>(Task task, object state)
        {
            AAsyncCallContext<T> context = (AAsyncCallContext<T>)state;
            return context._reader.ExecuteAsyncCall(task, context);
        }

        private static void CompleteAsyncCallCallback<T>(Task<T> task, object state)
        {
            AAsyncCallContext<T> context = (AAsyncCallContext<T>)state;
            context._reader.CompleteAsyncCall<T>(task, context);
        }

        private Task<T> InvokeAsyncCall<T>(AAsyncCallContext<T> context)
        {
            TaskCompletionSource<T> source = context._source;
            try
            {
                Task<T> task;
                try
                {
                    task = context.Execute(null, context);
                }
                catch (Exception ex)
                {
                    task = ADP.CreatedTaskWithException<T>(ex);
                }

                if (task.IsCompleted)
                {
                    CompleteAsyncCall(task, context);
                }
                else
                {
                    task.ContinueWith(
                       continuationAction: AAsyncCallContext<T>.s_completeCallback,
                       state: context,
                       TaskScheduler.Default
                   );
                }
            }
            catch (AggregateException e)
            {
                source.TrySetException(e.InnerException);
            }
            catch (Exception e)
            {
                source.TrySetException(e);
            }

            // Fall through for exceptions\completing async
            return source.Task;
        }

        /// <summary>
        /// Begins an async call checking for cancellation and then setting up the callback for when data is available
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="context"></param>
        /// <returns></returns>
        private Task<T> ExecuteAsyncCall<T>(AAsyncCallContext<T> context)
        {
            // _networkPacketTaskSource could be null if the connection was closed
            // while an async invocation was outstanding.
            TaskCompletionSource<object> completionSource = _stateObj._networkPacketTaskSource;
            if (_cancelAsyncOnCloseToken.IsCancellationRequested || completionSource == null)
            {
                // Cancellation requested due to datareader being closed
                return Task.FromException<T>(ADP.ExceptionWithStackTrace(ADP.ClosedConnectionError()));
            }
            else
            {
                return completionSource.Task.ContinueWith(
                    continuationFunction: AAsyncCallContext<T>.s_executeCallback,
                    state: context,
                    TaskScheduler.Default
                ).Unwrap();
            }
        }

        /// <summary>
        /// When data has become available for an async call it is woken and this method is called.
        /// It will call the async execution func and if a Task is returned indicating more data
        /// is needed it will wait until it is called again when more is available
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="task"></param>
        /// <param name="context"></param>
        /// <returns></returns>
        private Task<T> ExecuteAsyncCall<T>(Task task, AAsyncCallContext<T> context)
        {
            // this function must be an instance function called from the static callback because otherwise a compiler error
            // is caused by accessing the _cancelAsyncOnCloseToken field of a MarchalByRefObject derived class
            if (task.IsFaulted)
            {
                // Somehow the network task faulted - return the exception
                return Task.FromException<T>(task.Exception.InnerException);
            }
            else if (!_cancelAsyncOnCloseToken.IsCancellationRequested)
            {
                TdsParserStateObject stateObj = _stateObj;
                if (stateObj != null)
                {
                    // protect continuations against concurrent
                    // close and cancel
                    lock (stateObj)
                    {
                        if (_stateObj != null)
                        { // reader not closed while we waited for the lock
                            if (task.IsCanceled)
                            {
                                if (_parser != null)
                                {
                                    _parser.State = TdsParserState.Broken; // We failed to respond to attention, we have to quit!
                                    _parser.Connection.BreakConnection();
                                    _parser.ThrowExceptionAndWarning(_stateObj);
                                }
                            }
                            else
                            {
                                if (!IsClosed)
                                {
                                    try
                                    {
                                        return context.Execute(task, context);
                                    }
                                    catch (Exception)
                                    {
                                        CleanupAfterAsyncInvocation();
                                        throw;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            // if stateObj is null, or we closed the connection or the connection was already closed,
            // then mark this operation as cancelled.
            return Task.FromException<T>(ADP.ExceptionWithStackTrace(ADP.ClosedConnectionError()));
        }

        /// <summary>
        /// When data has been successfully processed for an async call the async func will call this
        /// function to set the result into the task and cleanup the async state ready for another call
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="task"></param>
        /// <param name="context"></param>
        private void CompleteAsyncCall<T>(Task<T> task, AAsyncCallContext<T> context)
        {
            TaskCompletionSource<T> source = context._source;
            context.Dispose();

            // If something has forced us to switch to SyncOverAsync mode while in an async task then we need to guarantee that we do the cleanup
            // This avoids us replaying non-replayable data (such as DONE or ENV_CHANGE tokens)
            var stateObj = _stateObj;
            bool ignoreCloseToken = (stateObj != null) && (stateObj._syncOverAsync);
            CleanupAfterAsyncInvocation(ignoreCloseToken);

            Task current = Interlocked.CompareExchange(ref _currentTask, null, source.Task);
            Debug.Assert(current == source.Task, "Should not be able to change the _currentTask while an asynchronous operation is pending");

            if (task.IsFaulted)
            {
                Exception e = task.Exception.InnerException;
                source.TrySetException(e);
            }
            else if (task.IsCanceled)
            {
                source.TrySetCanceled();
            }
            else
            {
                source.TrySetResult(task.Result);
            }
        }

        private void PrepareAsyncInvocation(bool useSnapshot)
        {
            // if there is already a snapshot, then the previous async command
            // completed with exception or cancellation.  We need to continue
            // with the old snapshot.
            if (useSnapshot)
            {
                Debug.Assert(!_stateObj._asyncReadWithoutSnapshot, "Can't prepare async invocation with snapshot if doing async without snapshots");

                if (_snapshot == null)
                {
                    _snapshot = new Snapshot
                    {
                        _dataReady = _sharedState._dataReady,
                        _haltRead = _haltRead,
                        _metaDataConsumed = _metaDataConsumed,
                        _browseModeInfoConsumed = _browseModeInfoConsumed,
                        _hasRows = _hasRows,
                        _altRowStatus = _altRowStatus,
                        _nextColumnDataToRead = _sharedState._nextColumnDataToRead,
                        _nextColumnHeaderToRead = _sharedState._nextColumnHeaderToRead,
                        _columnDataBytesRead = _columnDataBytesRead,
                        _columnDataBytesRemaining = _sharedState._columnDataBytesRemaining,

                        // _metadata and _altaMetaDataSetCollection must be Cloned
                        // before they are updated
                        _metadata = _metaData,
                        _altMetaDataSetCollection = _altMetaDataSetCollection,
                        _tableNames = _tableNames,

                        _currentStream = _currentStream,
                        _currentTextReader = _currentTextReader,
                    };

                    _stateObj.SetSnapshot();
                }
            }
            else
            {
                Debug.Assert(_snapshot == null, "Can prepare async invocation without snapshot if there is currently a snapshot");
                _stateObj._asyncReadWithoutSnapshot = true;
            }

            _stateObj._syncOverAsync = false;
            _stateObj._executionContext = ExecutionContext.Capture();
        }

        private void CleanupAfterAsyncInvocation(bool ignoreCloseToken = false)
        {
            var stateObj = _stateObj;
            if (stateObj != null)
            {
                // If close requested cancellation and we have a snapshot, then it will deal with cleaning up
                // NOTE: There are some cases where we wish to ignore the close token, such as when we've read some data that is not replayable (e.g. DONE or ENV_CHANGE token)
                if ((ignoreCloseToken) || (!_cancelAsyncOnCloseToken.IsCancellationRequested) || (stateObj._asyncReadWithoutSnapshot))
                {
                    // Prevent race condition between the DataReader being closed (e.g. when another MARS thread has an error)
                    lock (stateObj)
                    {
                        if (_stateObj != null)
                        { // reader not closed while we waited for the lock
                            CleanupAfterAsyncInvocationInternal(_stateObj);
                            Debug.Assert(_snapshot == null && !_stateObj._asyncReadWithoutSnapshot, "Snapshot not null or async without snapshot still enabled after cleaning async state");
                        }
                    }
                }
            }
        }

        // This function is called directly if calling function already closed the reader, so _stateObj is null,
        // in other cases parameterless version should be called
        private void CleanupAfterAsyncInvocationInternal(TdsParserStateObject stateObj, bool resetNetworkPacketTaskSource = true)
        {
            if (resetNetworkPacketTaskSource)
            {
                stateObj._networkPacketTaskSource = null;
            }
            stateObj.ResetSnapshot();
            stateObj._syncOverAsync = true;
            stateObj._executionContext = null;
            stateObj._asyncReadWithoutSnapshot = false;
#if DEBUG
            stateObj._permitReplayStackTraceToDiffer = false;
#endif

            // We are setting this to null inside the if-statement because stateObj==null means that the reader hasn't been initialized or has been closed (either way _snapshot should already be null)
            _snapshot = null;
        }

        private void PrepareForAsyncContinuation()
        {
            Debug.Assert(((_snapshot != null) || (_stateObj._asyncReadWithoutSnapshot)), "Can not prepare for an async continuation if no async if setup");
            if (_snapshot != null)
            {
                _sharedState._dataReady = _snapshot._dataReady;
                _haltRead = _snapshot._haltRead;
                _metaDataConsumed = _snapshot._metaDataConsumed;
                _browseModeInfoConsumed = _snapshot._browseModeInfoConsumed;
                _hasRows = _snapshot._hasRows;
                _altRowStatus = _snapshot._altRowStatus;
                _sharedState._nextColumnDataToRead = _snapshot._nextColumnDataToRead;
                _sharedState._nextColumnHeaderToRead = _snapshot._nextColumnHeaderToRead;
                _columnDataBytesRead = _snapshot._columnDataBytesRead;
                _sharedState._columnDataBytesRemaining = _snapshot._columnDataBytesRemaining;

                _metaData = _snapshot._metadata;
                _altMetaDataSetCollection = _snapshot._altMetaDataSetCollection;
                _tableNames = _snapshot._tableNames;

                _currentStream = _snapshot._currentStream;
                _currentTextReader = _snapshot._currentTextReader;

                _stateObj.PrepareReplaySnapshot();
            }

            _stateObj._executionContext = ExecutionContext.Capture();
        }

        private void SwitchToAsyncWithoutSnapshot()
        {
            Debug.Assert(_snapshot != null, "Should currently have a snapshot");
            Debug.Assert(_stateObj != null && !_stateObj._asyncReadWithoutSnapshot, "Already in async without snapshot");

            _snapshot = null;
            _stateObj.ResetSnapshot();
            _stateObj._asyncReadWithoutSnapshot = true;
        }

    }// SqlDataReader
}// namespace
