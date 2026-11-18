using System.IO;
using JackcessDotNet.Util;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Tables Access made keep every one of their indexes as the engine changes their rows (qcatco/exportmdb.interop#27):
/// the primary key, the other indexes, an index on several columns or sorted descending, and a tree two logical
/// indexes share. Each index is read back from the file's pages and must hold exactly the table's rows - Access's
/// own entries, which the engine did not write, among them, so the keys compared are the ones Access wrote.
/// A table with an index the engine cannot write keys for is refused and left as it was.
/// </summary>
public sealed class AccessMadeIndexTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"access_ix_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private Database? Open(string version, string file)
    {
        string? source = TestCorpus.File(version, file);
        if (source is null) return null;   // the corpus is optional locally; CI always has it
        File.Copy(source, _path, overwrite: true);
        return Database.Open(_path);
    }

    // The check the other tests rest on, and the proof of each key type: for every row Access wrote, the key the
    // engine makes of its values is the key Access entered for it - before the engine changes anything. The
    // indexCodes files have a table for each column type, ascending and descending, with nulls.
    [Theory]
    [InlineData("V2000", "indexCodesV2000.mdb")]
    [InlineData("V2003", "indexCodesV2003.mdb")]
    [InlineData("V2007", "indexCodesV2007.accdb")]
    [InlineData("V2010", "indexCodesV2010.accdb")]   // text and Memo in Access 2010's General order
    public void The_keys_the_engine_makes_are_the_keys_Access_wrote(string version, string file)
    {
        using var db = Open(version, file);
        if (db is null) return;

        var proved = new SortedSet<string>();
        var refused = new SortedSet<string>();
        foreach (var table in db.ListTables().Select(db.GetTable).Where(t => t.Indexes.Count > 0))
        {
            var types = table.Indexes.SelectMany(ix => ix.Columns)
                .Select(c => $"{c.Column.DataType}{(c.IsAscending ? "" : " descending")}").ToList();
            if (table.Indexes.All(ix => ix.Columns.All(c => IndexKeys.CanEncode(c.Column))))
            {
                Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, table.Name));
                proved.UnionWith(types);
            }
            else
            {
                refused.UnionWith(table.Indexes.SelectMany(ix => ix.Columns).Where(c => !IndexKeys.CanEncode(c.Column)).Select(c => c.Column.DataType.ToString()));
            }
        }

        foreach (string type in new[] { "Byte", "Int", "Long", "Money", "Float", "Double", "ShortDateTime", "Boolean", "Text", "Memo", "Guid" })
            Assert.Contains(type, proved);
        // (The corpus has no descending index on an Integer or a Date/Time column; ACE makes those in
        // qcatco/exportmdb.interop's EngineIndexesInAccessTests.)
        foreach (string type in new[] { "Byte", "Long", "Money", "Float", "Double", "Boolean", "Text", "Memo", "Guid" })
            Assert.Contains(type + " descending", proved);
        Assert.Equal(new[] { "Numeric" }, refused);
    }

    // The four bytes before an index block's columns and before a logical index's numbers: Access writes 1923 and
    // 1625 there in every Access 2000 and later file of the corpus, so the engine writes them in the definitions
    // it makes. (An Access 97 block and slot have no such bytes: 39 and 20 bytes, all of them accounted for.)
    [Fact]
    public void Access_begins_each_index_block_with_1923_and_each_index_slot_with_1625_and_so_does_the_engine()
    {
        static List<(int Block, int Slot)> Magics(Database db, Table table)
        {
            byte[] p = db.File.ReadPage(table.Definition.TdefPageNumber);
            int numCols = ByteUtil.GetShort(p, 45), slots = ByteUtil.GetInt(p, 47), blocks = ByteUtil.GetInt(p, 51);
            int pos = 63 + blocks * 12 + numCols * 25;
            for (int i = 0; i < numCols; i++) pos += 2 + ByteUtil.GetShort(p, pos);
            var found = new List<(int, int)>();
            for (int i = 0; i < slots; i++)
                found.Add((ByteUtil.GetInt(p, pos + Math.Min(i, blocks - 1) * 52), ByteUtil.GetInt(p, pos + blocks * 52 + i * 28)));
            return found;
        }

        int accessIndexes = 0;
        foreach (string file in TestCorpus.Files("*.*", "V2000", "V2003", "V2007", "V2010"))
        {
            Database db;
            try { db = Database.Open(file); }
            catch (Exception e) when (e is NotSupportedException or UnauthorizedAccessException) { continue; }
            using (db)
                foreach (var table in db.ListTables().Select(db.GetTable).Where(t => t.Indexes.Count > 0 && !t.Definition.HasUnreadIndexes))
                    foreach (var (block, slot) in Magics(db, table))
                    {
                        Assert.True(block == 1923 && slot == 1625, $"{Path.GetFileName(file)} {table.Name}: {block}, {slot}");
                        accessIndexes++;
                    }
        }
        if (TestCorpus.Root is not null) Assert.True(accessIndexes > 100, $"only {accessIndexes} indexes of Access's were looked at");

        using var made = Database.Create(_path, JetVersion.Jet4);
        var keyed = made.CreateTable("Keyed", new[] { new ColumnBuilder("Id", DataType.Long).Build() }, primaryKey: "Id");
        Assert.Equal(new[] { (1923, 1625) }, Magics(made, keyed));
    }

    // Access 97's index blocks and slots are read as 39 and 20 bytes with nothing before the columns or the
    // numbers, and every index so read has its root where an index page is: there is no such number in them.
    [Fact]
    public void An_Access_97_index_block_has_no_number_before_its_columns()
    {
        int indexes = 0;
        foreach (string file in TestCorpus.Files("*.mdb", "V1997"))
        {
            using var db = Database.Open(file);
            foreach (var table in db.ListTables().Select(db.GetTable).Where(t => !t.Definition.HasUnreadIndexes))
                foreach (var index in table.Indexes)
                {
                    Assert.NotEmpty(index.Columns);
                    Assert.Contains(db.File.ReadPage(index.RootPageNumber)[0], new byte[] { JetFormat.PageTypeIndexNode, JetFormat.PageTypeIndexLeaf });
                    indexes++;
                }
        }
        if (TestCorpus.Root is not null) Assert.True(indexes > 50, $"only {indexes} Access 97 indexes were looked at");
    }

    // The engine makes no table in an Access 97 file, with indexes or without: it says so before a page is added.
    [Fact]
    public void A_table_is_not_created_in_an_Access_97_file()
    {
        string? source = TestCorpus.File("V1997", "delV1997.mdb");
        if (source is null) return;   // the corpus is optional locally; CI always has it
        File.Copy(source, _path, overwrite: true);
        byte[] before = File.ReadAllBytes(_path);

        using (var db = Database.Open(_path))
        {
            Column[] Columns() => new[] { new ColumnBuilder("Id", DataType.Long).Build(), new ColumnBuilder("Grp", DataType.Long).Build() };
            Assert.Throws<NotSupportedException>(() => db.CreateTable("Plain", Columns()));
            Assert.Throws<NotSupportedException>(() => db.CreateTable("Keyed", Columns(), primaryKey: "Id"));
            Assert.Throws<NotSupportedException>(() => db.CreateTable("Indexed", Columns(), new[] { "Id" }, new[] { new IndexDefinition("ByGrp", "Grp") }));
        }

        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    // Single, Date/Time, Currency, Yes/No and Memo: rows of each go in, change and go, Access's own among them, and
    // the index holds exactly the rows left.
    [Theory]
    [InlineData("Table5")]
    [InlineData("Table5_desc")]
    [InlineData("Table6")]
    [InlineData("Table8")]
    [InlineData("Table8_desc")]
    [InlineData("Table10")]
    [InlineData("Table10_desc")]
    [InlineData("Table11")]
    [InlineData("Table11_desc")]
    public void An_index_Access_made_on_a_column_of_each_kept_type_is_kept(string tableName)
    {
        using var db = Open("V2003", "indexCodesV2003.mdb");
        if (db is null) return;
        var table = db.GetTable(tableName);
        var type = table.Columns.Single(c => c.Name == "data").DataType;
        var accessRows = table.ReadAllRows().Select(r => (string)r["name"]!).ToList();

        object? ValueOf(int i) => i % 9 == 0 ? null : type switch
        {
            DataType.Float         => (object)((i - 100) * 0.37f),
            DataType.ShortDateTime => new DateTime(1899, 12, 30).AddDays(i - 100).AddMilliseconds(i * 7919 % 86_400_000),
            DataType.Money         => (i - 100) * 1234.5678m,
            DataType.Boolean       => i % 2 == 0,
            DataType.Memo          => i % 4 == 0 ? new string('m', 300) + i : $"memo {i % 30}",   // beyond a key's 255 characters, and short
            _                      => throw new InvalidOperationException($"{tableName}.data is {type}"),
        };

        for (int i = 0; i < 200; i++)
            table.Insert(new Row { ["name"] = $"new{i:D3}", ["data"] = ValueOf(i) });
        foreach (string name in accessRows.Take(4))
            table.DeleteRow("name", name);                                        // rows and entries Access wrote
        foreach (string name in accessRows.Skip(4).Take(3))
            table.UpdateByPrimaryKey(name, new Row { ["data"] = ValueOf(50) });
        for (int i = 0; i < 200; i += 5)
            table.DeleteRow("name", $"new{i:D3}");
        for (int i = 1; i < 200; i += 5)
            table.UpdateByPrimaryKey($"new{i:D3}", new Row { ["data"] = ValueOf(i + 300) });

        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, tableName, expectedRows: accessRows.Count + 200 - 4 - 40));
    }

    // The same in Access 2010's file, whose text sorts in General: every table with an index on a Text or a Memo
    // column, ascending and descending, and each of them under a text primary key in that order. Rows go in,
    // change and go, Access's own among them, and both indexes hold exactly the rows left, under General keys.
    [Fact]
    public void A_text_or_Memo_index_Access_2010_made_in_General_is_kept()
    {
        using var db = Open("V2010", "indexCodesV2010.accdb");
        if (db is null) return;

        string[] words = { "résumé", "Æther", "naïve", "Zürich", "日本", "a-b", "O'Brien", "ß", "Ελληνικά", "\U0001F600", "テスト" };
        var kept = new List<string>();
        foreach (string tableName in db.ListTables())
        {
            var table = db.GetTable(tableName);
            var data = table.Columns.SingleOrDefault(c => c.Name == "data");
            if (data is null || data.DataType is not (DataType.Text or DataType.Memo) || table.Indexes.Count != 2) continue;
            Assert.True(data.SortOrder.IsGeneral && table.Columns.Single(c => c.Name == "name").SortOrder.IsGeneral);
            var accessRows = table.ReadAllRows().Select(r => (string)r["name"]!).ToList();
            if (accessRows.Count > 2000) continue;   // the tables of every character: looked up whole elsewhere

            string ValueOf(int i) => data.DataType == DataType.Memo && i % 4 == 0
                ? new string('é', 300) + i                              // beyond a key's 255 characters
                : $"{words[i % words.Length]} {i % 30}";
            for (int i = 0; i < 120; i++)
                table.Insert(new Row { ["name"] = $"{words[i % words.Length]} new{i:D3}", ["data"] = i % 9 == 0 ? null : ValueOf(i) });
            foreach (string name in accessRows.Take(3))
                table.DeleteRow("name", name);                                // rows and entries Access wrote
            foreach (string name in accessRows.Skip(3).Take(3))
                table.UpdateByPrimaryKey(name, new Row { ["data"] = ValueOf(50) });
            for (int i = 0; i < 120; i += 5)
                table.DeleteRow("name", $"{words[i % words.Length]} new{i:D3}");
            for (int i = 1; i < 120; i += 5)
                table.UpdateByPrimaryKey($"{words[i % words.Length]} new{i:D3}", new Row { ["data"] = ValueOf(i + 300) });

            Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, tableName, expectedRows: accessRows.Count + 120 - Math.Min(3, accessRows.Count) - 24));
            kept.Add($"{data.DataType}{(table.Indexes.SelectMany(ix => ix.Columns).Any(c => !c.IsAscending) ? " descending" : "")}");
        }
        // (The table with an ascending index on Text is the one of every character, left out above; each of these
        // tables has an ascending text key of its own, its primary key on "name".)
        Assert.Contains("Text descending", kept);
        Assert.Contains("Memo", kept);
        Assert.Contains(kept, k => k.EndsWith("descending", StringComparison.Ordinal));
    }

    // What reading forgave, writing must not trust: an index block that names a column the table has not is read
    // without that column, and a slot that names a block there is not is left out. Rows written by such a reading
    // would be keyed short, or not entered at all; the table is refused instead, and still read.
    [Theory]
    [InlineData(true)]    // the second column of Table12's (data1, data2) index names column 99
    [InlineData(false)]   // the slot of that index names block 7
    public void A_table_whose_index_definition_was_only_partly_read_is_refused_and_still_read(bool column)
    {
        using (var db = Open("V2003", "indexCodesV2003.mdb"))
        {
            if (db is null) return;
            var table = db.GetTable("Table12");
            var index = table.Indexes.Single(ix => ix.Name == "DataIndex");
            int tdef = table.Definition.TdefPageNumber;
            byte[] p = db.File.ReadPage(tdef);
            int numCols = ByteUtil.GetShort(p, 45), blocks = ByteUtil.GetInt(p, 51);
            int pos = 63 + blocks * 12 + numCols * 25;
            for (int i = 0; i < numCols; i++) pos += 2 + ByteUtil.GetShort(p, pos);
            if (column)
            {
                int second = pos + index.IndexDataNumber * 52 + 4 + 3;
                Assert.Equal(index.Columns[1].Column.ColumnNumber, ByteUtil.GetShort(p, second));
                ByteUtil.PutShort(p, second, 99);
            }
            else
            {
                int slot = pos + blocks * 52 + table.Indexes.ToList().IndexOf(index) * 28 + 4 + 4;
                Assert.Equal(index.IndexDataNumber, ByteUtil.GetInt(p, slot));
                ByteUtil.PutInt(p, slot, 7);
            }
            db.File.WritePage(tdef, p);
        }

        using var reopened = Database.Open(_path);
        var crafted = reopened.GetTable("Table12");
        Assert.True(crafted.Definition.HasUnreadIndexes);
        var rows = crafted.ReadAllRows();
        Assert.Equal(10, rows.Count);
        Assert.NotNull(crafted.NewIndexCursor().FindRow("row", rows[3]["row"]!));
        var before = IndexChecks.AllPages(reopened);

        Assert.Throws<NotSupportedException>(() => crafted.Insert(new Row { ["row"] = "another", ["data1"] = "x", ["data2"] = 1 }));
        Assert.Throws<NotSupportedException>(() => crafted.Validate(new Row { ["row"] = "another" }));
        Assert.Throws<NotSupportedException>(() => crafted.DeleteRow("row", rows[0]["row"]!));
        Assert.Throws<NotSupportedException>(() => crafted.UpdateByPrimaryKey(rows[0]["row"]!, new Row { ["data2"] = 5 }));

        IndexChecks.AssertUnchanged(before, reopened);
    }

    // indexV2003.mdb's Table2: an index on id, the primary key on id, and a relationship's index that shares the
    // primary key's tree - three indexes, two trees.
    [Fact]
    public void Rows_inserted_into_a_table_Access_made_are_entered_in_all_its_indexes()
    {
        using var db = Open("V2003", "indexV2003.mdb");
        if (db is null) return;
        var table = db.GetTable("Table2");
        Assert.Equal(3, table.Indexes.Count);

        for (int i = 0; i < 900; i++)   // enough to split both trees' leaves
            table.Insert(new Row { ["id"] = 1000 + (i * 7919) % 10007, ["data"] = $"row {i}" });

        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Table2", expectedRows: 903));
        Assert.All(db.GetTable("Table2").Indexes, ix => Assert.True(IndexChecks.Height(db, ix.RootPageNumber) >= 1, $"{ix.Name} did not split"));
    }

    [Fact]
    public void Rows_deleted_from_a_table_Access_made_leave_all_its_indexes()
    {
        using var db = Open("V2003", "indexV2003.mdb");
        if (db is null) return;
        var table = db.GetTable("Table2");
        for (int i = 0; i < 300; i++)
            table.Insert(new Row { ["id"] = 1000 + i, ["data"] = $"row {i}" });

        int accessRow = (int)table.ReadAllRows()[0]["id"]!;
        table.DeleteRow("id", accessRow);                 // a row Access wrote, with the entries Access wrote for it
        for (int i = 0; i < 300; i += 2)
            table.DeleteRow("id", 1000 + i);

        IndexChecks.AssertEveryIndexHoldsTheRows(db, "Table2", expectedRows: 3 + 300 - 1 - 150);
    }

    [Fact]
    public void An_updated_row_of_a_table_Access_made_moves_in_all_its_indexes()
    {
        using var db = Open("V2003", "indexV2003.mdb");
        if (db is null) return;
        var table = db.GetTable("Table2");
        for (int i = 0; i < 50; i++)
            table.Insert(new Row { ["id"] = 1000 + i, ["data"] = $"row {i}" });

        int accessRow = (int)table.ReadAllRows()[0]["id"]!;
        table.UpdateByPrimaryKey(accessRow, new Row { ["data"] = "changed" });
        for (int i = 0; i < 50; i += 3)
            table.UpdateByPrimaryKey(1000 + i, new Row { ["id"] = 5000 + i });   // the key itself: every tree's entry is re-keyed

        IndexChecks.AssertEveryIndexHoldsTheRows(db, "Table2", expectedRows: 53);
        var rows = db.GetTable("Table2").ReadAllRows();
        Assert.Equal("changed", rows.Single(r => (int)r["id"]! == accessRow)["data"]);
        Assert.Equal("row 3", rows.Single(r => (int)r["id"]! == 5003)["data"]);
        Assert.DoesNotContain(rows, r => (int)r["id"]! == 1003);
        Assert.Throws<InvalidOperationException>(() => table.UpdateByPrimaryKey(1001, new Row { ["id"] = accessRow }));
    }

    [Theory]
    [InlineData("Table12")]            // (data1, data2)
    [InlineData("Table12_asc_desc")]   // (data1, data2 descending)
    [InlineData("Table12_desc_asc")]   // (data1 descending, data2)
    public void An_index_Access_made_on_two_columns_is_kept(string tableName)
    {
        using var db = Open("V2003", "indexCodesV2003.mdb");
        if (db is null) return;
        var table = db.GetTable(tableName);
        int before = table.ReadAllRows().Count;

        for (int i = 0; i < 400; i++)
            table.Insert(new Row { ["row"] = $"new{i:D4}", ["data1"] = i % 5 == 0 ? null : $"d{i % 11}", ["data2"] = i % 3 == 0 ? null : (object)(i - 200) });
        for (int i = 0; i < 400; i += 4)
            table.DeleteRow("row", $"new{i:D4}");
        for (int i = 1; i < 400; i += 4)
            table.UpdateByPrimaryKey($"new{i:D4}", new Row { ["data1"] = $"u{i % 13}", ["data2"] = 7 });

        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, tableName, expectedRows: before + 300));
    }

    [Fact]
    public void A_duplicate_in_a_unique_index_Access_made_is_refused_with_nothing_changed()
    {
        using var db = Open("V2003", "indexCodesV2003.mdb");
        if (db is null) return;
        var table = db.GetTable("Table13");   // a GUID column with a unique index, beside the primary key
        Assert.True(table.Indexes.Single(ix => ix.Name == "data").IsUnique);
        table.AllowAutoNumberInsert = true;   // the column is an AutoNumber: the row brings a GUID the table has
        var taken = table.ReadAllRows().First(r => r["data"] is not null)["data"]!;
        var before = IndexChecks.AllPages(db);

        var refused = Assert.Throws<InvalidOperationException>(() => table.Insert(new Row { ["name"] = "a new name", ["data"] = taken }));

        Assert.Contains("duplicate", refused.Message);
        IndexChecks.AssertUnchanged(before, db);

        table.Insert(new Row { ["name"] = "a new name", ["data"] = Guid.NewGuid() });
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "Table13", expectedRows: 21);
    }

    [Fact]
    public void A_large_index_Access_made_is_kept_through_inserts_and_deletes()
    {
        using var db = Open("V2003", "indexCodesV2003.mdb");
        if (db is null) return;
        var table = db.GetTable("Table14");   // 66,429 rows; both its indexes are trees of several levels
        Assert.True(IndexChecks.Height(db, table.Indexes.Single(ix => ix.Name == "data").RootPageNumber) >= 2);
        Assert.True(IndexChecks.Height(db, table.Indexes.Single(ix => ix.IsPrimaryKey).RootPageNumber) >= 1);
        var first = table.ReadAllRows().Take(40).Select(r => (string)r["name"]!).ToList();

        for (int i = 0; i < 3_000; i++)
            table.Insert(new Row { ["name"] = $"engine row {i:D5}", ["data"] = $"{(char)('a' + i % 26)}{i * 7919 % 10007}" });
        foreach (string name in first)
            table.DeleteRow("name", name);

        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, "Table14", expectedRows: 66_429 + 3_000 - 40));
    }

    // Double keys: Access's own, for the numbers it indexed, are the keys the engine makes (the check above), and
    // the engine's new ones - negative, zero, tiny and huge - go among them in number order.
    [Theory]
    [InlineData("Table4")]
    [InlineData("Table4_desc")]
    public void An_index_Access_made_on_a_Double_column_is_kept(string tableName)
    {
        using var db = Open("V2003", "indexCodesV2003.mdb");
        if (db is null) return;
        var table = db.GetTable(tableName);
        int before = table.ReadAllRows().Count;
        double[] numbers = { 0d, -0.5, 0.5, -1e300, 1e300, 4.9e-324, -4.9e-324, 1d / 3, -1d / 3, 12345.678, -12345.678, double.MaxValue, double.MinValue };

        for (int i = 0; i < numbers.Length; i++)
            table.Insert(new Row { ["name"] = $"number {i}", ["data"] = numbers[i] });
        for (int i = 0; i < 300; i++)
            table.Insert(new Row { ["name"] = $"more {i}", ["data"] = i % 4 == 0 ? null : (object)((i - 150) * 1.37) });
        table.DeleteRow("name", "number 3");
        table.UpdateByPrimaryKey("number 4", new Row { ["data"] = -2.25 });

        Assert.Equal(2, IndexChecks.AssertEveryIndexHoldsTheRows(db, tableName, expectedRows: before + numbers.Length + 300 - 1));

        // The entries run in the numbers' order (reversed for the descending index), nulls at the low end.
        var index = db.GetTable(tableName).Indexes.Single(ix => ix.Name == "data");
        var rows = JetPages.Rows(db.File, IndexChecks.TdefOf(db, tableName)).ToDictionary(r => (r.Page, r.Row), r => r.Values);
        var inIndexOrder = JetPages.LeafEntries(db.File, index.RootPageNumber)
            .Select(e => rows[(e.RowPage, e.Row)].TryGetValue("data", out var v) && v is double d ? d : double.NegativeInfinity).ToList();
        var sorted = index.Columns[0].IsAscending ? inIndexOrder.OrderBy(d => d).ToList() : inIndexOrder.OrderByDescending(d => d).ToList();
        Assert.Equal(sorted, inIndexOrder);
    }

    // A refusal is of a write. A table with an index the engine cannot keep - or indexes it did not read - is
    // read as it always was: every row, by scan and by cursor, and through an index whose keys it does make.
    [Theory]
    [InlineData("V2007", "blobV2007.accdb", "Table1", 11)]      // an index on an attachment column
    [InlineData("V1997", "common1V1997.mdb", "Table1", 2)]      // Access 97 text keys
    [InlineData("V1997", "common1V1997.mdb", "Table2", 0)]      // a definition over several pages: indexes not read
    [InlineData("V1997", "common2V1997.mdb", "MSP_PROJECTS", 1)]
    [InlineData("V2003", "indexCodesV2003.mdb", "Table7", 20)]  // a Decimal index
    public void A_table_whose_indexes_the_engine_cannot_keep_is_still_read(string version, string file, string tableName, int rows)
    {
        string? source = TestCorpus.File(version, file);
        if (source is null) return;   // the corpus is optional locally; CI always has it
        File.Copy(source, _path, overwrite: true);
        byte[] before = File.ReadAllBytes(_path);

        using (var db = Database.Open(_path))
        {
            var table = db.GetTable(tableName);
            Assert.Throws<NotSupportedException>(() => table.Validate(new Row()));   // it is one of those tables

            var all = table.ReadAllRows();
            Assert.Equal(rows, all.Count);
            Assert.Equal(rows, table.NewCursor().Count());
            Assert.Equal(rows, table.RowCount);   // blobV2007: 8 of its 11 were read while rows Access had moved to another page were passed over
            _ = table.Properties;

            // Each row is found again by a column of its own, whichever way the cursor looks.
            foreach (var row in all.Take(5))
            {
                var column = table.Columns.First(c => row.TryGetValue(c.Name, out var v) && v is string or int or short);
                Assert.NotNull(table.NewIndexCursor().FindRow(column.Name, row[column.Name]!));
            }
        }

        Assert.Equal(before, File.ReadAllBytes(_path));
    }

    // A Decimal (Numeric) column: Access keeps its value among the row's variable-length values, where the engine
    // does not read or write it, so no key can be made of it.
    [Theory]
    [InlineData("Table7")]
    [InlineData("Table7_desc")]
    public void A_table_with_an_index_the_engine_cannot_write_keys_for_is_refused_and_left_as_it_was(string tableName)
    {
        using var db = Open("V2003", "indexCodesV2003.mdb");
        if (db is null) return;
        var table = db.GetTable(tableName);
        var row = table.ReadAllRows()[0];
        var before = IndexChecks.AllPages(db);

        Assert.Throws<NotSupportedException>(() => table.Insert(new Row { ["name"] = "another" }));
        Assert.Throws<NotSupportedException>(() => table.Validate(new Row { ["name"] = "another" }));
        Assert.Throws<NotSupportedException>(() => table.DeleteRow("name", row["name"]!));
        Assert.Throws<NotSupportedException>(() => table.UpdateByPrimaryKey(row["name"]!, new Row { ["name"] = "renamed" }));

        IndexChecks.AssertUnchanged(before, db);
    }
}
