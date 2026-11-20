# Indexes and keys

Which indexes are kept, and when one is trusted; the keys Access writes for text in each sort order, which ones
this library makes and which it refuses; and what happens to a key longer than the 510 bytes an index entry
holds. Adding an index or a primary key to a table is in
[Tables and schema changes](tables-and-schema.md#changing-a-table-that-has-rows), and how a lookup uses an index
in [Rows and cursors](cursors-and-rows.md#index-cursors-and-joins).

## Indexes kept on every write

Every index of a table is kept on every write, not the primary key alone, and unique indexes are enforced: Access
answers through them after this library adds, changes or deletes rows. Indexes are made with a table or added to
one that has rows, and their keys are written for Byte, Integer, Long, Large Number, Currency, Single, Double,
Date/Time, Date/Time Extended, Yes/No, Text and Memo (in the sort orders below) and GUID columns.

Text keys are written in the General - Legacy and General orders, and in the orders of the locales whose keys are
General - Legacy's. The engine refuses, before changing anything, to write to a table with an index whose keys
it cannot write: text in any other sort order, or a column type it makes no key for (a Decimal, Binary, OLE or
complex column). It still reads such a table. Whether a table takes rows can be asked first, with
`Table.InsertRefusal` ([When a change is refused](cursors-and-rows.md#when-a-change-is-refused)).

## Indexes built page after page

A row's entry goes into each index of its table as the row is inserted: the tree is read from its root to the
leaf the entry belongs in, the leaf written, and split when it is full. For a table of 35 indexes that is several
page reads and writes for each row and each index. Access does not build an index that way when it compacts a
file: it sorts the keys and writes the tree page after page. This library does the same wherever a tree begins
empty:

- **`Table.AddIndex`** builds the new tree from the table's rows, sorted.
- **`Table.InsertRange` and `Table.BulkInsert` into a table with no rows**, whose indexes hold no entry, put the
  rows in with no index entries and then build every tree from the table's rows, inside the batch's undo scope.
  Not when a relationship the table is in is enforced, or a cursor over its indexes is open: then each row is
  entered as it goes in. A table that has rows is always filled row by row.

Each leaf takes entries until the next does not fit it; each node above holds a copy of the last entry under each
child but its last, and its tail leads to the last; every level is chained, and every page but the root - which
stays on its page - goes into the index's usage map: the rules a row's insert keeps, so Access reads and adds to
the tree, and so does this library. A tree holds the same entries in the same order either way.

What changes while a batch goes into an empty table: a duplicate in a unique index or primary key is found once
every row is in, not at the row. The row named is the one row by row would name - the earliest of the batch that
duplicates a row before it, in any unique index or the primary key - with the value, and nothing is left in the
table; but a row refused for something else, a value too long, is named ahead of a duplicate at an earlier row,
as every row is in before any duplicate is looked for. A failure while the trees are built that is no row's - the
file or the sort's temporary folder could not be written - is thrown as itself, an `IOException`, with nothing left; and until the batch is in, the table's indexes hold none of its rows, so a sequence of rows
that looks in the table through an index as it is gone through does not find them.

**What it costs.** An export of 13 rows a document into a template of four tables and 35 indexes (ExportMdb's,
measured with its `ExportCostTests`): kept row by row, 11.5 ms a document at 2,000 documents and growing with the
export; built once its rows are in, 0.65 ms at 10,000 documents, 0.67 at 50,000 and 0.68 at 200,000, against
0.51 to 0.56 ms for the same rows with no index. **Memory**: one tree's keys are sorted at a time, up to 64 MB of
them in memory and the rest in sorted runs in temporary files (deleted when the build ends); as the tree is
written, the last entry of each of its pages is held too, for the levels above - about 75 bytes a row with keys of
510 bytes. Measured live with such keys: 34 MB at 50,000 rows, 76 MB at 150,000, 91 MB at 450,000. The rows
themselves are not held by `BulkInsert`. An index adds to the file what its keys take: 22% for that export, 77% when every row has a text
key of 510 bytes ([How a file grows](storage.md#how-a-file-grows)).

## The count of an index's keys

Access keeps in a table's definition, for each tree, a count of keys (Java Jackcess's `uniqueEntryCount`): a
statistic it plans a query by - an index whose count is 0 is one it takes to be empty - which no reader checks
against the tree. This library writes, for a tree built whole, the number of its keys, and adds one for each row
inserted under a key no other row had, in the write of the table's definition the insert makes anyway; an update
or a delete changes nothing. Beside ACE's twin of an export the counts are equal in every index (ExportMdb.Interop's
`IndexedExportTwinAuditTests`). Access's own updates and deletes move it in ways not fully measured: after ACE
deleted rows of a table, one count went from 5,000 to 2,833 with 4,333 rows left. In another, of 3,000 rows whose
trees this library built whole, each count was the one Access gives the tree it builds again when DAO compacts the
file (`BulkIndexesInAccessTests`); then ACE's delete of one row took no key off, its update to a new value added
none, and an insert it refused as a duplicate added one to the primary key's.

## An index is trusted

An index is trusted to hold every row of its table, as Access and Java Jackcess trust one: rows are found through
it, and a unique index that has no entry for a value is taken to mean no row has it. Nothing is read to check. A
file an earlier version of this library wrote rows into can have indexes that do not hold every row, and entries
that are no row's; what to do with such a file is in
[Files written by earlier versions](earlier-versions.md#indexes-that-do-not-hold-every-row).

## Text index keys: sort orders and limits

An index entry's key for text depends on the column's sort order: a code, which
is a locale's LCID, and in an Access 2000 or later file a version. The library
makes the keys of the orders below, each checked byte for byte against the keys
Microsoft's own engine writes.

### Written, and searched by

- **General - Legacy** (sort order 1033, version 0): Access 2000 to 2007, and
  what a new database gets by default in later versions.
- **General** (1033, version 1): what Access 2010 and later can make a new
  database in. A table created in such a file sorts as the file does.
- **The orders of nine other locales, version 0**, whose keys are those of
  General - Legacy: Arabic (1025), German (1031), Greek (1032), Hebrew (1037),
  Italian (1040), Dutch (1043), Russian (1049, DAO's `dbLangCyrillic`), British
  English (2057) and Portuguese (2070). A database made in such a locale - by
  DAO's `CreateDatabase` with that locale, or by Access with it as the "New
  database sort order" - gives every Text and Memo column the locale's LCID in
  place of 1033. ACE 16 was had to index 1,013,302 values in a database of each
  (`tools/ace-text-keys.ps1 -Locale`): every character of the BMP alone and
  between two letters, every pair of letters of Latin-1 and Latin Extended-A and
  of characters within the Greek, Cyrillic, Hebrew, Arabic and Thai blocks,
  letters with each combining mark, runs of ASCII, 500,000 values of two to
  eight characters, and the values of the General - Legacy golden files. Every
  key is the one ACE writes for the value in a database of locale 1033. A table
  created in such a file sorts as the file does.

### Searched by, in a file that is only read

- **Access 97's General order** (1033 in an Access 97 file). An Access 97 file
  is not written to at all (see
  [Access 97 files are read-only](file-formats.md#access-97-files-are-read-only)); a row is
  found through a text index of one instead of by reading its table. The codes
  are Jackcess's `General97IndexCodes`, with what ACE writes otherwise: a
  character keyed as two letters (Œ, Æ, ß) takes a place for each among the
  accents. Access 97 keeps text in code page 1252, and a value with a character
  that code page has not, or whose key is longer than the 255 bytes an entry
  there holds, is looked for by reading the table. The keys are those the 32-bit
  ACE 12.0 and Jet 4.0 write in an Access 97 file (ACE 16 opens none): every
  character of the code page alone and in every pair, and 40,000 longer values,
  were compared, and every text key in the nine files Access 97 itself wrote, in
  the corpus, is the key made of its row's values.

### Refused

Refused, rather than write a key Access cannot find:

- **Any other sort order.** ACE's keys were measured to differ from General -
  Legacy's for Chinese (1028, 2052), Czech, Danish and Norwegian, French,
  Hungarian, Icelandic, Japanese, Korean, Lithuanian, Polish, Romanian, Slovak,
  Slovenian, Croatian, Spanish (1034, 3082), Swedish and Finnish, Thai, Turkish,
  Ukrainian and Vietnamese - a few letters in most, and in Thai and Ukrainian
  only among other characters, which is why a locale that was not measured is
  refused too. The version 1 order of a locale other than 1033, which Access
  2010 and later can make a database in and DAO cannot, was not measured, nor
  were Access 97's other orders. A file whose catalog sorts in such an order is
  not given a table, and a table with an index on a Text or Memo column that
  sorts in one is not inserted into, updated or deleted from. Such a file is
  still read; its text indexes are not searched, its tables are scanned. Jackcess
  writes none of them either.
- **A value in an indexed Text or Memo column, or a table name, with a
  character Access keys by the character before it.** These are the Japanese length and iteration marks (ー U+30FC,
  々 U+3005, ゝ U+309D, ゞ U+309E, ヽ U+30FD, ヾ U+30FE, 〱 U+3031, 〲 U+3032,
  ｰ U+FF70; in General also U+3033-3035, 〻 U+303B, ゠ U+30A0 and ꀕ U+A015) and,
  in General - Legacy, the Arabic shadda (U+0651, U+FE7C, U+FE7D). So a Japanese
  name with ー (データ, コンピューター) or an Arabic word with a shadda cannot be a
  value of an indexed column (the primary key or any other index) or a table name: `Insert`, `Update`,
  `Delete` and `CreateTable` throw `NotSupportedException` before anything
  is written. In General the same goes for four Arabic ligatures (U+06D6,
  U+06D7, U+FD24, U+FD6F). Any text is fine in a column that is no key, and a
  row Access wrote with such a key is still found: the index's rows are gone through for it.

## Keys longer than 510 bytes

Access keeps no index key longer than 510 bytes. It cuts a longer one there
and ends it in a two-byte digest of every byte it cut off (their remainder by
the CRC-16 polynomial 0x8005) - a key of one column, of a Memo (its first 255
characters) or of several columns together. A file can hold four kinds of
entry for such a key:

| Written by | Stored as | Read / lookup | Insert, update | Delete |
| --- | --- | --- | --- | --- |
| Access (Jet 4.0, ACE 12, ACE 16 alike) | cut, with the digest | found | a duplicate in a unique index is refused | entry removed |
| This library | the same bytes as Access | found | the same | entry removed |
| This library from when it cut keys until [#56] (never released), and the versions of Jackcess that cut keys | cut, with a digest that leaves the last byte out: Access's for a key that ends in text, **another** for a key of several columns that ends in a number, a date or the like after text that filled the entry - Access's key of the same values with the last byte 0 | found | seen by a unique index when the row it points at has the value; a rewritten row's entry is removed and Access's written | entry removed, under the key it has |
| This library before that (the `JackcessDotNet` package up to 1.2.2 among its versions), and Jackcess before 5.2.0 | **whole**, over 510 bytes | found | seen by a unique index, so a duplicate is refused; a rewritten row's whole entry is removed and a cut one written | entry removed, under the whole key |

- **Lookups** of a value whose key is cut do not search the index: an index
  cursor goes through the index's rows, comparing each, and any other lookup
  reads the table, so a row is found whichever way its entry is stored. A
  range, or the closest row to such a value, is refused.
- **Writes** always store Access's cut key. Updating a row whose entry was
  whole, or cut with the earlier digest, replaces it with Access's; nothing
  converts rows that are not written, except that with
  `Database.VerifyIndexes` an index that is looked at (a unique one, or a
  relationship's) of Text and Long columns has entries cut with the earlier
  digest replaced when its table is next changed.
- **Uniqueness** follows Access: two values cut to the same entry are
  duplicates, whichever way the existing one is stored. A file an earlier
  version wrote may hold two such rows (their whole keys differed); neither
  can then be rewritten until the other is deleted. A key cut with the earlier
  digest is Access's key of another value - with the same text, (A, 1)'s and
  (A, 255)'s are (A, 0)'s - so an entry found under it alone is a duplicate only
  when the row it points at has the new row's value: the row is read.
- **Deletes and updates** remove the entry the row actually has, cut either
  way or whole, matched by its row. A row with no entry at all - earlier
  versions kept only the primary key - has nothing to remove, and that is not
  an error.
- Access itself does not seek a whole key: until such a row is rewritten,
  Access finds it by a scan but not through the index. Nor does it find, update
  or delete a row whose entry is cut with the earlier digest ("The search key
  was not found in any record") until the row is rewritten here or the file is
  compacted in Access.
- ACE's SQL does not find a row by `=` through an index whose key for the value
  is cut - a row Access wrote itself no more than one this library wrote - where
  DAO's `Seek` through the same index does.

`tests/JackcessDotNet.Tests/Assets/engine-696ca52-long-keys.mdb` is a file this
library wrote at 696ca52, when it wrote long keys whole, made by
`tools/legacy-long-keys.ps1`, and `engine-913240c-cut-keys.mdb` one this library
wrote with the earlier digest, made by `tools/earlier-cut-keys.ps1`.
`IndexKeys/cut-keys-*.tsv` hold the entries ACE wrote for keys of several columns,
made by `tools/ace-cut-keys.ps1`.

[Back to contents](README.md)

[#56]: https://github.com/markmario/jackcess-dotnet/issues/56
