# Golden files of text index keys

Each file holds the keys Microsoft's engine wrote for a list of text values, ascending and descending,
in one sort order. They are made by `tools/ace-text-keys.ps1`, which has the engine index each value and
reads the index pages back; `TextIndexKeyTests` compares the library's keys with them.

| File | Sort order | Written by | Read by a test |
|---|---|---|---|
| `general-legacy-ace16.tsv` | General - Legacy (1033 version 0) | ACE 16 | yes |
| `general-legacy-more-ace16.tsv` | General - Legacy | ACE 16 | yes |
| `general-ace16.tsv` | General (1033 version 1) | ACE 16 | yes |
| `access97-ace12.tsv` | Access 97's (Jet3, 1033) | 32-bit ACE 12.0 | **no** |

`access97-ace12.tsv` is not dead data: the library does not write Access 97's order yet
(qcatco/exportmdb.interop#26), and this file is the reference for the port. It is kept because the engine
that made it is rare: ACE 16 does not make or open a Jet3 file, so it took the 32-bit ACE 12.0 of Access
2007 (Jet 4.0, `DAO.DBEngine.36`, writes the same key for every value). Jet 4.0 and ACE 12.0 also write
the General - Legacy keys of the first two files, byte for byte as ACE 16 does.

Do not regenerate a file to make a failing test pass: a key that differs is a defect in the library.