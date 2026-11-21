using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;
using static JackcessDotNet.AccessTests.Support.AccessReferee;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// The files Access itself wrote, in the engine's corpus (tests/corpus, the Java Jackcess
    /// project's test databases): every table of every file, the system tables among them, is read by the engine and
    /// by a second reader that is not the engine - ACE through OLE DB, or DAO where ACE's provider will not read a
    /// table (it refuses most system tables) - and the rows must be the same. The files hold what Access leaves in
    /// real files and a test cannot easily make to order: tables with columns deleted, rows moved to other pages,
    /// numbers kept among the variable-length values (Access's own system tables keep them so) and text among the
    /// fixed-length ones, in the Access 2000, 2003, 2007, 2010 and 2019 formats. The Access 97 files are read in a
    /// 32-bit process only: 64-bit ACE does not open them, 32-bit ACE 12 does. The files
    /// are copied before they are opened: the corpus is never written to.
    /// </summary>
    public class CorpusInAccessTests : IDisposable
    {
        private TempDb _tmp;
        private readonly ITestOutputHelper _output;

        public CorpusInAccessTests(ITestOutputHelper output) { _tmp = new TempDb(); _output = output; }
        public void Dispose() { _tmp.Dispose(); }

        private static string CorpusRoot()
        {
            var corpus = Path.GetFullPath(typeof(CorpusInAccessTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "EngineCorpusDir").Value);
            if (Directory.Exists(corpus)) return corpus;
            throw new DirectoryNotFoundException("The engine's corpus (tests/corpus) was not found at " + corpus);
        }

        // The files left out, and why.
        private static readonly string[] LeftOut =
        {
            "unsupportedFields",   // neither ACE nor DAO opens it ("Unrecognized database format")
        };

        public static IEnumerable<object[]> Files()
        {
            var root = CorpusRoot();
            return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".accdb", StringComparison.OrdinalIgnoreCase))
                .Where(f => !LeftOut.Any(name => Path.GetFileName(f).StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                // Access 97: only a 32-bit process has a second reader for it.
                .Where(f => !Environment.Is64BitProcess || !f.Contains("V1997"))
                .Select(f => new object[] { f.Substring(root.Length + 1).Replace('\\', '/') })
                // And a file an earlier release of the engine itself wrote (markmario/jackcess-dotnet 696ca52), kept
                // among the engine's test assets: what that release wrote, this one reads as ACE does.
                .Concat(new[] { new object[] { EarlierEngineFile } })
                // And one Access made with Large Number and Date/Time Extended columns, their values at the
                // ends of their ranges: the corpus has one file of extended dates and none of large numbers.
                .Concat(new[] { new object[] { LargeNumbersFile } })
                .OrderBy(f => (string)f[0], StringComparer.Ordinal);
        }

        private const string EarlierEngineFile = "../JackcessDotNet.Tests/Assets/engine-696ca52-long-keys.mdb";
        private const string LargeNumbersFile = "../JackcessDotNet.Tests/Assets/ace-large-numbers-extended-dates.accdb";

        // Columns the engine gives no value for, of any row: attachments and multi-value fields
        // (#24), types later than the engine knows, and calculated columns, which it
        // reads as null (#25).
        private static bool Compared(string table, Column column)
        {
            // An attachment's bytes, in the hidden table that holds a complex column's values: ACE hands back the
            // file, the engine what is stored, which is the file in a wrapper (#24).
            if (table.StartsWith("f_", StringComparison.OrdinalIgnoreCase) && column.Name == "FileData") return false;
            return column.DataType != DataType.Complex && Enum.IsDefined(typeof(DataType), column.DataType) && !column.IsCalculated;
        }

        /// <summary>The rows of <paramref name="table"/> as DAO reads them: what ACE's OLE DB provider refuses, DAO often reads.</summary>
        private static List<Values> DaoRows(string path, string table, IList<Column> columns)
        {
            var rows = new List<Values>();
            dynamic dbe = Dao.NewEngine();
            try
            {
                dynamic db = dbe.OpenDatabase(path, false, false);
                try
                {
                    try
                    {
                        dynamic document = db.Containers["Tables"].Documents[table];
                        document.UserName = "Admin";
                        document.Permissions = 1048575;   // dbSecFullAccess
                    }
                    catch (Exception) { }
                    dynamic recordset = db.OpenRecordset("SELECT " + string.Join(", ", columns.Select(c => "[" + c.Name + "]")) + " FROM [" + table + "]", 4 /* dbOpenSnapshot */);
                    try
                    {
                        while (!recordset.EOF)
                        {
                            var row = new Values();
                            for (var i = 0; i < columns.Count; i++)
                            {
                                object value = recordset.Fields[i].Value;
                                if (value is DBNull) value = null;
                                // DAO gives a Replication ID as text: {guid {...}}.
                                var text = value as string;
                                if (text != null && columns[i].DataType == DataType.Guid)
                                    value = Guid.Parse(text.Replace("{guid ", "").TrimEnd('}') + "}");
                                // And a Date/Time Extended as text, which is compared to its last tick.
                                if (text != null && columns[i].DataType == DataType.DateTimeExtended)
                                    value = ToTheTick(ExtendedDate(text));
                                row[columns[i].Name] = value;
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
        /// The tables DAO lists in the file at <paramref name="path"/> - those the file itself holds, not its links
        /// to others - each with the fields Access shows an expression for: its calculated fields. Null when DAO
        /// does not open the file.
        /// </summary>
        private static Dictionary<string, List<string>> DaoCatalog(string path, out bool knowsCalculatedFields)
        {
            var tables = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            knowsCalculatedFields = false;
            dynamic dbe = Dao.NewEngine();
            try
            {
                // Calculated fields came with Access 2010, DAO 14: 32-bit ACE 12 is Access 2007's and shows none.
                double version;
                knowsCalculatedFields = double.TryParse((string)dbe.Version, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out version) && version >= 14;
                dynamic db;
                try { db = dbe.OpenDatabase(path, false, true); }
                catch (Exception) { return null; }
                try
                {
                    foreach (dynamic definition in db.TableDefs)
                    {
                        // A link to a table in another file or behind ODBC: the engine lists the tables a file holds.
                        string connect = null;
                        try { connect = definition.Connect as string; } catch (Exception) { }
                        if (!string.IsNullOrEmpty(connect)) continue;

                        var calculated = new List<string>();
                        try
                        {
                            foreach (dynamic field in definition.Fields)
                            {
                                string expression = null;
                                try { expression = field.Properties["Expression"].Value as string; }
                                catch (Exception) { }   // no such property: not a calculated field
                                if (!string.IsNullOrEmpty(expression)) calculated.Add((string)field.Name);
                            }
                        }
                        catch (Exception) { }   // a link to a file that is not there has no fields to ask
                        tables[(string)definition.Name] = calculated;
                    }
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
            return tables;
        }

        [Theory]
        [MemberData(nameof(Files))]
        public void the_engine_names_the_tables_and_the_calculated_fields_dao_does(string file)
        {
            var path = Path.Combine(_tmp.Dir, Path.GetFileName(file));
            File.Copy(Path.Combine(CorpusRoot(), file.Replace('/', Path.DirectorySeparatorChar)), path);
            var report = Environment.GetEnvironmentVariable("CORPUS_REFEREE_REPORT");

            bool knowsCalculatedFields;
            var dao = DaoCatalog(path, out knowsCalculatedFields);
            if (dao == null) Assert.Skip(file + ": DAO does not open the file in this process.");

            var engine = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using (var db = Database.Open(path))
                foreach (var name in db.GetTableNames().Concat(db.GetSystemTableNames()))
                    engine[name] = db.GetTable(name).Columns.Where(c => c.IsCalculated).Select(c => c.Name).ToList();

            // Every table DAO lists, the engine lists; and the engine lists no other, but for the tables Access
            // keeps from DAO's list: the hidden ones that hold complex columns' values and their types' templates,
            // and the SharePoint cache's.
            Assert.Equal(new string[0], dao.Keys.Where(n => !engine.ContainsKey(n)).OrderBy(n => n).ToArray());
            Assert.Equal(new string[0], engine.Keys.Where(n => !dao.ContainsKey(n) && WhyHiddenFromDao(n) == null).OrderBy(n => n).ToArray());

            // The engine takes a field for calculated by a flag in the table's definition (#25); DAO by the
            // expression Access keeps for it. They must name the same fields, no more and no fewer.
            var calculated = 0;
            foreach (var table in dao)
            {
                if (knowsCalculatedFields)
                    Assert.Equal(table.Value.OrderBy(n => n, StringComparer.Ordinal).ToArray(), engine[table.Key].OrderBy(n => n, StringComparer.Ordinal).ToArray());
                calculated += table.Value.Count;
            }
            if (report != null) File.AppendAllText(report, file + "\t\tCATALOG\t" + dao.Count + " tables, " + calculated + " calculated fields\r\n");
            _output.WriteLine(file + ": " + dao.Count + " tables as DAO lists them, " + calculated + " calculated fields");
            // The comparison is of something: the corpus's file of calculated fields has eleven.
            if (knowsCalculatedFields && file.EndsWith("calcFieldV2010.accdb", StringComparison.Ordinal)) Assert.Equal(11, calculated);
        }

        private static string WhyHiddenFromDao(string table)
        {
            if (table.StartsWith("MSysComplexType", StringComparison.Ordinal)) return "a template for a complex column's type";
            if (System.Text.RegularExpressions.Regex.IsMatch(table, "^f_[0-9A-F]{32}_")) return "the hidden table of a complex column's values";
            if (table.StartsWith("MSysWSDP", StringComparison.Ordinal)) return "the SharePoint cache's";
            return null;
        }

        /// <summary>
        /// Why <paramref name="table"/> of <paramref name="file"/> may go without a second reader, or null when it
        /// may not: a table that is passed over for any other reason fails the test, so that a comparison which
        /// stopped being made is seen.
        /// </summary>
        private static string WhyNoSecondReader(string file, Table table, string message)
        {
            var name = table.Name;
            return WhyNoSecondReader(name, table.Columns, message);
        }

        private static string WhyNoSecondReader(string table, IReadOnlyList<Column> columns, string message)
        {
            // Neither ACE nor DAO queries them ("Unexpected situation encountered when expanding a complex type
            // query"; 32-bit ACE 12: "Reserved error (-3087)"). They hold no rows.
            if (table.StartsWith("MSysComplexType", StringComparison.Ordinal)) return "a template for a complex column's type";
            // Access opens one through the complex column it belongs to, never by name (#24).
            if (System.Text.RegularExpressions.Regex.IsMatch(table, "^f_[0-9A-F]{32}_")) return "the hidden table of a complex column's values";
            // A table with calculated fields, to a reader older than they are: 32-bit ACE 12 is Access 2007's.
            if (columns.Any(c => c.IsCalculated) && message.Contains("cannot read the data in"))
                return "its calculated fields are newer than the reader";
            // System tables are read through DAO with the user's Access workgroup file; a machine without one
            // (a build agent) has no second reader for those ACE's provider refuses.
            if (table.StartsWith("MSys", StringComparison.Ordinal) && !File.Exists(Dao.Workgroup)) return "no Access workgroup file on this machine";
            return null;
        }

        /// <summary>
        /// The reader on this machine is older than the file and says so of the file, not of a table: 32-bit ACE 12
        /// does not open the Access 2010 format ("Unrecognized database format"), and the Access Database Engine
        /// 2016 redistributable - what CI installs - does not open a file with Access 2019's extended dates ("The
        /// database you are trying to open requires a newer version of Microsoft Access").
        /// </summary>
        private static bool ReaderDoesNotOpenTheFile(string message)
        {
            return message.Contains("Unrecognized database format") || message.Contains("requires a newer version of Microsoft Access");
        }

        [Theory]
        [MemberData(nameof(Files))]
        public void the_engine_reads_every_table_of_a_file_access_wrote_as_a_second_reader_reads_it(string file)
        {
            var path = Path.Combine(_tmp.Dir, Path.GetFileName(file));
            File.Copy(Path.Combine(CorpusRoot(), file.Replace('/', Path.DirectorySeparatorChar)), path);

            // What was compared, for whoever wants the list: CORPUS_REFEREE_REPORT names a file to add it to.
            var report = Environment.GetEnvironmentVariable("CORPUS_REFEREE_REPORT");
            var compared = 0;
            var passedOver = new List<string>();
            var notAllowed = new List<string>();
            // Opened only to be read, so that ACE - which opens a file to write - can open it beside the engine: a file
            // the engine has open to be written is kept from every writer (#62), and ACE
            // refused would leave DAO, read-only, as the second reader of every table.
            using (var db = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true }))
            {
                foreach (var name in db.GetTableNames().Concat(db.GetSystemTableNames()))
                {
                    var table = db.GetTable(name);
                    var columns = table.Columns.Where(c => Compared(name, c)).ToList();
                    if (columns.Count == 0)
                    {
                        // Counted, not skipped: a table with nothing to compare is one that was not compared.
                        passedOver.Add(name + ": no column of it is compared");
                        notAllowed.Add(name + ": no column of it is compared");
                        continue;
                    }

                    List<Values> second;
                    string reader;
                    try
                    {
                        // ACE's OLE DB provider hands out no Date/Time Extended value: reading one fails, or gives
                        // a date that is not the row's. DAO reads a table that has such a column.
                        if (columns.Any(c => c.DataType == DataType.DateTimeExtended))
                            throw new NotSupportedException("The provider does not read a Date/Time Extended column.");
                        second = AceRows(path, "SELECT " + string.Join(", ", columns.Select(c => "[" + c.Name + "]")) + " FROM [" + name + "]");
                        reader = "ACE";
                    }
                    catch (Exception)
                    {
                        try
                        {
                            // DAO does not hand out a security identifier: the Owner of a catalog row and the SID of
                            // a permission read as null through it, whatever the row holds.
                            columns = columns.Where(c => !(c.DataType == DataType.Binary && (c.Name == "Owner" || c.Name == "SID"))).ToList();
                            second = DaoRows(path, name, columns);
                            reader = "DAO";
                        }
                        catch (Exception e)
                        {
                            // A table neither will read (a link to a file that is not there, a table whose columns
                            // the engine could not name): no referee for it.
                            var inner = e.InnerException ?? e;
                            passedOver.Add(name + ": " + inner.Message);
                            if (!ReaderDoesNotOpenTheFile(inner.Message) && WhyNoSecondReader(file, table, inner.Message) == null)
                                notAllowed.Add(name + ": " + inner.Message);
                            if (report != null) File.AppendAllText(report, file + "\t" + name + "\tNONE\t" + inner.Message.Replace('\n', ' ').Replace('\r', ' ') + "\r\n");
                            continue;
                        }
                    }

                    var engine = table.Rows.Select(row =>
                    {
                        var values = new Values();
                        foreach (var column in columns)
                        {
                            object value;
                            values[column.Name] = row.TryGetValue(column.Name, out value) ? value : null;
                            if (column.DataType == DataType.DateTimeExtended && value is DateTime)
                                values[column.Name] = ToTheTick((DateTime)value);
                        }
                        return values;
                    }).ToList();
                    AssertSame(Written(second), Written(engine), file + ", table " + name + ": the engine's reading against " + reader + "'s");
                    compared++;
                    if (report != null) File.AppendAllText(report, file + "\t" + name + "\t" + reader + "\t" + engine.Count + "\r\n");
                }
            }
            // A file this process has no second reader for at all.
            if (compared == 0 && passedOver.Count > 0 && passedOver.All(ReaderDoesNotOpenTheFile))
                Assert.Skip(file + ": no second reader in this process opens the file.");
            // How much was refereed, said aloud. System tables need DAO and the user's Access workgroup file, so on
            // a machine without one (a build agent) fewer tables have a second reader: of the tables the 64-bit suite
            // compares on a machine with one, ACE reads the greater part and DAO the rest (the counts are in
            // CLAUDE.md, as this test prints them). A table may go without a second reader only for a reason
            // WhyNoSecondReader knows: any other is a comparison that stopped being made.
            _output.WriteLine(file + ": " + compared + " tables compared with a second reader, " + passedOver.Count + " without one"
                + (passedOver.Count > 0 ? " (" + string.Join("; ", passedOver.Select(p => p.Split(':')[0]).Take(12)) + ")" : ""));
            Assert.True(notAllowed.Count == 0, file + ": tables with no second reader, and no reason known for it: " + string.Join(" | ", notAllowed));
            Assert.True(compared > 0, file + ": no table was compared. " + string.Join(" | ", passedOver));
        }
    }
}
