# File formats

Which Access file formats are read and which are written, the format a new file is made in, how a Large Number or
Date/Time Extended column changes an `.accdb`'s format, how a file's format is decided when it is opened, and why
an Access 97 file is only read, and the code page its text is read in.

## Formats read and written

| Format | `FileFormat` | Read | Created and written |
| --- | --- | --- | --- |
| Access 97 (Jet 3) `.mdb` | `Access97` | ✅ | ❌ read-only, as in Jackcess ([below](#access-97-files-are-read-only)) |
| Access 2000-2003 (Jet 4) `.mdb` | `Access2000` | ✅ | ✅ |
| Access 2007 `.accdb` | `Access2007` | ✅ | ✅ |
| Access 2010 `.accdb` | `Access2010` | ✅ | ✅ |
| Access 2016 `.accdb` | `Access2016` | ✅ | ✅ |
| Access 2019 `.accdb` | `Access2019` | ✅ | ✅ |

A file whose pages are protected is read and written as the table in [Protected files](protected-files.md) says.

## Creating a file

`Database.Create(path)` makes an Access 2000-2003 `.mdb`; `Database.Create(path, FileFormat.Access2007)`,
`Access2010`, `Access2016` and `Access2019` make an `.accdb`:

```csharp
using var mdb   = Database.Create("MyData.mdb");
using var accdb = Database.Create("MyData.accdb", FileFormat.Access2010);
```

- A new `.accdb` is copied from one of two empty databases made by DAO's `CreateDatabase`
  (`tools/empty-databases.ps1`), not taken from Jackcess; `empty2003.mdb`, which a new `.mdb` is copied from, is
  Jackcess's.
- Tables made in a new Access 2000-2003 or Access 2007 file sort their text in General - Legacy, as Access's own
  do there; in the later formats they sort in General ([Text index keys](indexes.md#text-index-keys-sort-orders-and-limits)).
- `Database.Create` with `FileFormat.Access2016` or `FileFormat.Access2019` makes the Access 2010 file with the
  later version in its header, which is all Access itself changes; Access's compact puts the version back to
  2010's while no table has such a column.
- `Database.Create(path, FileFormat.Access97)` is refused ([below](#access-97-files-are-read-only)).
- `Database.Create` replaces a file only when no one has it open, and no one opens the new file until the new
  database is disposed ([Sharing a file with Access](sharing.md#what-each-open-does)).

## A column that changes a file's format

A Large Number or Date/Time Extended column is for an `.accdb`. A table with one is refused in an `.mdb`
before anything is written, as Access refuses it. In an `.accdb` of an earlier format it is created, and
the file becomes one of Access 2016's format (a Large Number) or Access 2019's (a Date/Time Extended), as
it does when Access adds the table: an Access older than that then refuses to open the file. Jackcess
refuses the table instead. An import makes no such column in a file of an older format:
[An import does not change a file's format](importing-and-exporting.md#type-round-trip-cheat-sheet). What the two
types hold: [Large Number and Date/Time Extended](data-types.md#large-number-and-datetime-extended).

## What a file is

What a file is, is what its own header says, as in Jackcess: an Access 97 file opened with a
`DatabaseOpenOptions.FallbackFormat` of `FileFormat.Access2000` is still read as an Access 97 file and still
read-only, and an Access 2000 file opened with a fallback of `FileFormat.Access97` is the Access 2000 file it is,
read and written like any other. The fallback format is used only for a file whose header does not say.

## Access 97 files are read-only

An Access 97 (Jet 3) file is read and never written, as in Java Jackcess, whose Jet 3 format is read-only
(`JetFormat.READ_ONLY`; `JetFormat.IsReadOnly` here). `Database.Open` opens one for reading only - a file marked
read-only, or on a read-only share, opens like any other - and everything that would change it is refused with
`NotSupportedException` before anything is written: inserting, updating or deleting a row, creating or importing
a table. `Database.Create(path, FileFormat.Access97)` is refused the same way, and makes no file. Its header
decides it ([What a file is](#what-a-file-is)), and `Database.IsReadOnly` says which a database is. No row or
table definition is made in Access 97's format either: `RowEncoder` and `TableDefinition.Serialize` refuse it.
It opens while Access has it open, as a file opened `ReadOnly` does, and its pages are read again at every move,
since DAO and ACE can write to it meanwhile - unless it is opened with `ReadOnly` and `KeepWritersOut`, which keeps
them out; what that costs is under [What each open does](sharing.md#what-each-open-does). Earlier versions wrote to such a file, and wrote rows Access 97's format does not have: the end of a row's data
and its count of variable-length columns in two bytes each, where Jet 3 has one, so that the row's text read back
as null ([Files written by earlier versions](earlier-versions.md#other-things-an-earlier-version-wrote)).

What is read of one:

- **Rows of any length.** A Jet 3 row gives the offset of each variable-length value in one byte, and a row
  longer than 256 bytes has a jump table - the number of the first value past each 256 bytes - as Jackcess reads
  it (`readJumpTableVarColOffsets`). Without it, the values of such a row past its first 256 bytes read as null
  or from the wrong place.
- **Indexes of any depth.** A Jet 3 index page has the length of the prefix its entries share in one byte, at
  offset 20, and the page's level in the next; Jet 4 has two bytes and then the level. Read as two bytes, the
  prefix of every page above the leaves was 256 too long for each level, and a lookup through such a page could
  miss its row. Jackcess reads two bytes there and keeps no level for Jet 3; the pages Access 97 wrote have 0 in
  the second on every leaf and 1 on every page above the leaves, and those Jet 4.0 wrote have 2 on the page
  above those.
- **The rows of one key, wherever they are.** An index that is not unique can hold the entries of one key on
  more leaves than one; a lookup follows them from the leaf the tree leads to into the leaves after it (in a
  file of any format).
- **Text indexes**, in Access 97's General order:
  [Searched by, in a file that is only read](indexes.md#searched-by-in-a-file-that-is-only-read).
- **Text in the code page it was written in**, 1252 unless `DatabaseOpenOptions.Access97CodePage` names another:
  [below](#text-in-a-code-page).
- **Passwords**, compared in the file's code page: [Protected files](protected-files.md#passwords-and-what-is-refused).
- **A Text column's length** in the file's code page, a byte a character: Text(255) has a `Column.MaxLength` of
  255, the Size 32-bit DAO 3.6 reports, and an export gives its `DataColumn` that `MaxLength`. It was half that,
  two bytes a character as in later formats, and `ToDataSet` refused the values longer than the half ([#85]). DAO
  3.6's Size and `MaxLength` agree for all 175 Text columns of the corpus's Access 97 files and of
  `access97-jet40.mdb` (`tools/access97-text-sizes.ps1`).

The evidence: the nine Access 97 files of the corpus, which Access 97 wrote, read as 32-bit ACE 12 and DAO read
them; and `tests/JackcessDotNet.Tests/Assets/access97-jet40.mdb`, a Jet 3 database that Jet 4.0 (32-bit DAO 3.6)
wrote, with rows either side of 256, 512 and 768 bytes and an index three levels deep, which reads as that
engine read it back (`tools/access97-fixture.ps1` made it; `Access97ReadTests`). The corpus alone does not show
all of this: its one row over 256 bytes has no value past them, none of its index pages above the leaves has a
shared prefix (one has two entries, the rest one), and no index of it is more than two levels deep. ACE 16
neither makes nor opens an Access 97 file.

## Text in a code page

An Access 97 file keeps its text - its values, and the names of its tables, columns and indexes - in a Windows code
page, a byte a character, or one or two in Japanese, Chinese and Korean. It does not record which. Jet writes the
text in the code page of the machine that writes it, and reads it in the code page of the machine that reads it: a
file written on a Russian machine, whose code page is 1251, shows "Ìîñêâà" for "Москва" on a Western one, whose code
page is 1252. A character the writer's code page lacks was written as the character Windows' best fit gives for it
(é as e in 1251) or as a question mark.

So the library cannot learn the code page from the file. It reads an Access 97 file's text in code page 1252, as
Access reads it on a Western machine, and in another when the caller names it:

```csharp
using var db = Database.Open("Orders97.mdb", new DatabaseOpenOptions { Access97CodePage = 1251 });
```

- Values, the names of tables, columns and indexes, properties and the database password are read in that code
  page, and a password given is converted to it before it is compared, as Jet converts it to the machine's.
- A text index in Access 97's General order is searched by the bytes of a value in that code page: Jet keys those
  bytes with the codes they have in code page 1252, whatever the code page. In 1251 a Cyrillic letter and its other
  case are one in an index, as the Latin-1 letters with the same bytes are, but Ї and ї are not; in 932 the two
  cases of a Cyrillic letter, and of a full-width Latin one, are not. A table is found by its name the same way. A
  value with a character the code page lacks is not looked for through an index, as in 1252.
- The option is not used for a file of a later format, whose text is in Unicode. A number that is no code page .NET
  has, or one of Unicode's encodings, is refused with `ArgumentException` before the file is opened: with UTF-8 as
  the process's code page, DAO 3.6 makes no Access 97 file at all ("Not a valid password").

The header has a sort order and a code page (offsets 0x3A and 0x3C, under the mask), and neither is the code page of
the text: they are what the file's creator was told when it was made. The two test files written with code pages
1251 and 932 say General and 1252, as all nine of the corpus's Access 97 files and `access97-jet40.mdb` do. The one
thing seen to follow the header's code page is the database password that DAO's `NewPassword` sets: set as
"Пароль1" with code page 1251 in a file whose header says 1252, it was kept as "??????1", and that file then opened
only with a password that is "??????1" in the reader's code page.

The evidence: `tests/JackcessDotNet.Tests/Assets/access97-cp1251.mdb`, `access97-cp932.mdb` and
`access97-cp1251-password.mdb`, written by Jet 4.0 (32-bit DAO 3.6) in processes whose code page was 1251 or 932 -
Windows 11 takes a code page for a process from its manifest - and, beside each, what DAO read of it with that code
page and with 1252: names, rows, the tables it found by name, a Seek through the text index for every value and its
other cases, and which passwords opened it (`tools/access97-code-page-fixture.ps1`; ACE 12's 32-bit DAO wrote and
read the same). The library reads each as DAO read it with the code page it is opened in, and every key of the
text indexes is the key it makes of the row's value. Code page 1253 was measured the same way by hand.

Not measured: a file Access 97 itself wrote on a machine of another code page - none is in the corpus, and no
Access here makes one - and code pages other than 1251, 1253 and 932, which are read by the same rule. A password
with a character the code page lacks is converted with .NET's best fit for that code page, which was compared with
Windows' only for 1252 ([Protected files](protected-files.md#passwords-and-what-is-refused)).

[Back to contents](README.md)

[#85]: https://github.com/markmario/jackcess-dotnet/issues/85
