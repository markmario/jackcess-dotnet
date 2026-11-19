# Sharing a file with Access

Whether a file can be open in this library and in Access, or in two `Database`s, at the same time, and what each
may then do.

## One writer at a time

This library takes no lock on a file, as Access does with its lock file: `Database.Open` refuses, with
`IOException`, a file another process - Access holding it - or another `Database` has open for writing, and leaves
it as it was. A file this library has open is not kept from Access, which should not be given it until the
`Database` is disposed ([#62]).

An Access 97 file is opened for reading only: [Access 97 files are read-only](file-formats.md#access-97-files-are-read-only).

[Back to contents](README.md)

[#62]: https://github.com/markmario/jackcess-dotnet/issues/62
