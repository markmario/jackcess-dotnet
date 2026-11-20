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
