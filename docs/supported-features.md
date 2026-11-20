# What is and is not supported

A checklist of what the library does, each with the page that has the detail; the limits to know about; and what
it does not do. Work not yet done is tracked in this repository's
[issues](https://github.com/markmario/jackcess-dotnet/issues).

## Status

| Feature | Status | See |
| --- | --- | --- |
| Read Jet 3 (Access 97) / Jet 4 (Access 2000–2003) | ✅ | [File formats](file-formats.md) |
| Read ACE 12 / 14 / 16 / 17 (`.accdb`) | ✅ | [File formats](file-formats.md) |
| Create new `.mdb` files (Jet 4) | ✅ | [Creating a file](file-formats.md#creating-a-file) |
| Create new `.accdb` files (Access 2007, 2010, 2016, 2019) | ✅ `FileFormat.Access2007`, `Access2010`, `Access2016`, `Access2019` | [Creating a file](file-formats.md#creating-a-file) |
| Large Number (`DataType.Int64`) and Date/Time Extended (`DataType.DateTimeExtended`) columns | ✅ Read, written, created and indexed; `.accdb` only | [Data types](data-types.md#large-number-and-datetime-extended) |
| Create or write Jet 3 (Access 97) files | ❌ Read-only, as in Jackcess | [Access 97 files are read-only](file-formats.md#access-97-files-are-read-only) |
| Open a file only to read it (`DatabaseOpenOptions.ReadOnly`), while Access has it open | ✅ Nothing written; every change refused | [Sharing a file with Access](sharing.md#opening-a-file-to-write-or-only-to-read) |
| Keep Access and other writers out while a file is open to be written | ✅ By the file's sharing; Access's lock file is neither made nor read | [Sharing a file with Access](sharing.md#what-each-open-does) |
| Row CRUD + primary-key B-tree index | ✅ | [Rows and cursors](cursors-and-rows.md) |
| Update or delete a row by where it was read (`Table.Update`, `Table.Delete`), whatever key the table has | ✅ In its place, as Access updates one: a row's id does not change | [Reading, updating and deleting rows](cursors-and-rows.md#reading-updating-and-deleting-rows) |
| Batch insert, all rows or none (`Table.InsertRange`) | ✅ | [Reading, updating and deleting rows](cursors-and-rows.md#reading-updating-and-deleting-rows) |
| Cursors: both ways, find first and next, update or delete the current row, one column, some columns, savepoints | ✅ No column matchers | [Cursors](cursors-and-rows.md#cursors) |
| Index cursors: the index's order, ranges between two entries, closest row, rows of an entry; `Joiner` | ✅ For every type an index is kept for | [Index cursors and joins](cursors-and-rows.md#index-cursors-and-joins) |
| Secondary indexes: created with a table, kept on every write, unique enforced | ✅ Byte, Integer, Long, Large Number, Currency, Single, Double, Date/Time, Date/Time Extended, Yes/No, Text and Memo (General - Legacy and General), GUID | [Indexes and keys](indexes.md) |
| Memo / OLE long values | ✅ | [Long values](storage.md#long-values) |
| PropertyMap & MSysRelationships | ✅ | [Properties](properties.md), [Relationships](relationships.md) |
| Properties written: a table's, a column's, the database's own, Summary and Custom | ✅ Summary and Custom only where the database has their documents | [Properties](properties.md) |
| A column's Required and Allow Zero Length: read, written, enforced | ✅ | [Required and Allow Zero Length](properties.md#required-and-allow-zero-length) |
| Password-protected `.mdb`: a database password, checked as Access checks it | ✅ Read + write (Access 97: read) | [Protected files](protected-files.md) |
| Encoded `.mdb` (Access's Encrypt Database, Jet RC4) | ✅ Read + write (Access 97: read), no password needed | [Protected files](protected-files.md) |
| Password-protected `.accdb` (Agile Encryption, Access 2010+) | ✅ Read + write | [Protected files](protected-files.md) |
| Password-protected `.accdb` (RC4 CryptoAPI, Access 2007) | ✅ Read + write | [Protected files](protected-files.md) |
| Password-protected `.accdb` (Non-Standard AES, compat mode 0) | ✅ Read + write | [Protected files](protected-files.md) |
| Password-protected `.accdb` (ECMA Standard Encryption) | ✅ Read, unverified against a file Access made (none found); ❌ write | [Protected files](protected-files.md) |
| Password-protected `.accdb` (Extensible Encryption) | ❌ External CSP — non-portable | [Protected files](protected-files.md) |
| Multi-column primary keys | ✅ | [Creating a table](tables-and-schema.md#creating-a-table) |
| Foreign-key enforcement, with cascades (on by default) | ✅ | [Relationships and foreign keys](relationships.md) |
| Add a column, an index or a primary key to a table that has rows | ✅ No AutoNumber column for a table with rows | [Changing a table that has rows](tables-and-schema.md#changing-a-table-that-has-rows) |
| Table definitions of more than one page | ✅ Read, written and changed | [How a table is written](tables-and-schema.md#how-a-table-is-written) |
| Create a relationship, with referential integrity and cascades | ✅ | [Creating a relationship](relationships.md#creating-a-relationship) |
| Drop a column, index, table or relationship; rename a column, index or table | ✅ As Access writes each; not a table's last column, nor a complex column | [Dropping and renaming](tables-and-schema.md#dropping-and-renaming) |
| Queries (`MSysQueries`) | ❌ Not yet ([#26]) | |

## Limits to know about

- **Text keys** are written in the General - Legacy and General orders, and in the orders of the locales whose
  keys are General - Legacy's; a table with an index whose keys cannot be written is read and not written to:
  [Indexes kept on every write](indexes.md#indexes-kept-on-every-write).
- **A table can be asked first** whether it takes rows, with `Table.InsertRefusal`:
  [When a change is refused](cursors-and-rows.md#when-a-change-is-refused).
- **Access 97 (Jet 3) files are read and not written**, as in Jackcess: one is opened for reading only, and
  creating one, or changing anything in one, is refused with `NotSupportedException`:
  [Access 97 files are read-only](file-formats.md#access-97-files-are-read-only).
- **One writer at a time.** This library takes none of Access's locks, so a file it has open to be written is kept
  from every other writer - Access says it is "already in use" - and it does not open a file another program or
  `Database` has open for writing (`DatabaseInUseException`). Readers - Access read-only, a `Database` opened
  `ReadOnly` - share it with a writer, and a reading made while the writer writes can miss rows, give a row twice or
  with a value of another row's, or fail; one that agrees with itself needs no writer while it is made. See
  [Sharing a file with Access](sharing.md).
- **A Large Number or Date/Time Extended column is for an `.accdb`**, and makes an older `.accdb` one of a later
  format, which older versions of Access do not open:
  [A column that changes a file's format](file-formats.md#a-column-that-changes-a-files-format).
- **`Database.Create` with `FileFormat.Access2016` or `Access2019`** makes the Access 2010 file with the later
  version in its header: [Creating a file](file-formats.md#creating-a-file).
- **An import does not change a file's format**:
  [Type round-trip cheat sheet](importing-and-exporting.md#type-round-trip-cheat-sheet).

## Not supported

- Saved queries (`MSysQueries`) are not read ([#26]).
- An Access 97 file is not written to, and none is created.
- An `.accdb` in ECMA Standard Encryption is not written to, and one encrypted by an extensible provider is not
  opened: [Protected files](protected-files.md#kinds-of-protection).
- Text in a sort order other than those whose keys are made, and a value with a character Access keys by the one
  before it, in an indexed column: [Refused](indexes.md#refused).
- Decimal, Binary, OLE and complex columns have no index keys this library writes: an index on one is not added,
  and a table with an index on one is not written to. A range, or the closest row, through an index on a Decimal,
  Binary or OLE column is refused ([Ranges and the closest row](cursors-and-rows.md#ranges-and-the-closest-row)).
- A complex or calculated column is not added, and a complex column is not dropped; a calculated column reads as
  null, and a table with one is not written to: [Calculated columns](data-types.md#calculated-columns).
- An AutoNumber column is not added to a table that has rows, and a table's last column is not dropped:
  [Tables and schema changes](tables-and-schema.md).
- A column's default value is not filled in: [Required and Allow Zero Length](properties.md#required-and-allow-zero-length).
- Column matchers of a caller's own, and looking for a null: [What cursors do not do](cursors-and-rows.md#what-cursors-do-not-do).
- Access's lock file (`.ldb`, `.laccdb`) is neither made nor read: a file is kept from other writers by its own
  sharing, and a reading beside a writer is not kept from the writer's changes
  ([Sharing a file with Access](sharing.md)).

[Back to contents](README.md)

[#26]: https://github.com/markmario/jackcess-dotnet/issues/26
