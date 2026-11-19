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
for byte as it was. Access is the referee of what is written (ExportMdb.Interop's `SchemaChangesInAccessTests`):
beside the same index made by ACE, or the same relationship made by DAO, in a twin file, DAO lists the same thing
and the two definitions hold the same index slots.

- **A column** goes after the table's other columns. The rows already there are not rewritten: each reads the
  new column as null - a Yes/No column as No - as Access shows them. So an AutoNumber column is added only to a
  table with no rows (Access would number the rows there are), and a complex or calculated column is not added.
- **An index** is built over the rows already there and kept on every write after. It is refused for what an
  insert would be: two rows with one key in a unique index (rows with a null in it aside), a null in an index
  that requires its values, a column whose keys this library does not write (Decimal, Binary, OLE, Complex, text
  in another sort order), a value no key is made for. An index like one the table has shares its tree.
- **A primary key** is an index with `IsPrimaryKey` set, added to a table that has none: unique, and requiring
  its values, as Access writes one. It is refused when two rows have one key or a row has a null in it, and
  when the table has a primary key already.
- **A relationship** is described in [Creating a relationship](relationships.md#creating-a-relationship).

## Names

- **Names.** An index or a relationship is refused a name Access does not allow: one with a period, an
  exclamation mark, a grave accent, a square bracket or a control character in it, or a space at its start. So
  nothing can take the name Access gives a referenced table's index (`.rB`, `.rC`...). A relationship given no
  name is named for its two tables; when their names make no valid name, one has to be given.
- **A name another object of its kind has is taken, without regard to case.** A table, or a column, index or
  relationship, is refused a name another of its kind has, the two compared as .NET compares them without regard
  to case (`StringComparison.OrdinalIgnoreCase`) - the comparison this library looks names up with, so that no
  two objects are taken for each other. `CreateTable` is refused two columns of one name so (`ArgumentException`,
  before anything is written): it made both, and a row then kept one value for the two. The comparison throws for
  no name: one already in a file - a noncharacter in it, say - stands in the way only of a new name equal to it.
  What is renamed is compared with every other object of its kind, and left out by what it is, not by its name; a
  table's rename changes the relationships that name it, and no other table's. A table asked for by a name one has
  exactly is that table, though another's is the same without regard to case.
  This is not yet Access's own rule. Measured with DAO, Access also takes as one names that differ in width, in
  kana, or in an accent written as one character or as two, which are two names here. And it keeps apart names
  .NET takes as one without regard to case - `xµ` and `xμ` (the micro sign and the Greek mu), and letters of
  other scripts that .NET's newer case tables pair - which are one name here on .NET, and two on .NET Framework.

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
  first.
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
  in its relationships. A name Access does not allow, or that is taken, is refused; one that differs only in
  case is taken. A saved query goes on naming a column or table by the name it had, as it does when Access's
  engine renames one. A relationship's index keeps its name, and a system table its own.
- **Pages given back** are free in the database's own map of its pages, with their bytes left as they were, as
  Access leaves them: Access uses them again, and compacting the file removes them. This library adds pages
  at the end of the file and does not itself take a freed one ([How a file grows](storage.md#how-a-file-grows)).

[Back to contents](README.md)

[#37]: https://github.com/markmario/jackcess-dotnet/issues/37
