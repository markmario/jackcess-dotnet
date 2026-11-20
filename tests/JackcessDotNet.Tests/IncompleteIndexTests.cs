using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// <see cref="Database.EnforceForeignKeys"/> over a table whose index does not hold every row.
/// Versions of this library before it kept every index added a row to its table's
/// primary key and to no other index, and rewrote or deleted one leaving its entries elsewhere where they were: a
/// row they inserted or updated is not found through the index on its foreign key. With
/// <see cref="Database.VerifyIndexes"/>, which every database here is opened with, enforcement finds rows through
/// an index only once it has seen, in this session, that the index holds every row; until then, and for an index
/// that does not, it reads the table. (Without it an index is believed, as Access believes one:
/// <see cref="TrustedIndexTests"/>.)
/// <para>
/// The rows are left out here by a helper in this class, not by the earlier code: a row is written as this version
/// writes it and its entries are then taken out of every index but the primary key, with the library's own index
/// writer, which is what the earlier code left. LegacyIncompleteIndexTests has a file the earlier
/// code itself wrote to.
/// </para>
/// </summary>
public sealed class IncompleteIndexTests : IDisposable
{
    private const int CascadeUpdates = 0x100;
    private const int CascadeDeletes = 0x1000;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"fki_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ColumnDefinition Long(string name) => new ColumnDefinition(name, DataType.Int32);
    private static ColumnDefinition Text(string name, int chars = 20) => new ColumnDefinition(name, DataType.Text) { MaxLength = chars };

    /// <summary>Declares a relationship as Access records one: a row of MSysRelationships for each pair of columns.</summary>
    private static void Relate(Database db, string name, string parent, string child, int flags, string parentColumn, string childColumn)
        => db.GetTable("MSysRelationships").Insert(new Row
        {
            ["ccolumn"] = 1, ["grbit"] = flags, ["icolumn"] = 0,
            ["szColumn"] = childColumn, ["szObject"] = child,
            ["szReferencedColumn"] = parentColumn, ["szReferencedObject"] = parent,
            ["szRelationship"] = name,
        });

    /// <summary>
    /// Parent(K, Name) and Child(Id, K, Name) with an index on Child.K, as Access gives a relationship: parents 1, 2
    /// and 3, each with two children Access might have added - and child 5, of parent 1, added as an earlier
    /// release added a row.
    /// </summary>
    private void ParentAndChild(int flags)
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var parent = db.CreateTable("Parent", new[] { Long("K"), Text("Name") }, primaryKey: "K");
        var child  = db.CreateTable("Child", new[] { Long("Id"), Long("K"), Text("Name") }, new[] { "Id" },
                                    new[] { new IndexDefinition("ChildK", "K") });
        Relate(db, "ParentChild", "Parent", "Child", flags, "K", "K");
        foreach (int k in new[] { 1, 2, 3 })
        {
            parent.Insert(new Row { ["K"] = k, ["Name"] = "p" + k });
            child.Insert(new Row { ["Id"] = k * 10, ["K"] = k, ["Name"] = "c" + k * 10 });
            child.Insert(new Row { ["Id"] = k * 10 + 1, ["K"] = k, ["Name"] = "c" + (k * 10 + 1) });
        }
        InsertAsEarlierReleasesDid(child, "Id", new Row { ["Id"] = 5, ["K"] = 1, ["Name"] = "c5" });
    }

    /// <summary>
    /// Adds <paramref name="row"/> as versions of this library before it kept every index did: the row, the row
    /// count and the row's primary-key entry, and no entry in any other index. The row is inserted and its other
    /// entries are then removed from their index pages.
    /// </summary>
    internal static void InsertAsEarlierReleasesDid(Table table, string primaryKey, Row row)
    {
        table.Insert(row);
        var others = table.IndexEntries.EntriesFor(row, enforce: false).Where(entry => !entry.Tree.IsPrimaryKey).ToList();
        table.IndexEntries.Remove(others, PointerOf(table, primaryKey, row[primaryKey]!));
    }

    /// <summary>
    /// Updates a row as those versions did: the row is rewritten and its primary-key entry follows it, and in every
    /// other index the entry it had stays where it was, under the old values, and it gains none.
    /// </summary>
    private static void UpdateAsEarlierReleasesDid(Table table, string primaryKey, object key, Row newValues)
    {
        var indexes = table.IndexEntries;
        Row before = table.Rows.Single(row => Equals(row[primaryKey], key));
        int was = PointerOf(table, primaryKey, key);

        table.UpdateByPrimaryKey(key, newValues);

        Row after = table.Rows.Single(row => Equals(row[primaryKey], key));
        indexes.Remove(indexes.EntriesFor(after, enforce: false).Where(entry => !entry.Tree.IsPrimaryKey).ToList(), PointerOf(table, primaryKey, key));
        indexes.Add(indexes.EntriesFor(before, enforce: false).Where(entry => !entry.Tree.IsPrimaryKey).ToList(), was);
    }

    private static int PointerOf(Table table, string column, object value)
    {
        var cursor = table.CreateCursor();
        while (cursor.MoveNext())
            if (Equals(cursor.Current![column], value)) return cursor.CurrentRowPointer;
        throw new InvalidOperationException($"No row of {table.Name} has {column} = {value}.");
    }

    /// <summary>The number of entries in the index, counted on its leaf pages.</summary>
    private static int EntriesIn(Database db, Table table, string index)
    {
        int root = table.Indexes.Single(ix => ix.Name == index).RootPageNumber;
        return new IndexWriter(db.File, new PageAllocator(db.File)).EntriesWithPrefix(root, Array.Empty<byte>()).Count();
    }

    private Database OpenEnforcing()
    {
        var db = Database.Open(_path);
        db.EnforceForeignKeys = true;
        db.VerifyIndexes = true;
        return db;
    }

    /// <summary>A table's rows as text, in a fixed order: "Id=10, K=1, Name=c10".</summary>
    private List<string> Rows(string table)
    {
        using var db = Database.Open(_path);
        return db.GetTable(table).Rows.ToList()
            .Select(row => string.Join(", ", row.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}")))
            .OrderBy(line => line, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Runs <paramref name="change"/> with enforcement on, expecting it refused with not a byte of the file changed.</summary>
    private string Refused(Action<Database> change)
    {
        byte[] before = File.ReadAllBytes(_path);
        string message;
        using (var db = OpenEnforcing())
            message = Assert.Throws<ConstraintViolationException>(() => change(db)).Message;
        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(_path)), "a refused change altered the file");
        return message;
    }

    private void Enforcing(Action<Database> change)
    {
        using var db = OpenEnforcing();
        change(db);
    }

    // ── A child the index on its foreign key does not hold ───────────────────

    [Fact]
    public void the_helper_leaves_a_row_in_its_table_and_its_primary_key_and_out_of_the_index_on_its_foreign_key()
    {
        ParentAndChild(CascadeDeletes);

        using var db = Database.Open(_path);
        var child = db.GetTable("Child");
        Assert.Equal(7, child.RowCount);
        Assert.Equal(7, child.Rows.Count());
        Assert.Equal(7, EntriesIn(db, child, "PrimaryKey"));
        Assert.Equal(6, EntriesIn(db, child, "ChildK"));
        Assert.NotNull(child.FindRowByPrimaryKey(5));
    }

    [Fact]
    public void a_cascade_delete_takes_a_child_an_earlier_release_left_out_of_the_index_on_its_foreign_key()
    {
        ParentAndChild(CascadeDeletes);

        Enforcing(db => db.GetTable("Parent").DeleteRow("K", 1));

        Assert.Equal(new[] { "K=2, Name=p2", "K=3, Name=p3" }, Rows("Parent"));
        Assert.Equal(new[] { "Id=20, K=2, Name=c20", "Id=21, K=2, Name=c21", "Id=30, K=3, Name=c30", "Id=31, K=3, Name=c31" }, Rows("Child"));
    }

    [Fact]
    public void a_delete_is_refused_while_only_a_child_left_out_of_the_index_refers_to_the_parent()
    {
        ParentAndChild(flags: 0);
        using (var db = Database.Open(_path))
        {
            // The children the index does hold go; child 5 is the only row left that refers to parent 1.
            db.GetTable("Child").DeleteRow("Id", 10);
            db.GetTable("Child").DeleteRow("Id", 11);
        }

        string message = Refused(db => db.GetTable("Parent").DeleteRow("K", 1));

        Assert.Contains("Foreign-key violation", message);
        Assert.Contains("K=1, Name=p1", Rows("Parent"));
    }

    [Fact]
    public void a_cascade_update_reaches_a_child_left_out_of_the_index_and_gives_it_its_entry()
    {
        ParentAndChild(CascadeUpdates);

        Enforcing(db => db.GetTable("Parent").UpdateByPrimaryKey(1, new Row { ["K"] = 7 }));

        Assert.Equal(new[] { "Id=10, K=7, Name=c10", "Id=11, K=7, Name=c11", "Id=5, K=7, Name=c5" },
                     Rows("Child").Where(row => row.Contains("K=7")).OrderBy(row => row, StringComparer.Ordinal));
        Assert.DoesNotContain(Rows("Child"), row => row.Contains("K=1,"));

        // Rewritten, the row has the entry any rewritten row has: the index now holds every row.
        using var read = Database.Open(_path);
        IndexChecks.AssertEveryIndexHoldsTheRows(read, "Child");
    }

    [Fact]
    public void a_parent_without_children_in_the_index_or_out_of_it_is_still_deleted_and_re_keyed()
    {
        ParentAndChild(flags: 0);
        using (var db = Database.Open(_path))
            db.GetTable("Parent").Insert(new Row { ["K"] = 4, ["Name"] = "p4" });

        Enforcing(db =>
        {
            db.GetTable("Parent").UpdateByPrimaryKey(4, new Row { ["K"] = 8 });
            db.GetTable("Parent").DeleteRow("K", 8);
        });

        Assert.Equal(new[] { "K=1, Name=p1", "K=2, Name=p2", "K=3, Name=p3" }, Rows("Parent"));
    }

    /// <summary>
    /// Counting entries would not have done. An update by an earlier release left the row's old entry behind and
    /// gave the rewritten row none: the index has an entry for every row of the table, by number, and still does
    /// not lead to the row. So an index is checked row by row, each row's key against the entry that points at it.
    /// </summary>
    [Fact]
    public void a_child_an_earlier_release_re_keyed_is_found_though_the_index_has_an_entry_for_every_row_by_count()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var parent = db.CreateTable("Parent", new[] { Long("K"), Text("Name") }, primaryKey: "K");
            var child  = db.CreateTable("Child", new[] { Long("Id"), Long("K"), Text("Name") }, new[] { "Id" },
                                        new[] { new IndexDefinition("ChildK", "K") });
            Relate(db, "ParentChild", "Parent", "Child", CascadeDeletes, "K", "K");
            parent.Insert(new Row { ["K"] = 1, ["Name"] = "p1" });
            parent.Insert(new Row { ["K"] = 2, ["Name"] = "p2" });
            child.Insert(new Row { ["Id"] = 10, ["K"] = 1, ["Name"] = "c10" });
            child.Insert(new Row { ["Id"] = 20, ["K"] = 2, ["Name"] = "c20" });
            UpdateAsEarlierReleasesDid(child, "Id", 20, new Row { ["K"] = 1 });

            Assert.Equal(child.RowCount, EntriesIn(db, child, "ChildK"));
        }

        Enforcing(db => db.GetTable("Parent").DeleteRow("K", 1));

        Assert.Empty(Rows("Child"));
    }

    // ── A parent the index on its key does not hold ──────────────────────────

    /// <summary>Parent(Id, Code), Code in a unique index a relationship refers to; parent 2 added as an earlier release added a row.</summary>
    private void ParentKeyedByAUniqueIndex()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var parent = db.CreateTable("Parent", new[] { Long("Id"), Long("Code") }, new[] { "Id" },
                                    new[] { new IndexDefinition("ParentCode", "Code") { IsUnique = true } });
        db.CreateTable("Child", new[] { Long("Id"), Long("Code") }, new[] { "Id" }, new[] { new IndexDefinition("ChildCode", "Code") });
        Relate(db, "ParentChild", "Parent", "Child", 0, "Code", "Code");
        parent.Insert(new Row { ["Id"] = 1, ["Code"] = 100 });
        InsertAsEarlierReleasesDid(parent, "Id", new Row { ["Id"] = 2, ["Code"] = 200 });
    }

    [Fact]
    public void an_insert_finds_a_parent_an_earlier_release_left_out_of_the_index_on_its_key()
    {
        ParentKeyedByAUniqueIndex();

        Enforcing(db => db.GetTable("Child").Insert(new Row { ["Id"] = 1, ["Code"] = 200 }));
        Enforcing(db => db.GetTable("Child").Insert(new Row { ["Id"] = 2, ["Code"] = 100 }));
        Assert.Equal(new[] { "Code=100, Id=2", "Code=200, Id=1" }, Rows("Child"));

        string message = Refused(db => db.GetTable("Child").Insert(new Row { ["Id"] = 3, ["Code"] = 300 }));
        Assert.Contains("Foreign-key violation", message);
    }

    [Fact]
    public void an_update_of_a_foreign_key_finds_a_parent_left_out_of_the_index_on_its_key()
    {
        ParentKeyedByAUniqueIndex();
        using (var db = Database.Open(_path))
            db.GetTable("Child").Insert(new Row { ["Id"] = 1, ["Code"] = 100 });

        Enforcing(db => db.GetTable("Child").UpdateByPrimaryKey(1, new Row { ["Code"] = 200 }));
        Assert.Equal(new[] { "Code=200, Id=1" }, Rows("Child"));

        Refused(db => db.GetTable("Child").UpdateByPrimaryKey(1, new Row { ["Code"] = 300 }));
    }

    /// <summary>
    /// A change that enforcement works out looks for a duplicate in a unique index among the rows as the change
    /// leaves them, through that index. A row the index does not hold is a duplicate all the same.
    /// </summary>
    [Fact]
    public void a_duplicate_of_a_row_left_out_of_a_unique_index_is_refused()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var parent = db.CreateTable("Parent", new[] { Long("K") }, primaryKey: "K");
            var child  = db.CreateTable("Child", new[] { Long("Id"), Long("K") }, new[] { "Id" },
                                        new[] { new IndexDefinition("OneEach", "K") { IsUnique = true } });
            Relate(db, "ParentChild", "Parent", "Child", 0, "K", "K");
            parent.Insert(new Row { ["K"] = 1 });
            parent.Insert(new Row { ["K"] = 2 });
            child.Insert(new Row { ["Id"] = 10, ["K"] = 1 });
            InsertAsEarlierReleasesDid(child, "Id", new Row { ["Id"] = 20, ["K"] = 2 });
        }

        string message = Refused(db => db.GetTable("Child").UpdateByPrimaryKey(10, new Row { ["K"] = 2 }));

        Assert.Contains("duplicate", message);
    }

    // ── An index that holds every row ────────────────────────────────────────

    /// <summary>
    /// An index is checked the first time a change looks for rows through it - the table is read, and the index's
    /// leaf pages - and not again while the database is open: the changes after it read the rows they reach and the
    /// index pages that lead to them, whichever <see cref="Table"/> they are made through.
    /// </summary>
    [Fact]
    public void an_index_that_holds_every_row_is_checked_once_and_then_used_without_reading_the_table()
    {
        const int Parents = 2_000, ChildrenEach = 5;
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var parent = db.CreateTable("Parent", new[] { Long("K"), Text("Name") }, primaryKey: "K");
            var child  = db.CreateTable("Child", new[] { Long("Id"), Long("K"), Text("Name", 100) }, new[] { "Id" },
                                        new[] { new IndexDefinition("ChildK", "K") });
            Relate(db, "ParentChild", "Parent", "Child", CascadeDeletes, "K", "K");
            for (int k = 1; k <= Parents; k++)
                parent.Insert(new Row { ["K"] = k, ["Name"] = "p" + k });
            for (int i = 0; i < Parents * ChildrenEach; i++)
                child.Insert(new Row { ["Id"] = i, ["K"] = 1 + i % Parents, ["Name"] = new string('c', 100) });
        }

        using (var db = OpenEnforcing())
        {
            var definition = db.GetTable("Child").Definition;
            int childPages = UsageMap.GetOwnedPages(db.File.ReadPage(definition.UmapPageNumber), definition.OwnedPagesRow,
                                                    db.File.Format, db.File).Count;
            Assert.True(childPages > 250, $"the child is only {childPages} pages");

            long ReadsOfDeleting(int k)
            {
                long before = db.File.ReadCount;
                db.GetTable("Parent").DeleteRow("K", k);
                return db.File.ReadCount - before;
            }

            long first = ReadsOfDeleting(2), second = ReadsOfDeleting(3), third = ReadsOfDeleting(4);

            Assert.True(first >= childPages, $"the first change read {first} pages: fewer than the child's {childPages}, so its index was not checked");
            Assert.True(second < childPages / 4 && third < childPages / 4,
                $"once checked, a cascade delete of {ChildrenEach} rows read {second} and then {third} pages ({childPages} pages in the child)");
            Assert.Equal((Parents - 3) * ChildrenEach, db.GetTable("Child").RowCount);
        }

        using var read = Database.Open(_path);
        IndexChecks.AssertEveryIndexHoldsTheRows(read, "Child");
    }

    /// <summary>
    /// The check must not find fault with an index that has none, or every change to its table would read the
    /// table: each index in the corpus whose keys this library makes - 1,652 of them, in files Access wrote - is
    /// seen to hold every row. The one exception has a long key Jackcess wrote whole, before it cut keys as
    /// Access does: a search for that row's value does not find it through the index, which is what the check is
    /// there to say.
    /// </summary>
    [Fact]
    public void every_index_in_the_corpus_is_seen_to_hold_every_row()
    {
        var wanting = new List<string>();
        int whole = 0;
        foreach (string file in TestCorpus.Files("*.*", "V2000", "V2003", "V2007", "V2010", "V2019"))
        {
            Database db;
            try { db = Database.Open(file); }
            catch (Exception e) when (e is NotSupportedException or InvalidDataException or InvalidOperationException) { continue; }   // not one this library opens
            using (db)
            {
                db.VerifyIndexes = true;
                foreach (string name in db.GetTableNames().Concat(db.GetSystemTableNames()))
                {
                    var table = db.GetTable(name);
                    if (table.Definition.HasUnreadIndexes) continue;
                    foreach (var tree in table.IndexEntries.Trees)
                    {
                        if (tree.Target.RootPage <= 0 || !tree.Columns.All(c => IndexKeys.CanEncode(c.Column))) continue;
                        if (table.IndexEntries.HoldsEveryRow(tree)) whole++;
                        else wanting.Add($"{Path.GetFileName(file)}: {name}.{tree.Name}");
                    }
                }
            }
        }

        Assert.Equal(new[] { "unicodeCompV2003.mdb: Table.Unicode" }, wanting);
        Assert.True(whole >= 1_600, $"only {whole} indexes were looked at");
    }

    /// <summary>
    /// An index found wanting is not searched while a row it does not hold remains, and what the session itself
    /// writes is right in it: the next change is as right as the first.
    /// </summary>
    [Fact]
    public void changes_after_the_first_go_on_reading_a_table_whose_index_does_not_hold_every_row()
    {
        ParentAndChild(CascadeDeletes);
        using (var db = Database.Open(_path))
            InsertAsEarlierReleasesDid(db.GetTable("Child"), "Id", new Row { ["Id"] = 6, ["K"] = 2, ["Name"] = "c6" });

        Enforcing(db =>
        {
            db.GetTable("Parent").DeleteRow("K", 3);
            db.GetTable("Parent").DeleteRow("K", 2);
            db.GetTable("Parent").DeleteRow("K", 1);
        });

        Assert.Empty(Rows("Parent"));
        Assert.Empty(Rows("Child"));
    }
}
