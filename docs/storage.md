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

Rows go where Access puts them, and finding room costs no more as a table grows. A row goes to the lowest page the
table's free-space map lists with room for it, as in Access - in a table only inserted into, as an export is, its
last page, so rows stay in insertion order - and room a delete or an update frees is used again as Access uses it
([#58]): after a delete, or an update that moves a row's values to another page and back, a row can go to an
earlier page. What a `Row`, a cursor or a savepoint that read a deleted row finds then is in
[Reading, updating and deleting rows](cursors-and-rows.md#reading-updating-and-deleting-rows).

**Where a row goes, and the room a delete or an update leaves, are Access's** ([#58]), as measured with DAO 16 and
ACE 16 in `.mdb` and `.accdb` files, on tables Access made and tables this library made, in one session or many.
ExportMdb.Interop's suite has Access place the same rows and compares every page's room and slots, and the map:

- A new row - and the values of a row an update makes too long for its page - goes to the lowest page the
  table's free-space map lists (the second usage map of its definition). A listed page without room for the row
  and its slot, or with 255 rows, leaves the map and the next is tried: a page leaves the map so, never because a
  row filled it. With none, the row goes to a new page, which the map lists only when it has room, the row on it,
  for another row of the same size: a page a row of more than half a page began is not listed, so the next row,
  however small, goes to another page - a new one when no other is listed - in Access as here.
- A delete takes the row's bytes off its page - the rows after them close up - and leaves its slot, marked
  deleted and empty; a new row takes a slot of its own, not a deleted row's. The page is listed when, before the
  delete, it had no room for the row it lost and a slot, whatever room the delete leaves it (to the byte, for rows
  of 101 to 401 bytes). A page a delete leaves with no row stays the table's.
- An update that fits where the row is lists nothing. A row that comes home lists the page its values leave by the
  same rule as a delete; values that move on to another page do not list the page they leave.
- Access gives back to the file a page an update leaves with no row - out of the table's maps - and takes it again
  for the next page it needs, its slots begun again from the first: a row updated back and forth past its page's
  room adds a page to Access's file at most when each change is a session of its own (one in 3,000 such cycles,
  measured). In one session Access did not take such pages back: its file grew by about three pages in four cycles
  (26 to 774 pages in 1,000), its table keeping its 4. This library adds every page at the end of the file and takes
  none back from it, so it keeps such a page for the table, begun again and listed: the next row, or values moved,
  go to it. Every slot on such a page was a deleted one - values moved there and gone again, or a row deleted with
  its index entries. A row deleted in the session is still named by what read it, a `Row`, a cursor or a savepoint,
  so until the session ends the page keeps that row's slot, deleted, with the slots before it, and is begun again
  after them: a slot number is used again only where nothing names it, and what read a deleted row finds no row in
  its slot. So the file grows by a page at most from one session to the next, and in one session while no row is
  deleted from that page; rows inserted and deleted on it in one session fill it with kept slots, up to 255, and the
  table then takes a new page. Over 3,000 such cycles in one session, each also inserting and deleting a small row
  on that page, this library's file went from 64 to 76 pages, and Access's from 64 to 8,931, its table keeping its 4
  pages (from 64 to 2,804 without the small row).
- Finding a page reads the pages tried and no other: the map is read once a session, and kept. A page tried
  without room for the row leaves the map, as in Access, so each listed page is tried once while it is listed: a
  run of inserts tries at most as many pages as it has inserts and the map lists pages, a few pages read for each,
  however large the table. One insert tries every listed page when none has room for it - a row larger than the
  room any delete left - and takes each off the map, about three pages read and one written for each (2,000 pages
  listed, a row of 1,039 bytes: 3,012 read, 1,008 written), so that the inserts after it try none of them again.
- Where Access does not use room, this library does not either: a page with 255 slots takes no more rows, however
  many of them are deleted ones; a row that shrinks where it is, or whose values move to another page, lists no
  page. Compacting the file in Access gives all of it back.

## Long values

- **A memo or OLE value is kept in its row only up to 64 bytes**, Access's own limit.
- **A memo's text is stored as Access stores it** ([#87]), measured with ACE 16 on a column with Unicode compression:
  no text is no bytes; 1 or 2 characters are UTF-16LE; 3 to 1,908 characters that are all 0x01 to 0xFF are
  compressed, 0xFF 0xFE and a byte each, so never more than one long-value row; anything longer, or with another
  character, is UTF-16LE. ACE also compresses text that mixes the two in runs; this library writes it as UTF-16LE,
  which every reader reads the same, except text that begins with U+FEFF, which it writes in runs as ACE does
  ([Text and Memo](data-types.md#text-and-memo), [#93]).
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
  fewer), and only once a value is on it. The first long value written to a column in a session, of one chunk or
  a chain, reads the pages its map lists, once: a chain's chunks go to pages emptied in an earlier session too ([#58]).
  A value made longer and shorter again and again adds a page to its file at most, as in Access; when each change
  was a session of its own, every chain a session wrote first went to new pages.

## How a file grows

- **Files up to the format's own limit.** A table's usage map grows past its first window of pages, as
  Access's does, up to the 2 GB a Jet file can be. The page that would make the file longer than 2,147,483,648
  bytes is refused with `DatabaseFullException`, and the change it was part of taken back: ACE reads a longer
  file and cannot write to it ("Cannot open database ''... may be corrupt", measured on 2,160,779,264 bytes).
- **An index takes room of its own.** Its tree holds every row's key, so a table with indexes reaches the limit
  sooner: an export of ExportMdb's shape is 22% larger with the template's 35 indexes, and 77% larger when every
  row has a text key of 510 bytes ([Indexes built page after page](indexes.md#indexes-built-page-after-page)).
- **Pages this library adds are taken out of the database's free-page map**, so Access does not hand them out
  again and write over them.
- **Pages given back** - a dropped index's, a dropped column's long values - are free in the database's own map
  of its pages, as Access leaves them; this library adds pages at the end of the file and does not itself take a
  freed one ([Dropping and renaming](tables-and-schema.md#dropping-and-renaming)).
- **A new index's pages, and a new Memo or OLE column's,** are listed in usage maps on a page of their own
  ([How a table is written](tables-and-schema.md#how-a-table-is-written)).

[Back to contents](README.md)

[#58]: https://github.com/markmario/jackcess-dotnet/issues/58
[#87]: https://github.com/markmario/jackcess-dotnet/issues/87
[#93]: https://github.com/markmario/jackcess-dotnet/issues/93
