# Relationships and foreign keys

How the relationships a database keeps with referential integrity are enforced on every insert, update and delete,
with the cascades they ask for; how keys are matched; what enforcement costs and what it does not check; and how a
relationship is created and dropped.

## Foreign-key enforcement

```csharp
using var db = Database.Open("Northwind.mdb");
// db.EnforceForeignKeys is true unless it is set false, as in Jackcess; Access always enforces.

// The relationships in MSysRelationships that have referential integrity are kept.
orders.Insert(new Row { ["CustomerID"] = "INVALID", ... });
//   throws ConstraintViolationException: "Foreign-key violation in relationship 'CustomersOrders'..."

Row customer = customers.FindRowByPrimaryKey("ALFKI")!;
customer["CustomerID"] = "ALFKA";
customers.Update(customer);
//   cascade update: the customer's orders take the new key - or, without it, refused while it has orders

customers.Delete(customer);
//   cascade delete: its orders go too, and their order lines; cascade-to-null: their CustomerID becomes null;
//   neither: refused while it has orders
```

`Insert`, `Update` and `Delete` - and a cursor's `UpdateCurrentRow` and `DeleteCurrentRow` - refuse what would
leave a row referring to no parent, and carry out the cascades a relationship asks for, as Access does. A foreign
key whose columns are all null refers to nothing. A refused change leaves the file untouched: everything is
checked, cascades included, before anything is written, and a change that fails while being written is taken
back. With `EnforceForeignKeys` off, nothing is checked and nothing cascades.

A refused change throws `ConstraintViolationException`, as every change the data does not allow does:
[When a change is refused](cursors-and-rows.md#when-a-change-is-refused).

## What it costs

Rows are found through the tables' indexes. Access gives every relationship an index on its foreign key, so an
insert reads about a dozen pages to find its parent, and an update or delete reads the rows it reaches and the
index pages that lead to them - a cascade delete of five rows about fifty pages, whether the child has a thousand
rows or a million - and holds only those rows in memory. Every row a cascade rewrites or deletes keeps its entry in
every index of its table, so Access still finds it, and still protects its parent, afterwards.

An index is trusted to hold every row of its table ([An index is trusted](indexes.md#an-index-is-trusted)). A file
an earlier version wrote rows into may have indexes that do not, and then needs `Database.VerifyIndexes`:
[Indexes that do not hold every row](earlier-versions.md#indexes-that-do-not-hold-every-row).

Where no index on the columns can be searched (a table made without one on its foreign key, an index whose keys
this library does not write, an index that does not hold every row, a parent key that is null) the table is read
from end to end and held in memory until the change is written: some 200 bytes a row, about 60 MB and a second
for a table of 300,000 rows. A change that would rewrite a row of a table Access has deleted a column from is
refused (`NotSupportedException`): rows of such a table are not yet written correctly.

## How keys are matched

Keys are matched as Access matches them - as its indexes hold them:

- **A null matches a null.** A parent whose key is null (a unique index allows one) has the rows whose foreign
  key is null for children: deleting it deletes them under cascade delete and is refused without it, and giving
  it a key gives them the key under cascade update. A parent that has a key never touches them, and a row can
  always be added, or changed, to have no foreign key. In a key of several columns, null matches null column by
  column.
- **Numbers are matched as stored.** A Double or Single by its bits: 0 and -0 are two keys, and so are a Double
  and the next Double after it. A Date/Time by the double the row stores, which a `DateTime` does not always hold.
- **A Date/Time Extended by the 42 bytes the row stores**, as Access matches it ([#51]). Access writes a date in
  more than one form - its `INSERT` with the time of day in 100-nanosecond units, its SQL `UPDATE` and DAO's
  `AddNew` and `Edit` on a dynaset in whole seconds - and a child whose bytes are another form of its parent's
  date has no parent: Access refuses its insert, and a relationship created over it. A `DateTime` a caller gives
  is the form `INSERT` writes, which is the form this library writes.
- **A Yes/No holds no null.** A null given it is stored No and checked as No: refused when no parent's key is No.
  A cascade-to-null of a Yes/No foreign key leaves the row No, as Access leaves it, parent or no parent.

## What enforcement does not check

- **A key written again unchanged**: Access's `UPDATE` refuses to assign a key other rows refer to when the
  relationship does not cascade updates, even the value it already has; here an unchanged key is not a change.
- **Relationships without referential integrity**: they only tell Access's query designer how tables join.

## Creating a relationship

Two tables that already have rows can be given a relationship, as Jackcess's `RelationshipBuilder` gives one; the
change is all or nothing, as every schema change is ([Changing a table that has rows](tables-and-schema.md#changing-a-table-that-has-rows)):

```csharp
db.CreateRelationship(new RelationshipDefinition("Customers", "Id", "Orders", "CustomerId")
{
    HasReferentialIntegrity = true,
    CascadeDeletes = true,
});
```

- **A relationship** is a row of `MSysRelationships` for each pair of columns and an entry in the catalog. With
  referential integrity it also has the two indexes Access keeps for one: on the referenced table one named
  `.rB`, `.rC`... that shares the tree of the unique index on the referenced columns - there must be one - and
  on the referring table one with the relationship's name on the foreign key, which shares the tree of a plain
  index on exactly those columns or is built. It is refused when a row already there refers to no parent, when
  the matched columns differ in type, and when its name is a relationship's or an index's of the referring
  table. `EnforceForeignKeys` keeps it from then on.
- **A table related to itself** has both of the relationship's indexes: the foreign key's is numbered first and
  the referenced side's after it, and named by that number, as DAO writes them.
- **One to one.** A relationship whose columns are unique on both sides is marked one to one, as Access's
  Relationships window and Jackcess mark it, and its foreign key's index is then unique, as Access makes it
  (Jackcess gives it a plain one). `RelationshipDefinition.IsOneToOne` says otherwise: `true` marks a
  relationship whatever indexes there are, as DAO does when told to, and the foreign key's unique index is
  built over the rows - refused when two of them refer to one parent; `false` leaves it unmarked.
- A relationship's name follows Access's rules for names
  ([Names](tables-and-schema.md#names)), and a cycle of cascading relationships is allowed, as in Access
  ([How a table is written](tables-and-schema.md#how-a-table-is-written)).

## Dropping a relationship

`Database.DropRelationship` drops one by its name. What it takes out of the file, and which relationships
dropping a table takes with it, is in [Dropping and renaming](tables-and-schema.md#dropping-and-renaming).

[Back to contents](README.md)

[#51]: https://github.com/markmario/jackcess-dotnet/issues/51
