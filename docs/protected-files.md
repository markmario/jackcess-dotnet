# Protected files

Opening an `.mdb` with a database password, an `.mdb` Access encoded, and an `.accdb` encrypted with a password:
which kinds are read and written, how a password is checked, and what is refused.

## Opening a protected file

```csharp
// An .mdb with a database password, or an .accdb encrypted with a password: what the file is, its header says.
using var db = Database.Open("Confidential.mdb", new DatabaseOpenOptions { Password = "passw0rd" });
```

A file is protected as its header says, and its password is checked as Access checks it: exactly, letter case and
all ([#46]). Each kind below that Access's engine still makes was made by it (DAO 16, and Jet 4.0 for Access 97, in
`tools/protected-fixtures.ps1`), opened here with its password, a wrong one and none, and compared table by table
with what DAO reads; and what this library wrote to each was read, searched through its primary key, added to and
compacted by Access (`ProtectedFilesInAccessTests`).

## Kinds of protection

| The file | Opens | Written |
| --- | --- | --- |
| An .mdb (Access 2000-2003) with a database password | with that password and no other | ✅ as any .mdb: the password changes no page |
| An .mdb whose pages Access encoded (its Encrypt Database command, DAO's `dbEncrypt`) | with any password or none, as Access opens it - unless it has a database password too | ✅ each page is decoded as it is read and encoded as it is written: RC4 under the key the header holds |
| An Access 97 .mdb with a database password, or encoded | as an Access 2000-2003 one | ❌ read-only, as every Access 97 file ([Access 97 files are read-only](file-formats.md#access-97-files-are-read-only)) |
| An .accdb with a password: Agile Encryption, which Access 2010 and later make, and ACE 16 for either .accdb format | with that password and no other | ✅ each page is decrypted as it is read and encrypted as it is written |
| An .accdb in RC4 CryptoAPI (Access 2007's), or the non-standard AES Jackcess-encrypt reads | with that password and no other | ✅ the same; refereed with jackcess-encrypt's files of them, as ACE 16 makes neither |
| An .accdb in ECMA Standard Encryption | with that password and no other; read as the specification says, which is unverified: no Access is known to make such a file, and none was found | ❌ read-only (`NotSupportedException` before anything is written): what this library would write could not be checked against Access |
| An .accdb encrypted by an extensible (external) provider | ❌ `NotSupportedException` | |

## Passwords and what is refused

- A file with a password opened with another, or with none, throws `InvalidCredentialException` before anything
  past its header is read.
- A file that is not protected opens whatever password is given: the password is not looked at.
- A file whose catalog does not read, with nothing in its header to account for it, is refused as damaged with
  `NotSupportedException`, whatever password is given; so is an .accdb whose header describes its encryption in a
  way that does not read (a length past the page, XML that does not parse, a value that is not one), given its
  password, or with a value MS-OFFCRYPTO does not allow or AES and its hash do not take - a salt, block, key or hash
  of another size, a spin count past 10,000,000 - which is refused as the file is opened, not at a page, and not
  spun.
- An .mdb's password is at most 20 characters, which is all DAO takes; an Access 97 file keeps it in a code page
  (1252 here), converted as Windows converts text to it: a character the code page lacks is the one Windows'
  best fit gives for it (L for Ł) or a question mark (Cyrillic, Chinese). Jet 3 checks a password given after the
  same conversion, and so does this library: `Пароль密码` opens such a file with `ПАРОЛЬ密码` too, and with
  `????????` (`tools/jet3-password-fixture.ps1`). The conversion is Windows', the same on every target: .NET's own
  best fit for the code page makes a question mark of eleven characters - spaces of other widths, dashes, quotation
  marks - that Windows gives a character of the code page for. Jet converts the password given to the code page of
  the machine it runs on; this library converts it to the one the file is opened in, 1252 unless
  `DatabaseOpenOptions.Access97CodePage` names another, and there with .NET's best fit, which was not compared with
  Windows' ([Text in a code page](file-formats.md#text-in-a-code-page)).
- The descriptor Access writes for an Agile-encrypted database has no data-integrity element, so there is no hash
  for a write to leave stale: Access reads what this library wrote as it reads its own.

[Back to contents](README.md)

[#46]: https://github.com/markmario/jackcess-dotnet/issues/46
