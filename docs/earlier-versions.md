# Files written by earlier versions

What a file can hold that an earlier version of this code wrote and Access would not have: indexes that miss rows,
index entries that point at no row, and a few values and pages laid out otherwise. This page says which files can
have them, how this library reads and changes such a file, and what to do about each.

## Which files

An earlier version, on this page, is the `JackcessDotNet` package up to 1.2.2 - the release of the port this
repository started from - or a build of this repository from before its first release as `Jackcess.Net`. No
release of `Jackcess.Net` writes what this page describes, except long values placed as earlier versions placed
them, when `Database.ShareLongValuePages` asks for that ([Long values](storage.md#long-values)). Later versions of
the `JackcessDotNet` package come from that other project, and what they write was not examined here.

Nothing in a file says that an earlier version wrote to it: whether one did is known only from where the file has
been. A file compacted in Access since (Compact and Repair, or DAO's `DBEngine.CompactDatabase`) has had its
indexes rebuilt, which makes [`VerifyIndexes`](#indexes-that-do-not-hold-every-row) needless; the other sections
say what each of the other differences does, and how to find it where it can be found.

## Indexes that do not hold every row

**`Database.VerifyIndexes`: for files an earlier version wrote to.** Versions of this library before it kept every
index - the `JackcessDotNet` package up to 1.2.2, and builds of this repository before a427b84, of 18 November
2026 - kept a table's primary key and no other index, and kept the primary key under the value a row was *found by*. In a
table Access made with indexes, a row one of them inserted is in no index but the primary key; a row one of them
updated is in each other index where it was before the update; and a row whose primary key one of them changed is
in the key under the value it had. Nothing in a file says that one of them wrote to it - a count of entries does
not (after such an update an index has an entry for every row, by number), nor does the number an index's
definition keeps, which is of keys ever added.

With such a file, and by default, this library does what Access does with it:

- enforcing foreign keys, a row that is not in the index on its foreign key is no one's child - its parent is
  deleted or re-keyed and the row left an orphan;
- a second row with the values of a row a unique index does not hold is added. The next time the file is
  compacted Access cannot build that index, and leaves the table without it, or without its primary key.

**Set `db.VerifyIndexes = true` for a file one of those versions wrote rows into and that has not been compacted
in Access since.** Compacting rebuilds the indexes, and the option is then needless. With it set:

- **The first change that looks for rows through an index reads its table once**, while the database is open, and
  compares the two row by row: every row's key must be in the index, pointing at that row. For a unique index
  that is the first `Insert`, `Update` or `Validate` of a session, which reads the table once for all
  the table's unique indexes together; for a relationship, the first change that keeps it.
- **The rows an index does not hold are kept beside it** while the database is open. A row is compared with them
  as well as with the unique index, so the duplicate is refused - as Access refuses it once the file is
  compacted. An index that lacks rows is not searched for a relationship: its table is read instead, as for a
  table with no index ([What it costs](relationships.md#what-it-costs)), so a change is slower and its outcome
  right. A row this library rewrites gains its entries and a row it deletes is gone, and the index is searched
  once none of them is left.
- **What it costs.** Measured on a table of 1,000,000 rows (30,304 pages) with the file in the operating
  system's cache: with a primary key the first insert read 33,644 pages in half a second and allocated 620 MB in
  passing; with a primary key and a unique index, 36,965 pages in 0.8 to 0.9 seconds and 990 MB. Every insert
  after it, and the first without the option, read 8 and 11 pages in 0.1 and 0.2 milliseconds. For a
  relationship, on a child of 1,000,000 rows and a parent of 200,000, the first cascade delete read 34,860 pages
  in about a second and the deletes after it 65 pages and a millisecond each. Memory is needed in proportion to
  the table's rows while it is read.
- **Nothing is read** for a table with no unique index, for a row with a null in its unique index's columns (it
  is no row's duplicate), or to fill a table made in the same session: it is looked at when it is empty.
- **Every entry seen to point at no row is taken out** when the table's indexes are next changed, whatever the
  key, and an entry seen to be a row's wrong one is moved to the row's key where that key is certain
  ([below](#entries-that-are-no-rows)). A row with no entry at all is not given one: nothing here rebuilds an
  index, and `DBEngine.CompactDatabase` does.

## Entries that are no row's

Those versions also left entries behind ([#36]): one that points where a row was, and one under the value a row
had before its primary key was changed, pointing at the row under the value it has.
Access follows both. Measured with ACE on such a file: a query that comes to the first fails with "Not a valid
bookmark" - for the key, and for any range that passes it - and goes on failing when the row that has the key has
an entry beside it; a query for the second's key is answered with the row that no longer has it, and that row is
not found under the value it has.

- **An entry that points at no row** - a slot deleted or never used, a row number past the page's rows, a page
  that is not one of the table's - is no row's duplicate, and when an entry is added or taken out under a key a
  unique index compares and the tree holds another under that key, it is taken out. A long key an earlier
  version wrote whole is looked for in that form too, and a removal that takes out a leaf's last entry reads the
  next leaf's first, one page more, since the next leaf can begin under the same key.
- **An entry that points at a row of the table is believed**, as Access believes it, whatever this library
  makes of that row's values: a row with its key is refused as a duplicate, and the entry stays. This library's
  key for a row may not be the one Access wrote - a key cut at 510 bytes in an index of several columns other than
  Text and Long, which was not measured - and an entry Access wrote, taken out on that judgement, is a row Access
  can then neither find, update nor delete. A row an index does not hold is a duplicate of nothing, as it is to
  Access, unless `VerifyIndexes` is set.
- **With `VerifyIndexes`**, every entry an index was seen to hold that points at no row goes when the table's
  indexes are next changed, whatever the key; and an entry that points at a row whose key is another, where that
  key is one this library makes as Access does, is that row's wrong one: a row with the entry's key is taken,
  the entry goes, and the row is entered under its own key, so that no row is left without an entry for having
  had a wrong one. What is under that key already is looked at the same way, an entry that is itself a wrong one
  being moved in its turn; beside a row that does have the key the row is entered all the same - the two are
  duplicates whatever the index says. An entry goes and its row's own comes, or neither: a failure part-way is
  undone. An update of the row itself enters it under its new key once: an entry an index holds already is never
  added again. A key is one this library makes as Access does for every type it writes - a Date/Time Extended
  value's made of the bytes its row stores, as Access makes it ([below](#datetime-extended-entries)) - and, cut
  at 510 bytes in an index of several columns, only where those columns are Text and Long, the shapes that were
  measured.
- **A key this library may not make as Access does** - one cut at 510 bytes in an index of several columns other
  than Text and Long - is never looked for through an index, with or without `VerifyIndexes`: the table is read
  for it, so a relationship's child or parent is found however Access keyed it. When a row with such a key is
  deleted or rewritten, its entry is looked for under this library's key, as any row's is, and only when the row has
  no entry there are its entries found by the row they point at - every leaf of the index read - so that none is
  left pointing where the row was.

An index that is as its rows are has none of these entries, and nothing is read to look for one: the pages an
insert reads are the ones it read before. An index that is not unique is not looked at this way without
`VerifyIndexes` - its entries under one key are its rows' - and an entry whose row has gone does no harm to this
library either way: each row found is read and compared.

## Date/Time Extended entries

**A row whose Date/Time Extended value is in another form than the one this library writes** may have its entry
where a version of this library before [#51] put it, when such a version added the index or rewrote the row: under
the key of the date it read, in the first form, or of no value - for Access's second form, before db6f93b, and for
the layout DAO's table-type recordset writes, which every such version read as no value ([#64]). When the row is
deleted or its date rewritten and it has no entry under the key of its bytes, the entry is looked for
under each of those keys, one way down the tree each to the key and the row, and taken out when it points at the
row. A relationship's search for such a row, a unique index's for its duplicate, and a `Joiner`'s by its bytes
look under the first form's key too, and read each row found there - the row looked for only when it has the very
bytes, the others the rows with the date in the first form - and a `DateTime` looked for through an index is
looked for under that key, which is its first form's. No search looks under the key of no value: every row with
no value is under it, as an index that does not ignore nulls holds them (Access's default for a unique index and a
relationship's), and each would be read. So a row the versions from 2930d3f to db6f93b entered there - ten hours
of `main`, never released - and a row in the table-type layout that any of those versions keyed, is found when it
is deleted or its date rewritten, and with `VerifyIndexes`, which reads the table when an index holds a row under
another key than its own; trusting the index, a relationship, a unique index and a `Joiner` do not find it, as
Access does not. Nor does a lookup by a date through an index cursor, which trusts its index whether or not
`VerifyIndexes` is set. With `VerifyIndexes`, in an index it looks at (a unique index, or a relationship's), such an
entry is the row's wrong one, and is moved to the key of the row's bytes, as it is in every index when the row is
rewritten. How Access keys a Date/Time Extended value: [How keys are matched](relationships.md#how-keys-are-matched).

## Other things an earlier version wrote

- **Index keys longer than 510 bytes**, stored whole or cut with a digest that is not Access's:
  [Keys longer than 510 bytes](indexes.md#keys-longer-than-510-bytes).
- **Decimal values in another byte order**, which read as other numbers; `DecimalByteOrder.Find` lists some of
  them: [Decimal](data-types.md#decimal).
- **Currency values rounded half away from zero**, which are consistent with Access and need no mending, but some
  of which are not found by the number they were written from: [Currency](data-types.md#currency).
- **Rows deleted with one flag bit**, which stay deleted for this library and for Access:
  [Rows on a page](storage.md#rows-on-a-page).
- **Tables whose free-space map is empty**: earlier versions left it so, and Access added a page for the first row
  it wrote to a table they had filled. Where a row goes, as the map has it:
  [Where a new row goes](storage.md#where-a-new-row-goes).
- **Long values whose last chunk shares a page with other values**, which Access must not be given to edit, and
  which `Database.ShareLongValuePages` keeps: [Long values](storage.md#long-values).
- **Memo and OLE values kept in the row past Access's 64 bytes**, up to 1,024, which Access shows as `#Error`
  ([What this fork changes](differences.md#what-this-fork-changes)). An update of such a row writes the value
  again, on pages of its own.
- **Text or Binary values longer than their column**, which refuse an update of their row until the update gives
  that column a value that fits:
  [Reading, updating and deleting rows](cursors-and-rows.md#reading-updating-and-deleting-rows).
- **Rows in Access 97 files** that Access 97's format does not have:
  [Access 97 files are read-only](file-formats.md#access-97-files-are-read-only).
- **Tables without properties**, so with no Required or Allow Zero Length to hold rows to:
  [Required and Allow Zero Length](properties.md#required-and-allow-zero-length).

## Test files

`tests/JackcessDotNet.Tests/Assets/engine-3ad3aed-rows-in-access-tables.mdb` is a database Access made and this
library then wrote to at 3ad3aed, before it kept every index, made by `tools/legacy-incomplete-indexes.ps1`.
`engine-3ad3aed-rows-in-unique-indexes.mdb` beside it is the same for unique indexes and a primary key, made by
`tools/legacy-unique-indexes.ps1`. `ace-extended-date-forms.accdb` holds Date/Time Extended values in every form
Access reads a date from, with indexes ACE made over them (`tools/ace-extended-date-forms.ps1`);
`engine-913240c-extended-date-keys.accdb` and `engine-c89bfe4-extended-date-keys.accdb` are copies of it that
this library changed at those commits (`tools/earlier-extended-date-keys.ps1`). `ace-extended-date-table-type.accdb`
holds values in the layout DAO's table-type recordset writes, beside the other two, keyed by ACE as DAO added them
(`tools/ace-extended-date-table-type.ps1`), and `engine-c89bfe4-table-type-keys.accdb` is a copy c89bfe4 changed
(`tools/earlier-extended-date-table-type-keys.ps1`). The files of long keys are listed under
[Keys longer than 510 bytes](indexes.md#keys-longer-than-510-bytes).

[Back to contents](README.md)

[#36]: https://github.com/markmario/jackcess-dotnet/issues/36
[#51]: https://github.com/markmario/jackcess-dotnet/issues/51
[#64]: https://github.com/markmario/jackcess-dotnet/issues/64
