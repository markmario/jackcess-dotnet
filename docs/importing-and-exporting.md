# Importing and exporting

Making a table from a `DataTable`, a `DataSet` or an `IEnumerable<T>`, or adding their rows to a table that is
there; the column each CLR type is given and what comes back; and reading a table back out as a `DataTable`, a
`DataSet` or objects. How each column type holds its values is in [Data types](data-types.md).

## Importing from `DataTable`, `DataSet`, or `IEnumerable<T>`

Skip the schema boilerplate — the importer infers columns from the source type:

```csharp
using var db = Database.Create("out.mdb");
db.ImportTable(myDataTable);

// a table under another name, with a primary key of its own
db.ImportTable(otherDataTable, new ImportOptions { TableName = "Extra", PrimaryKey = new[] { "Id" } });
db.ImportTables(myDataSet);

db.ImportTable(customers, new ImportOptions { PrimaryKey = new[] { "Id" } });

// Append rows into a pre-existing table (instead of failing):
db.ImportTable(moreRows, new ImportOptions { AppendIfExists = true });
```

An import holds every value of its source exactly, or refuses before anything is written ([#44]):

- **Every value is converted to what its column holds before anything is written**, and a value no column holds
  exactly is refused then: `ArgumentException`, naming the column and the row (`ColumnValueTooLongException` for
  text longer than a column that is there).
- **What is written is written whole or not at all**, in one undo scope: a row the database refuses part-way - a
  duplicate in a key, a null in a required column - takes back the table, the rows before it and, with
  `ImportTables`, every table before it. A refused import leaves the file byte for byte as it was.
- **A column a table is made with holds every value of its source** (the
  [cheat sheet](#type-round-trip-cheat-sheet) below): its type follows the CLR type, and a length, precision or
  scale the values need follows the values.
- **Nullability and keys are carried over.** A `DataColumn` with `AllowDBNull = false`, and a property with
  `[Required]` or of a value type that is not nullable, makes a required column - Access's Required property, which
  Access and this library hold rows to; an AutoNumber and a Yes/No are not given it. A `DataTable`'s primary key of
  several columns is the table's key, in its order; several `[Key]` properties make a key of several columns, in the
  order of their `[Column(Order = n)]` where they give one, and then as they are declared. A key on strings that run
  past 255 characters is on a Memo, as Access keeps one: its keys are a value's first 255 characters, so two values
  equal in those are a duplicate, as Access takes them (measured with DAO).
- **Adding to a table that is there** (`AppendIfExists`) converts a value to its column's type only when the column
  holds it exactly: a number to a numeric column whose range, places and digits hold it (a `double` to a Decimal
  column as the number it is when the column holds that, and otherwise as the number its round-trip form denotes;
  a `double` or `float` to a Currency column as the Currency Access makes of it - its exact value to four places,
  half to even - when that reads back as the number given, which it does when the number's round-trip form has
  four places or fewer; a `decimal` or an integer to a Double column when the double nearest it is that number, or has
  it as its round-trip form); text to a numeric column as the number it denotes in the invariant culture, held as
  a number would be - exactly: `"1e-30"` goes to a Double column and is refused by a Currency one, and NaN, the
  infinities and a number past the column's range are refused; text to a Yes/No,
  Date/Time or GUID column when it reads as one in the invariant culture (text with an offset to a Date/Time
  column is the UTC time it denotes, and text without one the time it says, whatever the machine's time zone; a
  time without a date, `"10:30"`, is on 30 December 1899, as Access gives one);
  any of these to Text or Memo as its
  invariant text (a `double` or `float` in its round-trip form - the fewest digits that read back as it, written
  as .NET writes them, the same text on every target - and a date in ISO 8601) when that fits; bytes to a Binary column
  that holds that many. A Yes/No column takes no null, which Access would read as No, and no number.
- **An empty byte array is written as `Table.Insert` writes one**, for a table made and for one that is there: a
  value of no bytes, as ACE's own INSERT writes it, which ACE reads back as one and DAO shows as Null (see
  [Binary and OLE Object](data-types.md#binary-and-ole-object)).
- **Two columns whose names are one to Access are refused** - `A` and `a` of a case-sensitive `DataTable`, a
  name and its full-width form, a property and a `[Column("a")]` - for a table made, which would have one column of
  the name, and for one that is there, whose column would keep one of their values. A source column goes to the
  column of a table that is there of exactly its name, or else to the first whose name is one with it
  ([Names](tables-and-schema.md#names)).
- An `IEnumerable<T>` is read whole, every object, before anything is written.

POCO mapping respects `[Key]`, `[Column("X")]`, `[Column(Order = n)]`, `[MaxLength]`, `[Required]`, `[NotMapped]`
and `[DatabaseGenerated(Identity)]`. Unmappable types can fall back to `Memo` via
`ImportOptions.FallbackUnmappableToString = true`.

## Type round-trip cheat sheet

How the importer maps a CLR type to a column of a table it makes, and what comes back. Each column holds every
value of its type, or of the values the source has ([#44]):

| CLR type | `DataType` | Notes |
| --- | --- | --- |
| `bool` | `Boolean` (Yes/No) | a Yes/No holds no null: a null is refused |
| `byte` | `Byte` | |
| `sbyte` / `short` | `Int16` (Integer) | both come back as `short`: Access's Byte is 0 to 255 |
| `ushort` / `int` | `Int32` (Long Integer) | both come back as `int`: Access's Integer is signed |
| `uint` | `Int64` (Large Number) in a file of Access 2016's format or later, `Decimal(10, 0)` in an older one | comes back as `long`, or `decimal` |
| `long` | `Int64` (Large Number) in a file of Access 2016's format or later, `Decimal(19, 0)` in an older one | comes back as `long`, or `decimal` |
| `ulong` | `Decimal(20, 0)` | past a Large Number's range; comes back as `decimal` |
| `float` / `double` | `Single` / `Double` | every bit, NaN and the infinities included |
| `decimal` | `Decimal(p, s)` | `s` the most places after the point a value has, `p` the digits the values need and 18 at least; refused when they need more than 28 |
| `DateTime` | `DateTime` | to the millisecond, the precision a Date/Time is read and written at; `Kind` is dropped; a date before 1 January 100 (`DateTime.MinValue` among them) is refused |
| `DateTimeOffset` | `DateTime` | stored as UTC |
| `Guid` | `Guid` | |
| `string` / `char` | `Text` | the source's maximum length, or 255 when it has none, when every value fits it |
| `string` that does not fit | `Memo` | |
| `byte[]` | `Binary` | the source's maximum length, when it has one of 255 or less and every value fits it |
| `byte[]` otherwise | `OleObject` | a `byte[]` without a length - a `DataTable`'s, which `DataColumn.MaxLength` gives none - as Java's importer makes a BLOB: it holds any value, now and later |
| `Nullable<T>` | as `T` | |
| `enum` | its underlying type | comes back as the enum on read |

A `decimal` key, a `ulong` key, and a `long` or `uint` key in a file before Access 2016's format, are refused
before the table is made, with `NotSupportedException`: they are Decimal columns, whose index keys this library
does not write. A `long` or `uint` key is a Large Number in a file created as `FileFormat.Access2016` or later.

**An import does not change a file's format.** A Large Number column makes a file of an older format one of
Access 2016's, which Access 2010 and 2013 do not open ([A column that changes a file's format](file-formats.md#a-column-that-changes-a-files-format));
the importer (`DatabaseImporter`) makes one only where the file is of that format already: a `long` or `uint`
column or property is a Large Number in a file of Access 2016's format or later - one created as
`FileFormat.Access2016`, or given such a column before - and a `Decimal(19, 0)` or `Decimal(10, 0)` in an older
one, an `.mdb` included. Java Jackcess likewise makes a Large Number only in a format that has it (its `BIGINT`
is a Long Integer otherwise, which does not hold every value).

## Exporting back

```csharp
DataTable          dt    = db.GetTable("Customers").ToDataTable();
DataSet            ds    = db.ToDataSet();
IEnumerable<Customer> cs = db.GetTable("Customers").ReadRows<Customer>();
```

The `IEnumerable<T>` path is lazy via `yield`, so `Take(n)` stops early without
materialising the whole table.

[Back to contents](README.md)

[#44]: https://github.com/markmario/jackcess-dotnet/issues/44
