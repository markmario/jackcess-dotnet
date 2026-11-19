# Changelog

All notable changes to JackcessDotNet (the `Jackcess.Net` package) are documented in this file. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

The first release of this repository as a package of its own, `Jackcess.Net`. Everything below
1.2.2 is the history of [`mehran-ghanizadeh/jackcess-dotnet`](https://github.com/mehran-ghanizadeh/jackcess-dotnet),
where this started; nothing from it was released under this ID. It will be 2.0.0, because the public API has
changed since 1.2.2.

### Changed

- **Package ID `Jackcess.Net`.** nuget.org's `JackcessDotNet` is the other port's package. The
  assembly and namespace are still `JackcessDotNet`.
- **Targets `net8.0`, `net472` and `netstandard2.0`.**
- **Files Access accepts.** Every change listed under "What this fork changes" in the README: files up to the
  format's 2 GB, Access's own text index keys, every index kept on every write, AutoNumbers from the table's
  counter, rows and long values laid out as Access lays them out, `.accdb` creation, Large Number and Date/Time
  Extended columns, and foreign keys enforced.
- **Public API.** Pages, formats and codecs are internal; the public API is recorded in
  `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` and the build checks it. A broken rule of the database
  has its own exception, and `TryGetTable` was added.

### Breaking changes to the public API

Code written against 1.2.2 does not compile against this without the changes below. Nothing a file holds
changes with them.

- **`JetVersion` is `FileFormat`**, its members named for the Access version: `Jet3`, `Jet4`, `Jet12`, `Jet14`,
  `Jet16`, `Jet17` are `Access97`, `Access2000`, `Access2007`, `Access2010`, `Access2016`, `Access2019`.
  `Database.Create(path)` makes an Access 2000 file.
- **`Database.Open(path, password)` and `Open(path, version)` are `Open(path, DatabaseOpenOptions)`**, with
  `Password` and `FallbackFormat`.
- **`Database.ListTables(bool)` is `GetTableNames()` and `GetSystemTableNames()`.**
- **`Database.GetTable` throws `ArgumentException`** for a table that is not there (it threw
  `InvalidOperationException`), and returns the same `Table` for a table for as long as the database is open.
- **`Database.EnforceForeignKeys` is `true` by default** (it was `false`), as in Jackcess.
- **`Table.ReadAllRows()` is `Table.Rows`**, read as it is enumerated; `Rows.ToList()` gives the list.
- **`Table.NewCursor()` and `NewIndexCursor(name)` are `CreateCursor()`, `CreateIndexCursor()` and
  `CreateIndexCursor(name)`.** The name is an index's name and nothing else: one that is no index of the table
  throws `ArgumentException` (the cursor read the table instead), and the name of the primary key's column no
  longer stands for the primary key. `IndexCursor.FindRowByPrimaryKey` is `Table.FindRowByPrimaryKey`, which
  takes a key of several columns too.
- **A cursor moves with `MoveNext()` and `MovePrevious()` and has `Current`**, in place of `GetNextRow()`,
  `GetPreviousRow()` and `GetCurrentRow()`; `BeforeFirst()` and `AfterLast()` are `MoveBeforeFirst()` and
  `MoveAfterLast()`.
- **`Table.UpdateByPrimaryKey(key, values)` and `Table.DeleteRow(column, value)` are removed:
  `Table.Update(row)` and `Table.Delete(row)`** take a row that was read from the table - by `Rows`, a cursor
  or `FindRowByPrimaryKey` - and find it by where it was read (`Row.Id`), so a table with a key of several
  columns, or with none, is updated and deleted like any other (#34). `Update` writes only the values that are
  no longer what the row holds, and gives the `Row` its new id: the row is written again elsewhere. A row that
  was not read from the table is refused with `ArgumentException`, and one that is no longer where it was read
  with `InvalidOperationException`.
- **An index cursor is a cursor over its index** (#43, #29). `CreateIndexCursor()` throws
  `InvalidOperationException` for a table with no primary key (it made a cursor that read the table), and
  `IndexCursor.Index` is never null. Its rows come in the index's order (they came in the table's). Every
  lookup goes through the cursor's own index: `FindRowByEntry` with more values than the index has columns
  throws `ArgumentException` (it looked in another index, or in the table's leading columns), and with fewer
  finds the first row with those leading values. `FindRow(column, value)` is `Cursor`'s, finds the first row in
  the cursor's order, and throws `ArgumentException` for a column the table does not have (it returned null).
  A lookup that finds a row leaves the cursor on it, and one that finds none leaves the cursor where it was;
  a row that cannot be read is an `InvalidDataException`, not "no such row". `CreateIndexCursor(null)` needs
  a cast, `(string)null`, now that there is an overload taking an `IndexRange`.
- **A Date/Time is matched by the number a row stores** (#35), as a Double is: a `DateTime` looked for finds
  the row that stores exactly `DateTime.ToOADate()`, and a `double` the row that stores that number. A lookup
  matched a date as it read, to the millisecond, so two dates within one could not be told apart; a date
  Access stored finer than a millisecond is now found by its number, not by the `DateTime` it reads as.
- **A table or column that is not there is an `ArgumentException`** in `CreateRelationship`, `AddIndex` and
  `CreateTable` (an index's column), and so is a primary key of more than ten columns; they threw
  `InvalidOperationException`. A name that is taken stays an `InvalidOperationException` where it was one:
  `CreateTable`, an import under a table's name, a relationship's name.
- **`DatabaseBuilder`, `CursorBuilder`, `TableBuilder` and `ColumnBuilder` are removed.** A column is described
  by a `ColumnDefinition` - `new ColumnDefinition("Name", DataType.Text) { MaxLength = 50 }` - and
  `Database.CreateTable` and `Table.AddColumn` take definitions; `CreateTable`'s primary key is
  `params string[]`. What the builder refused with `InvalidOperationException` as it was built, `CreateTable`
  refuses with `ArgumentException`, as it does a primary key column that is not among the columns.
- **`Row` no longer derives from `Dictionary<string, object?>`.** It implements `IDictionary` and
  `IReadOnlyDictionary`, so the indexer, initialisers and `TryGetValue` are as they were; `Row.Set` and
  `Row.FromValues` are removed, and `GetValue<T>` and `IsNull` added. **A row that is read has an entry for
  every column, `null` for a column with no value** (it had no entry for one), so `ContainsKey` no longer says
  whether a value is null.
- **`DataType` members are renamed** (their values are not): `Int` is `Int16`, `Long` is `Int32`, `BigInt` is
  `Int64`, `Float` is `Single`, `Numeric` is `Decimal`, `ShortDateTime` is `DateTime`, `Money` is `Currency`,
  `Ole` is `OleObject`, `ExtendedDateTime` is `DateTimeExtended`.
- **`Column.Length` is `LengthInBytes`** (with `MaxLength` in characters for Text beside it) and
  **`ColumnNumber` is `Ordinal`**. `Table.Properties` is the table's own `PropertyMap` and a column's are
  `Column.Properties`; `PropertyMaps` is internal, `PropertyMap` is the collection of its properties, and
  `PropertyMap.Property` is a class, not a record.
- **`Index.IgnoresNulls` is `IgnoreNulls`;** `IndexDefinition.DescendingColumns` is a collection to add to.
- **`Relationship.FromTable`, `ToTable` and `ColumnPairs` are `FromTableName`, `ToTableName`,
  `FromColumnNames` and `ToColumnNames`;** `CascadeNull` is `CascadeNullOnDelete`; `LeftOuterJoin` and
  `RightOuterJoin` are `JoinType`. `RelationshipDefinition` has the same names, takes each side's columns as a
  list, and `RelationshipJoinType` is `JoinType`.
- **An import takes its source alone, or its source and an `ImportOptions`.** `ImportTable` and `ImportTables`
  have no optional parameters: `ImportOptions.TableName` and `ImportOptions.PrimaryKey` are in place of
  `tableName` and `primaryKey`. `PrimaryKey` is the key's columns, so a key of several can be asked for, and one
  with no names asks for a table without a key. `ImportTables` refuses options that set either, with
  `ArgumentException`: they describe one table. The properties of `ImportOptions` are set, not `init`, and
  `ImportOptions.Default` is removed. `CreateFromDataTable`, `CreateFromDataSet` and `CreateFromCollection` are
  removed: `Database.Create(path)` and then `ImportTable`.
- **`ExportToDataTable(name)`, `ExportToDataSet()` and `ExportToCollection<T>(name)` are `Table.ToDataTable()`,
  `Database.ToDataSet()` and `Table.ReadRows<T>()`.** `ToDataSet` reads the database's own tables; a system
  table is read with `GetTable(name).ToDataTable()`.
- **`Database.FindDecimalsBeyondPrecision()` and `Table.FindDecimalsBeyondPrecision()` are
  `DecimalByteOrder.Find(database)` and `DecimalByteOrder.Find(table)`,** in the namespace
  `JackcessDotNet.Diagnostics`, where `DecimalBeyondPrecision` is too.

### Added

- **Row ids.** `Row.Id` is a `RowId` - the page a row is on and its number there, Jackcess's `RowId` - for a
  row that was read, and null for one a caller made (#34).
- **`Table.InsertRange(rows)`** inserts a batch whole or not at all: when a row is refused the rows before it
  are taken out again and a `BatchInsertException` names the row (`RowIndex`) and carries what refused it. What
  Jackcess does with `addRows` and `BatchUpdateException`, which leaves the rows before it in the table (#29).
- **Cursors** (#29): `UpdateCurrentRow` and `DeleteCurrentRow`, which keep every index and enforced
  relationship as `Table.Update` and `Table.Delete` do; `FindRow` and `FindNextRow`, by one column or by a
  pattern of several; `GetCurrentRowValue` for one column of the current row; `ColumnNames` to read some of the
  columns; `EnumerateBackward`; `IsBeforeFirst` and `IsAfterLast`; `GetSavepoint` and `RestoreSavepoint`.
- **Index cursors** (#29): a cursor over the rows between two entries of an index
  (`CreateIndexCursor(name, new IndexRange { ... })`), `FindClosestRowByEntry` for the first row at or after
  an entry, and `FindRowsByEntry` for every row of an entry - for every type an index is kept for, and refused
  with `NotSupportedException` for a value no key is made for.
- **`Joiner`** finds the rows of one table that go with a row of another through an index, either way (#29).
- `NOTICE`, crediting the works this is derived from, in the repository and the package.
- Releases from `vX.Y.Z` tags, checked before anything is built and published through nuget.org Trusted
  Publishing (`.github/workflows/publish.yml`). Upstream's tag-triggered `nuget-publish.yml` is retired.
- XML documentation in the package, `JackcessDotNet.xml` beside each assembly, so an editor shows it. Every
  public type and member has it, and the build fails on one that does not (CS1591).

## [1.2.2]

### Added

- **Package icon.** `jackcess.png` (256×256, ~48 KB) wired via
  `<PackageIcon>` so the nuget.org listing renders an icon next to the
  package title.

No library code changes vs. 1.2.1.

## [1.2.1]

### Changed

- **Package metadata.** Added `PackageProjectUrl`, `RepositoryUrl`, and
  `RepositoryType` so the nuget.org package page surfaces the GitHub repo
  under "Project website" and "Source repository".
- **SourceLink.** Added `Microsoft.SourceLink.GitHub` (PrivateAssets="All").
  Consumers can now debug-step from the JackcessDotNet symbols into the
  exact source on GitHub matching the published commit SHA.
- **Deterministic builds in CI.** `ContinuousIntegrationBuild` enabled when
  `GITHUB_ACTIONS=true` so the .nupkg shipped from CI is reproducible.
- **README badges.** Added NuGet (version + downloads), GitHub (author + repo),
  and license badges visible on both GitHub and the nuget.org package page.

No library code changes vs. 1.2.0 — same `.dll` byte-for-byte aside from
embedded SourceLink metadata.

## [1.2.0]

### Added

- **Office Agile Encryption write support** (Office 2010+ `.accdb`). Pages
  encrypt on write using the same AES-CBC + per-page IV scheme as the read
  path, so round-trips through `Database.Open(path, password)` are lossless.
  Note: the DataIntegrity HMAC isn't recomputed — Office Access may flag a
  stale integrity hash on modified files. Matches the upstream `jackcess-encrypt`
  limitation.
- **ECMA Standard Encryption write support** (Office 2007 `.accdb`). AES-ECB
  encrypt on the same per-page key-derivation path as decrypt.
- **RC4 CryptoAPI Encryption** (Office 2002–2003 `.accdb`, `vMinor=2` with
  `FAES_FLAG` clear and `algId=0x6801`). Read + write — RC4 is symmetric.
  Per MS-OFFCRYPTO §2.3.5.2, including the 40-bit-key → 128-bit padding quirk.
- **Non-Standard AES encryption** (compat mode 0). Activates when a file
  advertises RC4 in the flags but actually contains AES in the algorithm ID.
  Falls back automatically with hash iterations set to 0.
- **Multi-column primary keys** via `Database.CreateTable(name, columns,
  primaryKeyColumns: new[] { "A", "B" })`. Up to 10 columns per composite key
  (Jet's hard limit). The composite key encoder writes a single leading
  ascending-flag byte then concatenates per-column value bytes in declared
  order.
- **Foreign-key enforcement on insert** (opt-in). Set
  `Database.EnforceForeignKeys = true`, and `Table.Insert` validates that each
  FK column value references an existing row in the parent table per
  `MSysRelationships`. Restrict-only — no cascade. Null FK values pass
  (SQL semantics).
- **`ImportOptions.AppendIfExists`** for the wrapper. When set, importing to
  a name that already exists in the database appends rows into the existing
  schema instead of throwing. Source columns/properties are matched to target
  columns by name (case-insensitive); unmatched source columns are dropped
  silently, unmatched target columns are left null.
- **`OfficeCryptCodecHandler.Scheme`** accessor — string label that names the
  active scheme (e.g. `"Agile Encryption (Office 2010+)"`,
  `"RC4 CryptoAPI Encryption (RC4-128)"`). Useful for diagnostics.
- **CHANGELOG.md** shipped inside the `.nupkg`.

### Fixed

- **`.accdb` page decryption was producing garbage** for every Office Crypto
  variant. The encoding key at header offset `0x3E` is XOR-masked against
  `BASE_HEADER_MASK` at rest (Jackcess Java applies this de-obfuscation at
  the PageChannel layer, but this port has no such layer). The codec now
  un-masks the encoding key before deriving per-page IVs / per-page RC4 keys.
- **Unencrypted `.accdb` files were rejected** when the bytes at offset
  `0x3E` happened to contain random non-zero data (e.g. the `linkeeTest.accdb`
  fixture from the upstream corpus). The "blank key" check now runs against
  the *un-masked* bytes and short-circuits when the `EncryptionInfo.version`
  field is `0.0`.
- **NonStandard AES dispatch escape**. `BuildStandardOrRc4` previously called
  `StandardEncryptionInfo.Read` *before* dispatching to the RC4-or-NonStandard
  fallback, so a `NotSupportedException` thrown by `ValidateRc4Header`
  escaped past the try/catch ladder. The Read is now deferred into the
  individual sub-paths, each with its own validation.
- **`Database.GetTable` no longer returns `null` PK metadata for composite
  PKs**. The reader rebuilds `PrimaryKeyColumnNames` from the on-disk index
  column block when more than one column is registered.

### Changed

- `OfficeCryptCodecHandler.HashBytes` switched from `HashAlgorithm.TransformBlock`
  + `TransformFinalBlock` to .NET 8's static one-shot methods
  (`SHA512.HashData` etc.). ~10× faster on the 100,000-iteration spin loop
  and removes a class of `HashAlgorithm` state subtleties.
- `OfficeCryptCodecHandler.AgileDescriptor.KeyData` (nested type) renamed to
  `KeyParams` to resolve CS0102 ("property and nested type can't share a name").
  No external behavior change — the type is internal.

## [1.1.0]

### Added

- **Office Crypto codec for `.accdb`** — read-only support for:
  - **Agile Encryption** (Office 2010+, `vMajor=4, vMinor=4`). MS-OFFCRYPTO
    §2.3.4.10–13 with PBKDF + AES-CBC and configurable hash. Verifies the
    password before returning a codec; throws `UnauthorizedAccessException`
    on mismatch rather than feeding garbage to the row parser.
  - **ECMA Standard Encryption** (Office 2007, `vMajor∈{2,3,4} / vMinor=2`).
    MS-OFFCRYPTO §2.3.4.5–9 with 50,000-iteration SHA-1 derivation and
    AES-ECB page crypto.

## [1.0.0]

### Added

- Initial release.
- **Read** Jet 3 (Access 97) and Jet 4 (Access 2000–2003) `.mdb` files.
- **Read** ACE 12 / 14 / 16 / 17 (`.accdb`, Access 2007 through 2019).
- **Create** new `.mdb` files at Jet 4 (and Jet 3 — read path only on Jet 3
  schema, write path on Jet 4).
- **Row CRUD**: `Insert`, `UpdateByPrimaryKey`, `DeleteRow`, table-scan cursor,
  index cursor with `FindRowByPrimaryKey` for single-column PKs.
- **B-tree primary-key index** with split-and-promote: root starts as a leaf,
  flips to a 2-level tree on overflow.
- **Memo / OLE long-value** storage via LVAL pages.
- **PropertyMap** reader (table-level + per-column properties from
  `MSysObjects.LvProp`).
- **MSysRelationships** parser exposed via `Database.GetRelationships()`.
- **Password-protected `.mdb`** via the Jet RC4 codec (Access 97 / 2000–2003).
- **`DatabaseImporter`** wrapper: `ImportTable(DataTable)`, `ImportTables(DataSet)`,
  and `ImportTable<T>(IEnumerable<T>)`. Schema inferred from source type;
  POCO reflection honours `[Key]`, `[Column]`, `[MaxLength]`, `[NotMapped]`,
  `[DatabaseGenerated(Identity)]`. `ImportOptions.FallbackUnmappableToString`
  stores unmappable CLR types as Memo via `ToString()`.
- **`DatabaseExporter`** wrapper: `ExportToDataTable(name)`, `ExportToDataSet()`,
  and lazy `ExportToCollection<T>(name?)`.
- Folder-organized source layout (`Codecs/`, `Cursors/`, `Indexes/`, `IO/`,
  `Pages/`, `Properties/`, `Schema/`, `Tables/`, `Util/`).
- NuGet package generated on every build (`GeneratePackageOnBuild`); README
  and LICENSE shipped inside the `.nupkg`; symbols emitted as `.snupkg`.
- GitHub Actions workflow that builds + tests on every push, and pushes to
  nuget.org on `v*` tags.
