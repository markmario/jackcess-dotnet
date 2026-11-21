using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using JackcessDotNet.AccessTests.Support;
using JackcessDotNet;
using Xunit;

namespace JackcessDotNet.AccessTests
{
    /// <summary>
    /// Databases whose text sorts in another locale's order (the engine's #23). DAO's <c>CreateDatabase</c> takes a
    /// locale, as Access's "New database sort order" does, and gives every Text and Memo column of the database that
    /// locale's sort order: its LCID, where General - Legacy has 1033. An index entry's key is the text in that
    /// order, so the engine can keep a text index only where it makes the keys ACE makes.
    /// <para>
    /// What ACE's keys are was measured with the engine's tools/ace-text-keys.ps1, which has ACE index a list of
    /// values and reads the keys back from the file: for the locales in <see cref="KeyedAsGeneralLegacy"/> they are
    /// the keys of General - Legacy, byte for byte, for 1,013,302 values each (every character of the BMP alone and
    /// between two letters, every pair of letters of Latin-1 and Latin Extended-A, every pair within the Greek,
    /// Cyrillic, Hebrew, Arabic and Thai blocks, letters with every combining mark, and 500,000 values of two to
    /// eight characters), and for those in <see cref="KeyedOtherwise"/> they are not. The engine writes text keys in
    /// the first and refuses to in the second; here ACE is asked through the index for what the engine wrote in the
    /// first, and the engine is held to refusing the second with nothing changed.
    /// </para>
    /// <para>
    /// Two of them are sweeps that take minutes a locale, and without <see cref="Sweeps.Full"/> they run a sample:
    /// the walk of the index runs in every locale over some 3,500 values chosen for it (<see cref="SampleValues"/>)
    /// rather than 50,000, and the seeks in three of the locales (<see cref="KeyedAsGeneralLegacyToSeek"/>).
    /// </para>
    /// </summary>
    public class SortOrdersInAccessTests : IDisposable
    {
        private readonly TempDb _tmp = new TempDb();
        public void Dispose() { _tmp.Dispose(); }

        /// <summary>The locales whose text ACE keys as it keys General - Legacy: LCID and name.</summary>
        public static IEnumerable<object[]> KeyedAsGeneralLegacy()
        {
            yield return new object[] { 0x0401, "Arabic" };
            yield return new object[] { 0x0407, "German" };
            yield return new object[] { 0x0408, "Greek" };
            yield return new object[] { 0x040D, "Hebrew" };
            yield return new object[] { 0x0410, "Italian" };
            yield return new object[] { 0x0413, "Dutch" };
            yield return new object[] { 0x0419, "Cyrillic" };
            yield return new object[] { 0x0809, "English (United Kingdom)" };
            yield return new object[] { 0x0816, "Portuguese" };
        }

        /// <summary>
        /// The locales the seeks run in: every one of <see cref="KeyedAsGeneralLegacy"/> in the full sweep, and in the
        /// sample three - a right-to-left script, a Latin one with ß, and a locale of a sublanguage other than the
        /// first (0x0809, where 0x0409 is the first), so that a code read by its primary language alone shows. What
        /// the seeks add to the walk below is how ACE goes on with the engine's index pages, which is the same in
        /// every locale; whether each locale's keys are General - Legacy's the walk asks in all nine.
        /// </summary>
        public static IEnumerable<object[]> KeyedAsGeneralLegacyToSeek()
        {
            return Sweeps.Full ? KeyedAsGeneralLegacy() : KeyedAsGeneralLegacy().Where(locale => new[] { 0x0401, 0x0407, 0x0809 }.Contains((int)locale[0]));
        }

        /// <summary>Some of the locales whose text ACE keys otherwise: LCID and name.</summary>
        public static IEnumerable<object[]> KeyedOtherwise()
        {
            yield return new object[] { 0x0405, "Czech" };
            yield return new object[] { 0x040A, "Spanish" };
            yield return new object[] { 0x0411, "Japanese" };
            yield return new object[] { 0x041D, "Swedish" };
            yield return new object[] { 0x041E, "Thai" };
            yield return new object[] { 0x041F, "Turkish" };
            yield return new object[] { 0x0422, "Ukrainian" };
            yield return new object[] { 0x0804, "Chinese" };
        }

        /// <summary>A new Access 2000 database DAO made in the locale <paramref name="lcid"/>.</summary>
        private string DatabaseIn(int lcid)
        {
            var path = _tmp.NewMdbPath();
            var dbeType = Type.GetTypeFromProgID("DAO.DBEngine.120", throwOnError: true);
            var dbe = Dao.Engine;
            try
            {
                var db = dbeType.InvokeMember("CreateDatabase", BindingFlags.InvokeMethod, null, dbe,
                    new object[] { path, ";LANGID=0x" + lcid.ToString("X4") + ";CP=1252;COUNTRY=0", 64 });
                db.GetType().InvokeMember("Close", BindingFlags.InvokeMethod, null, db, null);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(db);
            }
            finally { Dao.Release(dbe); }
            return path;
        }

        private static ColumnDefinition[] Columns(params ColumnDefinition[] columns) { return columns; }
        private static ColumnDefinition Long(string name) { return new ColumnDefinition(name, DataType.Int32); }
        private static ColumnDefinition Text(string name, int chars) { return new ColumnDefinition(name, DataType.Text) { MaxLength = chars }; }

        // Words of the locales' own scripts among the Latin ones: accents, a character sorted as two letters,
        // punctuation Access weighs apart, an ideograph.
        private static readonly string[] Words =
        {
            "résumé", "Æther", "naïve", "Zürich", "日本", "a-b", "O'Brien", "Straße", "Ελληνικά", "Привет", "שלום", "مرحبا",
            "ijs", "città", "ação", "Łódź", "Ёлка", "ψυχή",
        };

        private static int Key(int i) { return (int)((i * 7919L) % 100003); }

        private static string Code(int i) { return Words[i % Words.Length] + " " + Key(i).ToString("D6"); }

        private static void Insert(OleDbConnection connection, string table, string code, int n)
        {
            using (var command = new OleDbCommand("INSERT INTO [" + table + "] (Code, N) VALUES (?, ?)", connection))
            {
                command.Parameters.Add("Code", OleDbType.VarWChar, 255).Value = code;
                command.Parameters.Add("N", OleDbType.Integer).Value = n;
                command.ExecuteNonQuery();
            }
        }

        private static object NumberOf(OleDbConnection connection, string table, string code)
        {
            using (var command = new OleDbCommand("SELECT N FROM [" + table + "] WHERE Code = ?", connection))
            {
                command.Parameters.Add("Code", OleDbType.VarWChar, 255).Value = code;
                return command.ExecuteScalar();
            }
        }

        /// <summary>
        /// The codes among <paramref name="codes"/> that DAO does not find by a Seek on the table's primary key: a
        /// Seek goes down the index by the key DAO makes for the value, so it finds an entry only where ACE keeps it.
        /// </summary>
        private static List<string> NotFoundBySeek(string path, string table, IEnumerable<string> codes)
        {
            var missing = new List<string>();
            dynamic dbe = Dao.Engine;
            try
            {
                dynamic db = dbe.OpenDatabase(path);
                try
                {
                    dynamic rs = db.OpenRecordset(table, 1 /* dbOpenTable */);
                    try
                    {
                        rs.Index = "PrimaryKey";
                        foreach (var code in codes)
                        {
                            rs.Seek("=", code);
                            if ((bool)rs.NoMatch || !Equals((string)rs.Fields["Code"].Value, code)) missing.Add(code);
                        }
                    }
                    finally { rs.Close(); }
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
            return missing;
        }

        /// <summary>
        /// In a database of each such locale the engine fills two tables with a text primary key, one ACE made and
        /// one the engine made with an index on the text as well: ACE seeks every entry, adds its own between them,
        /// refuses a duplicate of the engine's, and DAO compacts the file with every row; the engine goes on from
        /// ACE's pages. And a table the engine creates there is one ACE lists, finds by its name, which the catalog
        /// keys in the locale's order, and reads (the engine's #48, fixed by its #53).
        /// </summary>
        [Theory]
        [MemberData(nameof(KeyedAsGeneralLegacyToSeek))]
        public void ace_seeks_and_adds_to_a_text_key_the_engine_built_in_a_database_of_a_locale_keyed_as_general_legacy(int lcid, string name)
        {
            var path = DatabaseIn(lcid);
            AccessReferee.Execute(path, "CREATE TABLE Coded (Code TEXT(60) CONSTRAINT PrimaryKey PRIMARY KEY, N LONG)");
            var tables = new[] { "Coded", "Twice" };

            using (var db = Database.Open(path))
            {
                var coded = db.GetTable("Coded");
                var again = new IndexDefinition("ByAgain", "Again");
                again.DescendingColumns.Add("Again");
                var twice = db.CreateTable("Twice", Columns(Text("Code", 60), Long("N"), Text("Again", 60)), new[] { "Code" }, new[] { again });
                for (var i = 1; i <= 3000; i++)
                {
                    coded.Insert(new Row { ["Code"] = Code(i), ["N"] = i });
                    twice.Insert(new Row { ["Code"] = Code(i), ["N"] = i, ["Again"] = Code(i) });
                }

                // A table of the engine's own: its name is keyed in the catalog, in the locale's order.
                var made = db.CreateTable("Ünïcødé Made", Columns(Text("Code", 60), Long("N")), primaryKey: "Code");
                made.Insert(new Row { ["Code"] = Code(1), ["N"] = 1 });
            }
            Assert.Contains("already exists", Assert.Throws<OleDbException>(() => AccessReferee.Execute(path, "CREATE TABLE [Ünïcødé Made] (Id LONG)")).Message);
            Assert.Contains("already exists", Assert.Throws<OleDbException>(() => AccessReferee.Execute(path, "CREATE TABLE [ÜNÏCØDÉ made] (Id LONG)")).Message);
            Assert.Equal(1, AccessReferee.Scalar(path, "SELECT N FROM [Ünïcødé Made]"));

            // Through the descending index on the other text column, which only the engine has written to.
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                foreach (var i in new[] { 1, 10, 11, 1500, 3000 })
                    using (var command = new OleDbCommand("SELECT N FROM Twice WHERE Again = ?", connection))
                    {
                        command.Parameters.Add("Again", OleDbType.VarWChar, 255).Value = Code(i);
                        Assert.Equal(i, command.ExecuteScalar());
                    }
            }
            var byAgain = AccessReferee.DaoRows(path, "Twice", "ByAgain").Select(row => (string)row["Again"]).ToList();
            Assert.Equal(3000, byAgain.Count);
            Assert.Equal(Enumerable.Range(1, 3000).Select(Code).OrderBy(code => code, StringComparer.Ordinal), byAgain.OrderBy(code => code, StringComparer.Ordinal));

            foreach (var table in tables)
            {
                // Every entry the engine wrote is where ACE looks for it.
                Assert.True(NotFoundBySeek(path, table, Enumerable.Range(1, 3000).Select(Code)).Count == 0, name + ": DAO does not find by a Seek what the engine wrote to " + table);

                using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
                {
                    connection.Open();
                    Assert.Equal(10, NumberOf(connection, table, Code(10)));
                    Assert.Equal(11, NumberOf(connection, table, Code(11)));
                    for (var i = 3001; i <= 4500; i++)   // between the engine's keys: ACE writes into and splits its leaves
                        Insert(connection, table, Code(i), i);
                    foreach (var i in Enumerable.Range(1, Words.Length))   // a key the engine wrote is one ACE finds taken
                        Assert.Contains("duplicate values", Assert.Throws<OleDbException>(() => Insert(connection, table, Code(i), -1)).Message);
                }
                Assert.Equal(4500, AccessReferee.Scalar(path, "SELECT COUNT(*) FROM [" + table + "]"));
            }

            // The engine finds ACE's rows and its own, and adds to the pages ACE wrote and split.
            using (var db = Database.Open(path))
            {
                foreach (var name2 in tables)
                {
                    var table = db.GetTable(name2);
                    Assert.Equal(4500, table.Rows.Count());
                    Assert.Equal(4234, table.FindRowByPrimaryKey(Code(4234))["N"]);
                    Assert.Equal(17, table.FindRowByPrimaryKey(Code(17))["N"]);
                    Assert.Throws<ConstraintViolationException>(() => table.Insert(new Row { ["Code"] = Code(4234).ToUpperInvariant(), ["N"] = -1 }));
                    for (var i = 4501; i <= 5000; i++)
                        table.Insert(new Row { ["Code"] = Code(i), ["N"] = i });
                    table.DeleteRow("Code", Code(20));
                    table.DeleteRow("Code", Code(4000));
                    table.UpdateByPrimaryKey(Code(30), new Row { ["Code"] = Code(6000) });
                }
            }
            var kept = Enumerable.Range(1, 5000).Where(i => i != 20 && i != 4000 && i != 30).Concat(new[] { 6000 }).Select(Code).ToList();
            var gone = new[] { Code(20), Code(4000), Code(30) };
            foreach (var table in tables)
            {
                Assert.Equal(4998, AccessReferee.Scalar(path, "SELECT COUNT(*) FROM [" + table + "]"));
                Assert.True(NotFoundBySeek(path, table, kept).Count == 0, name + ": DAO does not find by a Seek a row of " + table);
                Assert.Equal(gone, NotFoundBySeek(path, table, gone));
            }

            var compacted = AccessReferee.Compact(path);
            foreach (var table in tables)
            {
                Assert.Equal(4998, AccessReferee.Scalar(compacted, "SELECT COUNT(*) FROM [" + table + "]"));
                Assert.Empty(NotFoundBySeek(compacted, table, kept));
            }
            Assert.Throws<OleDbException>(() => AccessReferee.Scalar(compacted, "SELECT COUNT(*) FROM MSysCompactError"));   // DAO had nothing to record
        }

        /// <summary>Values for an index: characters of the whole BMP alone and between two letters, pairs of the scripts' letters, and values of several characters.</summary>
        private static List<string> Values()
        {
            var values = new List<string>();
            for (var c = 1; c < 0x10000; c += 3)
                if (c < 0xD800 || c > 0xDFFF) values.Add(((char)c).ToString());
            for (var c = 0x21; c < 0x10000; c += 7)
                if (c < 0xD800 || c > 0xDFFF) values.Add("a" + (char)c + "b");
            foreach (var block in new[] { 0x0391, 0x0410, 0x05D0, 0x0621, 0x00C0 })
                for (var a = 0; a < 40; a++)
                    for (var b = 0; b < 40; b++)
                        values.Add(new string(new[] { (char)(block + a), (char)(block + b) }));

            var random = new Random(23);
            var pools = new[] { Tuple.Create(0x20, 0x7F), Tuple.Create(0xA0, 0x250), Tuple.Create(0x370, 0x500), Tuple.Create(0x590, 0x700), Tuple.Create(0x300, 0x370), Tuple.Create(0x2000, 0x3100) };
            for (var i = 0; i < 12000; i++)
            {
                var text = new StringBuilder();
                var length = random.Next(2, 9);
                for (var j = 0; j < length; j++)
                {
                    var pool = pools[random.Next(10) < 4 ? 0 : random.Next(pools.Length)];
                    text.Append((char)random.Next(pool.Item1, pool.Item2));
                }
                values.Add(text.ToString());
            }
            // A value that ends in a space is the value without it to Access, and a table holds no empty key here.
            return values.Where(v => v.Trim(' ').Length == v.Length).ToList();
        }

        /// <summary>
        /// Text the other locales' orders key otherwise, measured (the engine's docs/indexes.md, "Text index keys: sort
        /// orders and limits"): letters sorted as two (ß, æ, œ, ĳ and the ligatures, beside the pairs they stand for), the digraphs of Czech,
        /// Spanish, Hungarian and Croatian, the letters Scandinavian, Turkish, Polish, Romanian, Icelandic, Ukrainian
        /// and Vietnamese sort apart, French's accents, Thai's vowels written before their consonant, kana, a
        /// Japanese mark keyed by the character before it (refused, as ACE keys it by its neighbour), the hyphen and
        /// apostrophe Access weighs apart, digits, full-width forms and case. In these nine locales each must still
        /// be General - Legacy's key.
        /// </summary>
        private static readonly string[] OtherOrdersDiffer =
        {
            "ß", "ẞ", "ss", "SS", "Straße", "Strasse", "STRASSE", "Maße", "Masse",
            "æ", "Æ", "ae", "AE", "Ærø", "œ", "Œ", "oe", "OE", "œuvre", "oeuvre", "cœur", "coeur",
            "ĳ", "Ĳ", "ij", "IJ", "ﬁ", "ﬂ", "ﬀ", "fi", "ǆ", "ǅ", "Ǆ", "dž", "ǉ", "lj", "ǌ", "nj",
            "å", "Å", "aa", "Aa", "AA", "ä", "Ä", "ø", "Ø", "ö", "Ö", "ü", "Ü", "y", "w", "v", "vw", "wv", "Åsa", "Asa",
            "ch", "Ch", "CH", "cz", "hz", "chata", "hrad", "ll", "Ll", "LL", "lz", "llama", "luz", "ñ", "Ñ", "nz", "niño", "nino", "rr",
            "cs", "dz", "dzs", "gy", "ly", "ny", "sz", "ty", "zs", "č", "ć", "đ", "Đ", "š", "ž", "ř", "ě", "ů", "ô", "ľ",
            "ı", "I", "İ", "i", "ıi", "İi", "Iı", "ğ", "ş", "ç", "Ç",
            "ą", "ę", "ł", "Ł", "ń", "ś", "ź", "ż", "į", "ų", "ă", "â", "î", "ș", "ş", "ț", "ţ",
            "ð", "Ð", "þ", "Þ", "Việt", "ơ", "ư", "ạ", "ả", "ấ",
            "cote", "coté", "côte", "côté",
            "ґ", "Ґ", "є", "Є", "і", "ї", "Ї", "г", "е", "и", "й", "ь", "ʼ", "ёлка", "елка", "Ёж",
            "เก", "เกม", "แก", "โก", "ใก", "ไก", "กเ", "ก่", "ก้", "กา", "กิ", "ขา", "เข",
            "ア", "あ", "ｱ", "カ", "ガ", "か", "が", "ニホン", "にほん", "ー", "日本", "中", "中国", "한", "한국",
            "a-b", "ab", "a'b", "-ab", "'ab", "co-op", "coop", "O'Brien", "OBrien", "a–b", "a—b",
            "1", "10", "2", "١", "٢", "١٢", "１", "Ａ", "ａ", "A", "a", "Ab", "aB", "AB",
            "مرحبا", "مَرْحَبًا", "שלום", "שָׁלוֹם", "Ελληνικά", "ελληνικα", "ά", "ψυχή",
        };

        /// <summary>
        /// The values the walk of the index takes in a pull request's run, some 3,500 for each locale where
        /// <see cref="Values"/> has 50,000 (see <see cref="Sweeps"/>). Each part of the full sweep is here at a stride,
        /// and the nine locales take it from nine places, so that between them the run covers much of what one
        /// locale's full sweep does: every 64th character of the BMP alone, and every 64th between two letters, from
        /// a place that moves seven on for each locale (the two half a stride apart); the pairs of the letters of
        /// Greek, Cyrillic, Hebrew, Arabic, Latin-1 and Thai at strides of three and five; a letter with each combining
        /// mark; <see cref="OtherOrdersDiffer"/>; and 600 values of two to eight characters drawn as the full sweep
        /// draws them, from a seed of the locale's own. A change to the engine's text keys in these locales moves the
        /// keys of most letters, or of a block, or of characters that sort as two or not at all, and each of those is
        /// here in every locale.
        /// </summary>
        private static List<string> SampleValues(int lcid)
        {
            var locale = KeyedAsGeneralLegacy().Select(l => (int)l[0]).ToList().IndexOf(lcid);
            if (locale < 0) throw new ArgumentException("No sample for locale " + lcid, nameof(lcid));

            var values = new List<string>();
            for (var c = 1 + 7 * locale; c < 0x10000; c += 64)
                if (c < 0xD800 || c > 0xDFFF) values.Add(((char)c).ToString());
            for (var c = 0x21 + 32 + 7 * locale; c < 0x10000; c += 64)
                if (c < 0xD800 || c > 0xDFFF) values.Add("a" + (char)c + "b");
            foreach (var block in new[] { 0x0391, 0x0410, 0x05D0, 0x0621, 0x00C0, 0x0E01 })
                for (var a = locale % 3; a < 40; a += 3)
                    for (var b = locale % 5; b < 40; b += 5)
                        values.Add(new string(new[] { (char)(block + a), (char)(block + b) }));
            for (var mark = 0x0300; mark < 0x0370; mark++)
                values.Add((locale % 2 == 0 ? "a" : "E") + (char)mark);
            values.AddRange(OtherOrdersDiffer);

            var random = new Random(lcid);
            var pools = new[] { Tuple.Create(0x20, 0x7F), Tuple.Create(0xA0, 0x250), Tuple.Create(0x370, 0x500), Tuple.Create(0x590, 0x700), Tuple.Create(0x300, 0x370), Tuple.Create(0x2000, 0x3100) };
            for (var i = 0; i < 600; i++)
            {
                var text = new StringBuilder();
                var length = random.Next(2, 9);
                for (var j = 0; j < length; j++)
                {
                    var pool = pools[random.Next(10) < 4 ? 0 : random.Next(pools.Length)];
                    text.Append((char)random.Next(pool.Item1, pool.Item2));
                }
                values.Add(text.ToString());
            }
            return values.Where(v => v.Trim(' ').Length == v.Length).ToList();
        }

        /// <summary>The numbers of <paramref name="table"/>'s rows in the order of its index on the text: DAO walks the index's own pages.</summary>
        private static List<int> InIndexOrder(string path, string table)
        {
            return AccessReferee.DaoRows(path, table, "ByText").Select(row => Convert.ToInt32(row["N"])).ToList();
        }

        /// <summary>
        /// Some 50,000 values, each added by ACE to one table and by the engine to another, both made by ACE with
        /// an index on the text: DAO, walking each index, comes to the rows in the same order - the engine's entry
        /// for every value is where ACE's own is among the others - and seeks each of the engine's. A value the
        /// engine makes no key for (one with a character Access keys by its neighbours) is refused and left out of
        /// both. Without <see cref="Sweeps.Full"/>, the values are the locale's sample (<see cref="SampleValues"/>).
        /// </summary>
        [Theory]
        [MemberData(nameof(KeyedAsGeneralLegacy))]
        public void dao_walks_the_engine_s_index_in_the_order_of_ace_s_own_in_a_database_of_a_locale_keyed_as_general_legacy(int lcid, string name)
        {
            var path = DatabaseIn(lcid);
            foreach (var table in new[] { "A", "E" })
                AccessReferee.Execute(path, "CREATE TABLE " + table + " (N LONG, T TEXT(255))", "CREATE INDEX ByText ON " + table + " (T)");

            var values = Sweeps.Full ? Values() : SampleValues(lcid);
            var taken = new List<KeyValuePair<int, string>>();
            using (var db = Database.Open(path))
            {
                var e = db.GetTable("E");
                for (var i = 0; i < values.Count; i++)
                {
                    try { e.Insert(new Row { ["N"] = i, ["T"] = values[i] }); }
                    catch (NotSupportedException) { continue; }
                    taken.Add(new KeyValuePair<int, string>(i, values[i]));
                }
            }
            // The full sweep refuses some 30 of its 50,000 values, the sample one to five of its 3,500.
            Assert.True(taken.Count > values.Count - (Sweeps.Full ? 600 : values.Count / 100), name + ": the engine took " + taken.Count + " of " + values.Count + " values");

            dynamic dbe = Dao.Engine;
            var notSought = new List<int>();
            try
            {
                dynamic db = dbe.OpenDatabase(path);
                try
                {
                    dynamic rs = db.OpenRecordset("A", 1 /* dbOpenTable */);
                    try
                    {
                        dynamic number = rs.Fields["N"], text = rs.Fields["T"];
                        foreach (var value in taken)
                        {
                            rs.AddNew();
                            number.Value = value.Key;
                            text.Value = value.Value;
                            rs.Update();
                        }
                    }
                    finally { rs.Close(); }

                    rs = db.OpenRecordset("E", 1);
                    try
                    {
                        rs.Index = "ByText";
                        foreach (var value in taken)
                        {
                            rs.Seek("=", value.Value);
                            if ((bool)rs.NoMatch) notSought.Add(value.Key);
                        }
                    }
                    finally { rs.Close(); }
                }
                finally { db.Close(); }
            }
            finally { Dao.Release(dbe); }
            Assert.True(notSought.Count == 0, name + ": DAO does not find by a Seek " + notSought.Count + " of the engine's values, the first U+"
                + string.Join(" U+", (notSought.Count > 0 ? values[notSought[0]] : "").Select(c => ((int)c).ToString("X4"))));

            var ace = InIndexOrder(path, "A");
            var engine = InIndexOrder(path, "E");
            Assert.Equal(taken.Count, ace.Count);
            var first = Enumerable.Range(0, Math.Min(ace.Count, engine.Count)).Where(i => ace[i] != engine[i]).Take(1).ToList();
            Assert.True(ace.SequenceEqual(engine), name + ": the indexes are in different orders" + (first.Count == 0 ? "" : ", first at entry " + first[0]
                + ": ACE's is U+" + string.Join(" U+", values[ace[first[0]]].Select(c => ((int)c).ToString("X4")))
                + ", the engine's U+" + string.Join(" U+", values[engine[first[0]]].Select(c => ((int)c).ToString("X4")))));
        }

        /// <summary>
        /// In a database of a locale whose keys are not those of General - Legacy the engine makes no text key: a
        /// row for a table with a text index, and a table (whose name the catalog keys), are refused with the file
        /// as it was, and what is there is read as ACE reads it - a row found by its text, by reading the table.
        /// </summary>
        [Theory]
        [MemberData(nameof(KeyedOtherwise))]
        public void the_engine_reads_and_does_not_write_a_text_key_in_a_database_of_a_locale_keyed_otherwise(int lcid, string name)
        {
            var path = DatabaseIn(lcid);
            AccessReferee.Execute(path,
                "CREATE TABLE Coded (Code TEXT(60) CONSTRAINT PrimaryKey PRIMARY KEY, N LONG)",
                "CREATE TABLE Plain (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, Remark TEXT(60))");
            using (var connection = new OleDbConnection(AceVerification.ConnectionString(path)))
            {
                connection.Open();
                for (var i = 1; i <= 200; i++)
                    Insert(connection, "Coded", Code(i), i);
            }
            var before = File.ReadAllBytes(path);

            using (var db = Database.Open(path))
            {
                var coded = db.GetTable("Coded");
                var refused = Assert.Throws<NotSupportedException>(() => coded.Insert(new Row { ["Code"] = "new", ["N"] = 1 }));
                Assert.Contains("sorts in", refused.Message);
                Assert.Throws<NotSupportedException>(() => coded.UpdateByPrimaryKey(Code(5), new Row { ["N"] = 50 }));
                Assert.Throws<NotSupportedException>(() => coded.DeleteRow("Code", Code(5)));
                Assert.Throws<NotSupportedException>(() => db.CreateTable("Made", Columns(Long("Id"), Text("Txt", 20))));

                Assert.Equal(200, coded.Rows.Count());
                Assert.Equal(77, coded.CreateIndexCursor().FindRow("Code", Code(77))["N"]);
            }
            Assert.True(before.SequenceEqual(File.ReadAllBytes(path)), name + ": a refused change altered the file");
            AccessReferee.AssertEngineReadsWhatAceReads(path, "Coded");

            // A table with no text in an index is written to: its keys are numbers, the same in any order.
            using (var db = Database.Open(path))
                db.GetTable("Plain").Insert(new Row { ["Id"] = 1, ["Remark"] = Words[0] });
            Assert.Equal(Words[0], AccessReferee.Scalar(path, "SELECT Remark FROM Plain WHERE Id = 1"));
        }
    }
}
