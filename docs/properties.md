# Properties

The named, typed values Access keeps for a table, its columns and the database itself: how they are read,
changed and written; how a column's Required and Allow Zero Length, which are properties too, are written and
held to; and the default Access fills a Replication ID AutoNumber from.

## Reading and writing properties

Access keeps a table's properties, its columns' and the database's own as named, typed values in the catalog.
They are read, changed and written, as Jackcess's `PropertyMap` is ([#27]):

```csharp
using var db = Database.Open("Shop.mdb");
var orders = db.GetTable("Orders");

orders.Properties.Set("Description", "One row an order");
orders.Columns.First(c => c.Name == "Placed").Properties.Set("Caption", "Placed on");
orders.Properties.Save();                         // the table's and its columns', together

db.Properties.Set("AppTitle", "The shop");
db.Properties.Save();
string? title = (string?)db.SummaryProperties["Title"];
```

- **`Set` and `Remove` change the map; `Save` writes it.** A table's own properties and its columns' are one
  value in the file, so saving any of those maps saves them all. A save is written whole or not at all, and a
  save with nothing changed writes nothing. Until a change is saved it is the map's alone: `Column.IsRequired`,
  `Column.AllowZeroLength` and what rows are refused for follow the file, as Access would see it.
- **A save rewrites the object's row in the catalog** where the row is, as any row this library updates: the
  properties themselves, a long value, are written to pages of their own again, and the room the old ones had
  is noted for later long values to use.
- **Names and values are Access's.** A property's name is 1 to 64 characters, with no period, exclamation
  mark, grave accent, square bracket or control character and no space at its start; a value is not text or
  bytes of no length, which Access takes for no value. `Set` refuses the others, as DAO refuses them.
- **What Access wrote goes back as it was.** A property that was read and not changed is written with the bytes
  and flags it had, and the names the file lists keep their places: a value saved with nothing changed is the
  same bytes. (Jackcess lists the names again and encodes every value again.)
- **Types.** A property that is there keeps its type. A new one takes the type Access keeps that name as
  (`DefaultValue` a Memo, `Required` a Yes/No, `Format` Text, and so on) or else the type of the value; an
  overload names the type. Access keeps no property as a Large Number or a Decimal - DAO refuses to make one,
  and reads a Large Number property as 0 - so neither is written.
- **The database's three sets**: `Database.Properties` (what DAO's own `Database.Properties` lists beyond its
  built-in ones), `SummaryProperties` and `UserDefinedProperties` (the Summary and Custom sheets). The last
  two are kept with documents that Access's own interface adds to a database. A database made by DAO alone,
  or by this library as an `.accdb`, has neither, and saving to one is refused: the library does not add the
  document. Its new `.mdb` has both.

## Required and Allow Zero Length

**Required and Allow Zero Length are properties too** ([#41]): nothing of them is in a table's definition.
`Column.IsRequired` and `Column.AllowZeroLength` read them, `CreateTable` and `AddColumn` write them from the
`ColumnDefinition` - as DAO writes them for a field it makes - and rows are held to them as Access holds them,
with `ConstraintViolationException`:

- a row with no value in a required column is refused. A Yes/No always has a value, and an AutoNumber its number;
- a zero-length string is refused in a Text or Memo column whose `AllowZeroLength` property is there and false.
  A column with no such property takes one: that is what Access does with a column made by SQL's `CREATE TABLE`,
  though DAO reports the property as false. Spaces are not a zero-length string;
- an insert is checked in every column, an update only in the columns it gives a value: a row that already
  breaks a rule in another column is still updated, as Access updates it;
- a cascade is held to them too: an update or delete of a parent whose cascade would give a child's column a
  null it requires a value in place of, or a zero-length string it does not take, is refused whole, parent and
  all, as Access refuses it ("Cannot perform cascading operation");
- a required column can be added to a table that has rows, as in Access: the rows have no value in it, are
  still updated in other columns, and a new row needs one;
- a table made by an earlier version of this library has no properties, so nothing new is refused in it.

Where a table's properties cannot be written - a database whose catalog sorts in an order other than General
and General - Legacy, or a table whose properties were not all read - `AddColumn` still adds a column that is
what was asked for with no property at all (not required, and for Text and Memo allowing a zero-length string:
the default definition), and refuses, before anything is written, one that is required, does not allow a
zero-length string or is a Replication ID AutoNumber. `CreateTable` is refused in such a database already,
whatever its columns: the table's row cannot be added to the catalog.

`ColumnDefinition.AllowZeroLength` is `true` unless set, as for a field made in Access's table designer; a field
made with DAO does not allow a zero-length string unless asked. A column's default value is not filled in: a
required column with a `DefaultValue` and no value in the row is refused here, where Access would use the
default.

## Replication ID AutoNumbers

**A Replication ID AutoNumber** is given the `DefaultValue` property `GenGUID()` ([#33]): Access fills the column
of a row it adds from that, not from the column's flag. A Replication ID column Access made with that default
and without the flag (DAO and SQL make them so) is not an AutoNumber to this library, which fills in no default.

[Back to contents](README.md)

[#27]: https://github.com/markmario/jackcess-dotnet/issues/27
[#33]: https://github.com/markmario/jackcess-dotnet/issues/33
[#41]: https://github.com/markmario/jackcess-dotnet/issues/41
