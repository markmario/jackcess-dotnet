# Tables and schema changes

Creating a table and its primary key, and changing a table that already has rows: adding a column, an index or a
primary key, dropping or renaming a column, an index or a table, and the names Access allows. Creating and
dropping a relationship is in [Relationships and foreign keys](relationships.md), and the index keys a table's
indexes hold in [Indexes and keys](indexes.md).

## Creating a table

`Database.CreateTable` makes a table from `ColumnDefinition`s, with the names of its primary key's columns after
them (the [quick start](../README.md#quick-start) makes one). A table's primary key can have several columns:

```csharp
var orders = db.CreateTable("Orders", new[]
{
    new ColumnDefinition("CustomerId", DataType.Int32),
    new ColumnDefinition("OrderId",    DataType.Int32),
    new ColumnDefinition("Total",      DataType.Currency),
}, "CustomerId", "OrderId");

Row? order = orders.FindRowByPrimaryKey(42, 7);
```

Up to 10 columns per composite key (Jet's hard limit), named in key order after the columns; none for a
table without a primary key.

`CreateTable` is all or nothing, as every change on this page is
([Changing a table that has rows](#changing-a-table-that-has-rows)), and writes each column's Required and Allow
Zero Length as Access's properties ([Required and Allow Zero Length](properties.md#required-and-allow-zero-length)).

## Changing a table that has rows

A table that already has rows - one Access made or one this library made - can be given a column, an index or
a primary key, and two tables a relationship, as Jackcess's `TableUpdater` and `RelationshipBuilder` do:

```csharp
using var db = Database.Open("Shop.mdb");
var orders = db.GetTable("Orders");

orders.AddColumn(new ColumnDefinition("Shipped", DataType.DateTime));
orders.AddIndex(new IndexDefinition("ByCustomerAndDate", "CustomerId", "Placed") { IsUnique = true });

db.CreateRelationship(new RelationshipDefinition("Customers", "Id", "Orders", "CustomerId")
{
    HasReferentialIntegrity = true,
    CascadeDeletes = true,
});

db.GetTable("Notes").AddIndex(new IndexDefinition("PrimaryKey", "Id") { IsPrimaryKey = true });

orders.RenameColumn("Placed", "PlacedOn");
orders.RenameIndex("ByCustomerAndDate", "ByCustomer");
orders.DropIndex("ByCustomer");
orders.DropColumn("Shipped");
db.DropRelationship("CustomersOrders");
db.RenameTable("Orders", "Sales");
db.DropTable("Notes");
```

Each change is all or nothing, and so is the creation of a table (`CreateTable`): what can refuse it is found before
anything is written, and what is written is taken back if writing it fails, so a refused change leaves the file byte
for byte as it was. Access is the referee of what is written (`SchemaChangesInAccessTests`):
beside the same index made by ACE, or the same relationship made by DAO, in a twin file, DAO lists the same thing
and the two definitions hold the same index slots.

- **A column** goes after the table's other columns. The rows already there are not rewritten: each reads the
  new column as null - a Yes/No column as No - as Access shows them. So an AutoNumber column is added only to a
  table with no rows (Access would number the rows there are), and a complex or calculated column is not added.
- **An index** is built over the rows already there and kept on every write after. It is refused for what an
  insert would be: two rows with one key in a unique index (rows with a null in it aside), a null in an index
  that requires its values, a column whose keys this library does not write (Decimal, Binary, OLE, Complex, text
  in another sort order), a value no key is made for. An index like one the table has shares its tree. The tree
  is built from the rows sorted, page after page, as Access builds one when it compacts
  ([Indexes built page after page](indexes.md#indexes-built-page-after-page)): a few page writes for each hundred
  rows.
- **A primary key** is an index with `IsPrimaryKey` set, added to a table that has none: unique, and requiring
  its values, as Access writes one. It is refused when two rows have one key or a row has a null in it, and
  when the table has a primary key already.
- **A relationship** is described in [Creating a relationship](relationships.md#creating-a-relationship).

## Names

- **Names.** An index or a relationship is refused a name Access does not allow: one with a period, an
  exclamation mark, a grave accent, a square bracket or a control character in it, or a space at its start. So
  nothing can take the name Access gives a referenced table's index (`.rB`, `.rC`...). A relationship given no
  name is named for its two tables; when their names make no valid name, one has to be given.
- **A name that is one with another of its kind is taken, as Access takes it** ([#63]). A table, or a column,
  index or relationship, is refused a name that is one with another's of its kind, before anything is written -
  `ArgumentException`, or `InvalidOperationException` for a table's - and the message names the other. Access
  decides which two names are one by their text index keys, and so does this library: two names are one when the
  keys an index has for them are equal, their trailing spaces dropped, or when the keys of the whole names are.
  Access refuses a second name whose index key is a first one's; its own lookups - DAO's collections, SQL - take as
  one two names whose whole keys are equal: DAO makes a table `x` and a space and a table `x` and an ideographic
  space, and then deletes neither by its name. So names in the other case, the other width or the other kana, with
  an accent written as one character or as two, or `ß` against `ss`, are one; names Access keeps apart are two:
  `xµ` and `xμ` (the micro sign and the Greek mu), `e` and `é`, `I` and `ı`. The comparison is the same on every
  target.
- **The order they are compared in** is the one Access compares them by:
  - in a database that sorts in General - Legacy, Access 2000 to 2007's, a table's, a column's, an index's and a
    relationship's name by General - Legacy's keys;
  - in one that sorts in General, Access 2010's, which a new file of a later format sorts in
    ([File formats](file-formats.md)), each by General's keys and by General - Legacy's. Access compares a table's
    name there by the one in its catalog and by the other in its lookups: DAO makes two tables `xȤ` and `xȥ`, which
    General's keys keep apart and General - Legacy's take as one, and then finds either for the other. And it
    compares a column's name by the one or the other as its process has gone: DAO made two fields `xƀ` and `xɃ`,
    which General's keys take as one and General - Legacy's keep apart, and kept them apart; in a process that had
    used such a database through DAO before, ACE refused the second as a field already there and as an index's
    name, and its SQL found the first by the name of the second. In a database that sorts in General - Legacy, ACE
    kept them apart whatever the process had done;
  - in an Access 97 file, which this library only reads, a table's name by the index keys of Access 97's General
    order, and a column's or an index's by those and General - Legacy's, by which DAO's Fields and Indexes take
    some names as one there (`xÞ` and `xTH`).
- **Measured.** DAO 16 and Jet 4.0 were asked about 18,140 pairs of names, as field, table and index names, in a
  database of each of those orders, by `tools/dao-object-names.ps1`; the engine's tests hold 743 of them
  (`tests/JackcessDotNet.Tests/Assets/dao-object-names.tsv`). The pairs: each character of the BMP that has another
  form - its other case, its canonical and compatibility forms and their case, its other kana, the letters it sorts
  as - after an `x`, against that form; trailing and inner spaces, and the characters General - Legacy has no code
  for; the names of code page 1252 against their letters without an accent; and names with the marks below. Every
  pair DAO takes as one, or makes as two and then confuses, is one here. Of the 90,205 verdicts in which DAO keeps
  two names apart and tells them apart, 3,786 are one here, each for one of the two reasons that follow.
- **Names Access allows that are refused**, so that no name made here is one Access's lookups take for another:
  - In a database that sorts in General, a name that one of the two orders' keys takes as one with another's of
    its kind, which the other's keep apart and DAO made beside it in its own process: 3,739 of the verdicts, most of
    them a column's or an index's name General's keys take as one with another (`xƀ` and `xɃ`, a letter and its
    compatibility form), and some a table's name General - Legacy's keys take as one with another (`xÜ` against
    `xU` with a combining diaeresis, `x` against `x` followed by a character General - Legacy has no code for:
    U+03F3, U+202F, U+FEFF).
  - A name with a mark Access keys by the characters around it - the prolonged sound mark `ー`, the iteration
    marks `ゝ`, `々` and `〱` to `〵`, the double hyphen `゠`, the Yi iteration mark, the shadda and the Arabic marks
    General keys by context - is one with another name when any reading of it is: each mark as itself, a voiced
    iteration mark as the plain one and U+3099 (`ゞ` is one with `ゝ` and U+3099, as to Access); each as the
    character before it, as Access reads `ー` after a Latin letter; and the Arabic marks left out. That takes as
    one some names Access keeps apart (`人々` and `人人`): 47 of the verdicts, and as two none it takes as one.
    Such a name is a column's or an index's; a table or a relationship is not given one, in either order, though
    General - Legacy's key of it is made ([Refused](indexes.md#refused)).
- **A database whose text sorts in another order.** Access compares the names of its columns and indexes by that
  order's own keys, which this library makes for General - Legacy, General and the orders whose keys are General -
  Legacy's ([Text index keys](indexes.md#text-index-keys-sort-orders-and-limits)). In another, names General -
  Legacy keeps apart are one: DAO refused an index `xʎ` beside `xÜ` in Swedish, `xė` beside `xℯ` in Czech, `x―`
  beside `xー` in Japanese and `xi` beside `xİ` in Turkish, and made such fields and then took one for the other.
  There a new column, index or relationship name is compared with the others by General - Legacy's keys and without
  regard to case, as .NET's ordinal comparison takes case (`StringComparison.OrdinalIgnoreCase`), and refused -
  `ArgumentException`, before anything is written - only when it is one with another by either. That is not the
  order's own rule, which this library does not have: it makes names Access refuses there, as those above, and
  refuses some Access makes. .NET Framework takes fewer characters for another's case than .NET does - the micro
  sign and mu are one on .NET and two on .NET Framework - so such a name is refused on the one and made on the
  other. In Swedish, Czech, French and Japanese DAO gave the pairs of names of printable ASCII it was first asked
  about - in another case, with punctuation or spaces among its letters - the verdict General - Legacy gives; but
  even such names are not compared there quite as General - Legacy compares them: in Czech DAO keeps `xch` and `xcH`
  apart, which General - Legacy's keys take as one, so the second is refused here where Access makes it. No table is
  created or renamed there, and no column Access keeps properties for is renamed or dropped: the catalog's keys are
  not made. The database's tables, columns and indexes are found by name as in any other.
- **A lookup finds the name given exactly first.** `GetTable`, `TryGetTable`, a table's columns and indexes, a
  `Row`'s entries, a join's columns, an export's properties and an import's columns are found by the name given
  exactly, when there is one; otherwise by the first name that is one with it by its index key, and then by the
  first that is one with it at all, as Access's lookups take the first of names that are one. A file can have two
  names that are one - Access makes the fields `x` and `x ` (a space), and the tables above - and each is found by
  its own.
- **What this library keeps by name it keeps by the exact name**: a row a caller makes, the row it writes, a
  table's long-value and property maps, and the tables and columns of the relationships it enforces. A `Row` a
  caller makes holds each value under the name given - `x` and `X` are two entries, as `x` and `x ` (a space) are -
  and a value is set, added and taken out by its exact name alone; it is read by its exact name, and otherwise by a
  name that is one with it by General - Legacy's keys. A name finds its column where the row meets a table -
  `Insert`, `InsertRange`, `Update`, `Validate`: the column of exactly that name, and only when there is none the one
  a lookup of it finds. Two of a row's names that go to one column (`x` and `X` for a column `x`) are refused with
  `ArgumentException`, naming both and the column, before anything is written: neither value is dropped for the
  other. So a table with two columns whose names are one keeps a value, a map and a relationship of each: a row
  given `x`, `x  ` (two spaces), `xµ` and `xμ` for a table DAO made with those four fields writes each value to its
  own, and ACE reads the four back. A row a table gives has an entry for each column it was read with, under the
  column's name, and a name of another spelling goes to the column a lookup of it finds in the table, where an
  insert sends it: a row read with some of the columns reads and sets it as a whole row does, and given a value for
  a column it was read without, adds it under the column's name.
- **The comparison throws for no name**, and a name already in a file stands in the way only of a new name that
  is one with it. Half of a surrogate pair alone in a name, which Access keeps as it is given, is read as the file
  holds it and found by that name; a new name with one is refused with `NotSupportedException`. What is renamed is
  compared with every other object of its kind, and left out by what it is, not by its name; a table's rename
  changes the relationships that name it, and no other table's.

## How a table is written

- **Where Access and Jackcess differ, Access is followed**: a name that is taken is refused, not changed; a
  cycle of cascading relationships is allowed; a table's index slots stay in the order of their names; a new
  index's block carries the counts Access puts there.
- **A table's definition goes on to as many pages as Access gives it**: a table with many columns or long names
  is created, read, written to and changed like any other, up to the 255 columns and 32 indexes a table has.
  Access puts a definition on the fewest pages that hold its length and eight bytes more, so one that ends in
  the last eight bytes of a page has a page after it with none of it. Jackcess writes such a definition on the
  page it fills, and Access then opens nothing of the file; here Access is followed.
- **Usage maps.** A new index's pages, and a new Memo or OLE column's, are listed in usage maps on a page of
  their own - 4 KB each - as for a table this library creates; Access finds room on the table's usage-map page
  first. Measured ([#37]): DAO and ACE's DDL alike put a table's Memo and OLE columns' maps, and a later index's, as
  rows on the table's usage-map page, when they make the table and when they add to it. DAO adds and deletes
  columns and indexes on this library's tables, its own and this library's index among them, ACE writes to them,
  and DAO compacts them whole.
- **The bytes of a column's definition are DAO's.** A column that is not Text has zeros where DAO writes zeros and
  ACE's DDL writes the database's sort order (DAO writes the sort order for Text only; this library for Text and
  Memo, as ACE's DDL does for a Memo). A Date/Time Extended column has 00 00 for its precision and scale, as DAO
  writes them, where ACE's DDL writes 09 00 (and, by an earlier report, Access's table designer 09 07). Measured
  ([#37], [#38]), with nothing found that depends on either: DAO's design changes and compaction, and what ACE and
  DAO store and find in a Date/Time Extended column, seven places of its seconds among it, are the same with each.
- **Permissions.** A new table or relationship has, in `MSysACEs`, the entries its container passes on, its
  owner's among them, as Access gives them: without the owner's, Access in an `.mdb` would not change what
  this library made.

## Dropping and renaming

A column, an index, a relationship or a table can be dropped, and a column, an index or a table renamed ([#37]).
Jackcess does neither, so what is dropped or renamed is written as Access (DAO) was measured to write it; Access
is the only referee, and what it refuses is refused:

- **Dropping an index** takes it out of the table's definition. Its tree's pages go back to the file, unless
  another index of the table shares the tree. A primary key is dropped by its index's name. An index a
  relationship keeps, and the unique index or primary key a relationship with referential integrity refers to
  a table's rows by, are not dropped while the relationship is there.
- **Dropping a column** takes it out of the definition, with the properties Access keeps for it, and gives a
  Memo or OLE column's long values back to the file. The rows are not rewritten - Access does not rewrite them
  either: each keeps the bytes it had for the column, which nothing reads, until the file is compacted. A column
  in an index or in a relationship, with or without referential integrity, is not dropped. Access drops a
  table's last column; this library refuses to, as it does a complex column. A dropped column keeps its number
  until the file is compacted, so a table that has had 255 columns takes no more, however few it has: Access
  refuses the next ("Too many fields defined") and so does `AddColumn`, until Access compacts the file.
- **Dropping a relationship** takes out its rows in `MSysRelationships`, its entry in the catalog and, with
  referential integrity, its index on each table.
- **Dropping a table** takes its entry out of the catalog and the relationships in which it refers to another
  table, or to itself, with it. A table another table refers to is not dropped. A `Table` got for it before
  throws `InvalidOperationException` from then on.
- **Renaming** a column changes its name in the definition, in its relationships and in its properties; an
  index's, in the definition, where its slot moves to where the new name sorts; a table's, in the catalog and
  in its relationships. A name Access does not allow, or that is taken, is refused; one that is one only with
  the object's own name - in another case, say - is taken. A saved query goes on naming a column or table by the
  name it had, as it does when Access's engine renames one. A relationship's index keeps its name, and a system
  table its own.
- **Pages given back** are free in the database's own map of its pages, with their bytes left as they were, as
  Access leaves them: Access uses them again, and compacting the file removes them. This library adds pages
  at the end of the file and does not itself take a freed one ([How a file grows](storage.md#how-a-file-grows)).

[Back to contents](README.md)

[#37]: https://github.com/markmario/jackcess-dotnet/issues/37
[#38]: https://github.com/markmario/jackcess-dotnet/issues/38
[#63]: https://github.com/markmario/jackcess-dotnet/issues/63
