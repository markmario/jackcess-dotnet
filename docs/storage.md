# Storage: rows, long values and file growth

How rows and long values (Memo and OLE Object) are laid out on a file's pages, where a new row or value goes,
what a delete leaves, and how a file grows. Rows and long values are laid out as Access lays them out, so Access
reads back every value this library writes, a long memo in several pieces included. What a change does when it
fails part-way is in [When a change is refused](cursors-and-rows.md#when-a-change-is-refused).

## Rows on a page

Rows are read and written by the table's own definition - where it keeps each column, including after columns
have been deleted - and a row Access moved to another page is read where its pointer leads.

- **A row keeps its place when it is updated**, rewritten in its slot or moved to another page with its slot
  pointing there, as Access does it: [Reading, updating and deleting rows](cursors-and-rows.md#reading-updating-and-deleting-rows).
- **An overflow pointer that leads outside the file, or to a row its page does not have, is a damaged file**
  (`InvalidDataException` naming the page), not a missing row.
- **A deleted row is marked as Access marks one** (both flag bits). Rows deleted by earlier versions carry the
  first bit alone; they stay deleted for this library and for Access, which reads, edits and compacts such a
  table as any other.
- **A page holds at most 255 rows.**

## Where a new row goes

Inserts cost the same however large the table. A row goes to the table's last data page, so rows also stay in
insertion order. The room an emptied overflow slot or a deleted row leaves on a page is not used again, so a row
updated back and forth past its page's room makes the file grow until Access compacts it ([#58]).

## Long values

- **A memo or OLE value is kept in its row only up to 64 bytes**, Access's own limit.
- **Long values are placed as Access places them**: every chunk of a value too long for one page (4,076 bytes)
  has a page to itself. Access gives every page of such a value back to the file when the value is deleted or
  replaced, so a page shared with another value loses that value. `Database.ShareLongValuePages = true` keeps the
  placement of earlier versions - the last chunk on a page with other values' - which is smaller (a fifth
  smaller for 5,000-character Memos, nearly half for values just over one page) and reads the same, but is not
  safe for Access to edit long values in. A file written that way keeps its shared pages when it is added to.
  Added to in the default placement, such a file gets no value on a page that may hold the end of a chain.
- **A value of one chunk goes to the page with the least room that will take it**, found by its room: an insert
  looks at no page that cannot take the value, however many the column has. A page is listed in the column's
  free-space map while it has 258 bytes or more free, which is where ACE lists one (it lists none with 257 or
  fewer), and only once a value is on it. The first long value written to a column in a session
  reads the pages its map lists, once.

## How a file grows

- **Files up to the format's own limit.** A table's usage map grows past its first window of pages, as
  Access's does, up to the 2 GB a Jet file can be.
- **Pages this library adds are taken out of the database's free-page map**, so Access does not hand them out
  again and write over them.
- **Pages given back** - a dropped index's, a dropped column's long values - are free in the database's own map
  of its pages, as Access leaves them; this library adds pages at the end of the file and does not itself take a
  freed one ([Dropping and renaming](tables-and-schema.md#dropping-and-renaming)).
- **A new index's pages, and a new Memo or OLE column's,** are listed in usage maps on a page of their own
  ([How a table is written](tables-and-schema.md#how-a-table-is-written)).

[Back to contents](README.md)

[#58]: https://github.com/markmario/jackcess-dotnet/issues/58
