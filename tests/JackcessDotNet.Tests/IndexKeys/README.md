# Golden files of text index keys

Each file holds the keys Microsoft's engine wrote for a list of text values, ascending and descending,
in one sort order. They are made by `tools/ace-text-keys.ps1`, which has the engine index each value and
reads the index pages back; `TextIndexKeyTests` compares the library's keys with them.

| File | Sort order | Written by | Read by a test |
|---|---|---|---|
| `general-legacy-ace16.tsv` | General - Legacy (1033 version 0) | ACE 16 | yes |
| `general-legacy-more-ace16.tsv` | General - Legacy | ACE 16 | yes |
| `general-legacy-marks-ace16.tsv` | General - Legacy | ACE 16 | yes |
| `general-ace16.tsv` | General (1033 version 1) | ACE 16 | yes |
| `access97-ace12.tsv` | Access 97's General (Jet3, 1033) | 32-bit ACE 12.0 | yes |
| `access97-more-ace12.tsv` | Access 97's General | 32-bit ACE 12.0 | yes |
| `cut-keys-general-legacy-ace16.tsv` | General - Legacy | ACE 16 | yes |
| `cut-keys-general-ace16.tsv` | General | ACE 16 | yes |

The `cut-keys-` files are of whole entries, not of values: the entries ACE wrote for 32 rows in seven indexes
of several columns whose keys it cut at 510 bytes - text cut in the first, middle and last column, before a
Long ascending and descending, after another text, and in a unique index. They are made by
`tools/ace-cut-keys.ps1`, and `CutKeyTests` compares the library's keys and entries with them.

`general-legacy-marks-ace16.tsv` holds values with the twelve marks General - Legacy keys by the character
before them (ー, 々, ゝ, the shadda and their like), after characters of every kind, made by
`tools/ace-mark-keys.ps1`. That script's `-Sweep` keys each mark after every character of the BMP, which is
too much to keep here; the rule was held to all of it.

The Access 97 files (`Access97TextKeyTests`) took an engine that is rare: ACE 16 does not make or open a Jet3
file, so it took the 32-bit ACE 12.0 of Access 2007 (Jet 4.0, `DAO.DBEngine.36`, writes the same key for
every value). Access 97 keeps text in code page 1252: the first file's values are those of the General -
Legacy file, and the ones with a character the code page has not were stored as something else, so a test
passes them by. The second holds only the code page's characters. Jet 4.0 and ACE 12.0 also write the
General - Legacy keys of the first two files, byte for byte as ACE 16 does.

No file holds the keys of another locale's order: where ACE's keys for a locale are those of General -
Legacy, the first two files are that locale's too (`tools/ace-text-keys.ps1 -Locale` makes a locale's, to
compare), and `SortOrdersInAccessTests` has ACE seek what the library wrote in a database
of each such locale.

Do not regenerate a file to make a failing test pass: a key that differs is a defect in the library.