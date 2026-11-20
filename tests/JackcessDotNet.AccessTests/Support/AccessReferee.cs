using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using JackcessDotNet;
using Xunit;

namespace JackcessDotNet.AccessTests.Support
{
    /// <summary>
    /// Microsoft Access's own engine as the referee of the managed engine's rows, in both directions: what the engine
    /// reads of a table must be what ACE reads of it, and a table the engine has changed must be read by ACE - by
    /// scan, through its primary key, and after DAO has compacted the file - as the rows the engine was given.
    /// Values are compared in one written form, so an Int32 from ACE and an int from the engine are the same thing
    /// and a Decimal is the same number whatever its scale.
    /// </summary>
    internal static class AccessReferee
    {
        /// <summary>A row as a test holds it: column name to value, null for a null.</summary>
        public sealed class Values : Dictionary<string, object>
        {
            public Values() : base(StringComparer.OrdinalIgnoreCase) { }
        }

        /// <summary>A row from name, value, name, value...</summary>
        public static Values V(params object[] namesAndValues)
        {
            var values = new Values();
            for (var i = 0; i < namesAndValues.Length; i += 2)
                values[(string)namesAndValues[i]] = namesAndValues[i + 1];
            return values;
        }

        public static void Execute(string path, params string[] statements)
        {
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                foreach (var sql in statements)
                    using (var command = new OleDbCommand(sql, connection))
                        command.ExecuteNonQuery();
            }
        }

        public static object Scalar(string path, string sql)
        {
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            using (var command = new OleDbCommand(sql, connection))
            {
                connection.Open();
                return command.ExecuteScalar();
            }
        }

        /// <summary>Every row ACE reads with <paramref name="sql"/>, a null for a null.</summary>
        public static List<Values> AceRows(string path, string sql)
        {
            var rows = new List<Values>();
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            using (var command = new OleDbCommand(sql, connection))
            {
                connection.Open();
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                    {
                        var row = new Values();
                        for (var i = 0; i < reader.FieldCount; i++)
                            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        rows.Add(row);
                    }
            }
            return rows;
        }

        public static List<Values> AceTable(string path, string table)
        {
            return AceRows(path, "SELECT * FROM [" + table + "]");
        }

        /// <summary>ACE inserts <paramref name="row"/>, its values given as parameters.</summary>
        public static void AceInsert(string path, string table, Values row)
        {
            var columns = row.Keys.ToList();
            var sql = "INSERT INTO [" + table + "] (" + string.Join(", ", columns.Select(c => "[" + c + "]")) + ") VALUES ("
                      + string.Join(", ", columns.Select(c => "?")) + ")";
            AceWrite(path, sql, columns.Select(c => row[c]));
        }

        /// <summary>ACE sets <paramref name="changes"/> in the row whose <paramref name="key"/> column is <paramref name="id"/>.</summary>
        public static void AceUpdate(string path, string table, string key, object id, Values changes)
        {
            var columns = changes.Keys.ToList();
            var sql = "UPDATE [" + table + "] SET " + string.Join(", ", columns.Select(c => "[" + c + "] = ?")) + " WHERE [" + key + "] = ?";
            AceWrite(path, sql, columns.Select(c => changes[c]).Concat(new[] { id }));
        }

        private static void AceWrite(string path, string sql, IEnumerable<object> parameters)
        {
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            using (var command = new OleDbCommand(sql, connection))
            {
                connection.Open();
                var n = 0;
                foreach (var value in parameters)
                {
                    OleDbParameter parameter;
                    if (value is DateTime) parameter = new OleDbParameter("p" + n, OleDbType.Date) { Value = value };
                    else if (value is byte[]) parameter = new OleDbParameter("p" + n, OleDbType.LongVarBinary) { Value = value };
                    else if (value is string) parameter = new OleDbParameter("p" + n, OleDbType.LongVarWChar) { Value = value };
                    else if (value is decimal) parameter = new OleDbParameter("p" + n, OleDbType.Decimal) { Value = value, Precision = 28, Scale = Scale((decimal)value) };
                    else parameter = new OleDbParameter("p" + n, value ?? DBNull.Value);
                    command.Parameters.Add(parameter);
                    n++;
                }
                Assert.Equal(1, command.ExecuteNonQuery());
            }
        }

        private static byte Scale(decimal value)
        {
            return (byte)((decimal.GetBits(value)[3] >> 16) & 0xFF);
        }

        /// <summary>Every row the engine reads of <paramref name="table"/>.</summary>
        public static List<Values> EngineTable(string path, string table)
        {
            using (var db = Database.Open(path))
            {
                var opened = db.GetTable(table);
                return opened.Rows.Select(row =>
                {
                    var values = new Values();
                    foreach (var column in opened.Columns)
                    {
                        object value;
                        values[column.Name] = row.TryGetValue(column.Name, out value) ? value : null;
                    }
                    return values;
                }).ToList();
            }
        }

        public static Row EngineRow(Values values)
        {
            var row = new Row();
            foreach (var pair in values)
                row[pair.Key] = pair.Value;
            return row;
        }

        /// <summary>One value in the form every reader's is compared in.</summary>
        public static string Written(object value)
        {
            if (value == null || value is DBNull) return "null";
            if (value is bool) return (bool)value ? "yes" : "no";
            if (value is byte || value is short || value is int || value is long || value is sbyte || value is ushort || value is uint)
                return Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture);
            if (value is float) return "single " + ((float)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is double) return "double " + ((double)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is decimal) return "decimal " + ((decimal)value).ToString("0.############################", CultureInfo.InvariantCulture);
            if (value is DateTime) return ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            if (value is Guid) return ((Guid)value).ToString("D");
            var text = value as string;
            if (text != null)
                return text.Length <= 40 ? "'" + text + "'" : "text of " + text.Length + " #" + Hash(System.Text.Encoding.Unicode.GetBytes(text));
            var bytes = value as byte[];
            if (bytes != null)
                return bytes.Length <= 20 ? "0x" + BitConverter.ToString(bytes).Replace("-", "") : "bytes of " + bytes.Length + " #" + Hash(bytes);
            return value.GetType().Name + " " + Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes), 0, 6).Replace("-", "");
        }

        /// <summary>Rows in the form they are compared in: each its columns in name order, the rows sorted.</summary>
        public static List<string> Written(IEnumerable<Values> rows)
        {
            return rows
                .Select(row => string.Join(", ", row.Keys
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .Select(k => k + "=" + Written(row[k]))))
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToList();
        }

        public static void AssertSame(List<string> expected, List<string> actual, string what)
        {
            var missing = expected.Except(actual).Take(3).ToList();
            var extra = actual.Except(expected).Take(3).ToList();
            Assert.True(expected.SequenceEqual(actual),
                what + ": " + actual.Count + " rows, expected " + expected.Count
                + (missing.Count > 0 ? "\n  expected and not there: " + string.Join("\n                          ", missing) : "")
                + (extra.Count > 0 ? "\n  there and not expected: " + string.Join("\n                          ", extra) : ""));
        }

        /// <summary>The engine reads <paramref name="table"/> as ACE reads it. Returns the rows, as ACE gave them.</summary>
        public static List<Values> AssertEngineReadsWhatAceReads(string path, string table)
        {
            var ace = AceTable(path, table);
            AssertSame(Written(ace), Written(EngineTable(path, table)), "the engine's reading of " + table + " against ACE's");
            return ace;
        }

        /// <summary>
        /// <paramref name="table"/> holds exactly <paramref name="expected"/>, for ACE and for the engine: read by
        /// scan, each row found through the index on <paramref name="key"/> (a Long column) and by a scan ACE cannot
        /// use the index for, and all of it again in a copy DAO has compacted.
        /// </summary>
        public static void AssertTableIs(string path, string table, string key, IEnumerable<Values> expected, string when)
        {
            var rows = expected.ToList();
            var written = Written(rows);
            List<Values> aceRows;
            try { aceRows = AceTable(path, table); }
            catch (Exception e) when (e is InvalidOperationException || e is OleDbException)
            {
                // Which values ACE cannot read, so the failure names them.
                var unreadable = new List<string>();
                foreach (var row in rows)
                    foreach (var column in row.Keys)
                        try { AceRows(path, "SELECT [" + column + "] FROM [" + table + "] WHERE [" + key + "] = " + Convert.ToString(row[key], CultureInfo.InvariantCulture)); }
                        catch (Exception) { unreadable.Add(column + " of " + key + " " + row[key] + " (expected " + Written(row[column]) + ")"); }
                throw new Xunit.Sdk.XunitException("ACE could not read " + table + " " + when + ": " + e.Message
                    + "\n  it cannot read: " + string.Join("; ", unreadable.Take(12)));
            }
            AssertSame(written, Written(aceRows), table + " as ACE reads it " + when);
            AssertSame(written, Written(EngineTable(path, table)), table + " as the engine reads it " + when);

            Assert.Equal(rows.Count, Convert.ToInt32(Scalar(path, "SELECT COUNT(*) FROM [" + table + "]")));
            foreach (var row in rows)
                foreach (var where in new[] { "[" + key + "] = ", "[" + key + "] + 0 = " })
                {
                    var found = AceRows(path, "SELECT * FROM [" + table + "] WHERE " + where + Convert.ToString(row[key], CultureInfo.InvariantCulture));
                    AssertSame(Written(new[] { row }), Written(found), table + " WHERE " + where + row[key] + " " + when);
                }

            var compacted = Compact(path);
            try { AssertSame(written, Written(AceTable(compacted, table)), table + " after DAO compacted the file, " + when); }
            finally { File.Delete(compacted); }
        }

        /// <summary>One table, the rows it should hold, and the two engines changing it in turn.</summary>
        public sealed class Subject
        {
            private readonly string _path;
            private readonly string _table;
            private readonly string _key;
            private readonly Dictionary<long, Values> _rows = new Dictionary<long, Values>();

            /// <summary>Starts from what ACE reads, which the engine must read the same.</summary>
            public Subject(string path, string table, string key)
            {
                _path = path;
                _table = table;
                _key = key;
                foreach (var row in AssertEngineReadsWhatAceReads(path, table))
                    _rows.Add(Convert.ToInt64(row[key]), row);
            }

            public List<long> Ids { get { return _rows.Keys.OrderBy(k => k).ToList(); } }
            public Values this[long id] { get { return _rows[id]; } }

            private void Engine(Action<Table> change)
            {
                using (var db = Database.Open(_path))
                    change(db.GetTable(_table));
            }

            private Values Whole(Values given)
            {
                // Every column of the table, a null for one not given.
                var whole = new Values();
                foreach (var column in _rows.Count > 0 ? _rows.Values.First().Keys.ToList() : given.Keys.ToList())
                    whole[column] = null;
                foreach (var pair in given)
                    whole[pair.Key] = pair.Value;
                return whole;
            }

            public void EngineInserts(params Values[] rows)
            {
                Engine(table =>
                {
                    foreach (var row in rows)
                    {
                        table.Insert(EngineRow(row));
                        _rows.Add(Convert.ToInt64(row[_key]), Whole(row));
                    }
                });
            }

            public void EngineUpdates(long id, Values changes)
            {
                Engine(table => table.UpdateByPrimaryKey((int)id, EngineRow(changes)));
                foreach (var pair in changes)
                    _rows[id][pair.Key] = pair.Value;
            }

            public void EngineDeletes(params long[] ids)
            {
                Engine(table =>
                {
                    foreach (var id in ids)
                    {
                        table.DeleteRow(_key, (int)id);
                        _rows.Remove(id);
                    }
                });
            }

            public void AceInserts(params Values[] rows)
            {
                foreach (var row in rows)
                {
                    AceInsert(_path, _table, row);
                    _rows.Add(Convert.ToInt64(row[_key]), Whole(row));
                }
            }

            public void AceUpdates(long id, Values changes)
            {
                AceUpdate(_path, _table, _key, (int)id, changes);
                foreach (var pair in changes)
                    _rows[id][pair.Key] = pair.Value;
            }

            public void AceDeletes(params long[] ids)
            {
                foreach (var id in ids)
                {
                    Execute(_path, "DELETE FROM [" + _table + "] WHERE [" + _key + "] = " + id);
                    _rows.Remove(id);
                }
            }

            /// <summary>ACE and the engine both read the table as the rows it should hold; DAO compacts it.</summary>
            public void Check(string when)
            {
                AssertTableIs(_path, _table, _key, _rows.Values, when);
            }
        }

        /// <summary>DAO compacts <paramref name="path"/> into a new file beside it and returns that file's path.</summary>
        public static string Compact(string path)
        {
            var compacted = Path.Combine(Path.GetDirectoryName(path), Guid.NewGuid().ToString("N") + "-compacted" + Path.GetExtension(path));
            var dbeType = Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true);
            var dbe = Activator.CreateInstance(dbeType);
            try { dbeType.InvokeMember("CompactDatabase", BindingFlags.InvokeMethod, null, dbe, new object[] { path, compacted }); }
            catch (TargetInvocationException e) { throw new Xunit.Sdk.XunitException("DAO could not compact the file: " + e.InnerException.Message); }
            finally { Dao.Release(dbe); }
            return compacted;
        }

        /// <summary>SQL's name for a Large Number column (Access 2016 and later).</summary>
        public const string LargeNumber = "BIGINT";

        /// <summary>SQL's name for a Date/Time Extended column (Access 2019 and later).</summary>
        public const string DateTimeExtended = "DATETIME2";

        private static readonly Dictionary<string, string> WhyAceLacks = new Dictionary<string, string>();

        /// <summary>
        /// Skips the test, saying why, when ACE on this machine has no column of <paramref name="sqlType"/>: the
        /// Access Database Engine 2016 redistributable - what CI installs - has no Date/Time Extended, and does not
        /// open a file that has one. Whether it has the type is asked of ACE itself, by having it make such a
        /// column in a database of its own, and not taken from what it says of the file under test: "requires a
        /// newer version" or "unrecognized database format" of a file the engine wrote, from an ACE that makes the
        /// column, is the engine's fault and fails the test.
        /// </summary>
        public static void SkipUnlessAceHas(string sqlType)
        {
            string why;
            if (!WhyAceLacks.TryGetValue(sqlType, out why))
            {
                var probe = Path.Combine(Path.GetTempPath(), "ace-has-" + Guid.NewGuid().ToString("N") + ".accdb");
                try
                {
                    DaoCreate(probe, 256);
                    Execute(probe, "CREATE TABLE Probe (V " + sqlType + ")");
                    why = null;
                }
                catch (Exception e) when (e is OleDbException || e is TargetInvocationException)
                {
                    why = (e.InnerException ?? e).Message;
                }
                finally { File.Delete(probe); }
                WhyAceLacks[sqlType] = why;
            }
            if (why != null)
                Assert.Skip("ACE on this machine makes no " + sqlType + " column, so there is no referee here for a file that has one: " + why);
        }

        private static bool _askedAbout2010;
        private static string _whyNo2010;

        /// <summary>
        /// Skips the test, saying why, when ACE in this process does not open a database in the Access 2010 format,
        /// which is the format of the databases that sort in General: 32-bit ACE 12 is
        /// Access 2007's, and says "Unrecognized database format" of one. Whether it does is asked of ACE itself, by
        /// having it make a table in an empty database of that format - the engine's, which is a copy of one DAO
        /// made. (Asked to make one itself, 32-bit DAO 12 makes a database it can open, of its own format.)
        /// </summary>
        public static void SkipUnlessAceOpensAccess2010()
        {
            if (!_askedAbout2010)
            {
                var probe = Path.Combine(Path.GetTempPath(), "ace-opens-2010-" + Guid.NewGuid().ToString("N") + ".accdb");
                try
                {
                    Database.Create(probe, FileFormat.Access2010).Dispose();
                    Execute(probe, "CREATE TABLE Probe (V LONG)");
                    _whyNo2010 = null;
                }
                catch (Exception e) when (e is OleDbException || e is TargetInvocationException || e is System.Runtime.InteropServices.COMException)
                {
                    _whyNo2010 = (e.InnerException ?? e).Message;
                }
                finally { File.Delete(probe); }
                _askedAbout2010 = true;
            }
            if (_whyNo2010 != null)
                Assert.Skip("ACE in this process does not open an Access 2010 .accdb, the format of the database that sorts in General, "
                    + "so there is no referee here for this case: " + _whyNo2010);
        }

        /// <summary>
        /// A Date/Time Extended value as it is compared: to its last tick. (A Date/Time is compared to the
        /// millisecond, which is as far as the engine reads one.)
        /// </summary>
        public static string ToTheTick(DateTime value)
        {
            return "extended " + value.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The Date/Time Extended value DAO hands out as text, month first whatever the machine's settings:
        /// "01/02/2020 03:04:05.1234567". ACE's OLE DB provider hands out no such value at all - reading one fails,
        /// or gives a date that is not the row's - so DAO is the reader of these columns.
        /// </summary>
        public static DateTime ExtendedDate(string daoText)
        {
            return DateTime.ParseExact(daoText, "MM/dd/yyyy HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        }

        private const int DaoDateTimeExtended = 26;

        /// <summary>
        /// Every row DAO reads with <paramref name="source"/> - a query, or with <paramref name="index"/> a table's
        /// name, read in that index's order: DAO then walks the index's own pages. A null for a null, and a
        /// Date/Time Extended value as a <see cref="DateTime"/>.
        /// </summary>
        public static List<Values> DaoRows(string path, string source, string index = null)
        {
            var rows = new List<Values>();
            dynamic dbe = Activator.CreateInstance(Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true));
            try
            {
                dynamic db = dbe.OpenDatabase(path, false, true);
                try
                {
                    dynamic recordset = index == null ? db.OpenRecordset(source, 4 /* dbOpenSnapshot */) : db.OpenRecordset(source, 1 /* dbOpenTable */);
                    try
                    {
                        if (index != null) recordset.Index = index;
                        int fields = recordset.Fields.Count;
                        while (!recordset.EOF)
                        {
                            var row = new Values();
                            for (var i = 0; i < fields; i++)
                            {
                                dynamic field = recordset.Fields[i];
                                object value = field.Value;
                                if (value is DBNull) value = null;
                                if (value is string && (int)field.Type == DaoDateTimeExtended) value = ExtendedDate((string)value);
                                row[(string)field.Name] = value;
                            }
                            rows.Add(row);
                            recordset.MoveNext();
                        }
                    }
                    finally { recordset.Close(); }
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
            return rows;
        }

        /// <summary>
        /// A new, empty database made by DAO in the given format: 64 for Access 2000-2003 (.mdb), 128 for Access 2007
        /// (.accdb) and 256 for Access 2010 and later (.accdb).
        /// </summary>
        public static void DaoCreate(string path, int version)
        {
            var dbeType = Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true);
            var dbe = Activator.CreateInstance(dbeType);
            try
            {
                var db = dbeType.InvokeMember("CreateDatabase", BindingFlags.InvokeMethod, null, dbe, new object[] { path, ";LANGID=0x0409;CP=1252;COUNTRY=0", version });
                db.GetType().InvokeMember("Close", BindingFlags.InvokeMethod, null, db, null);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(db);
            }
            finally { Dao.Release(dbe); }
        }
    }
}
