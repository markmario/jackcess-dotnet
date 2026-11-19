<#
.SYNOPSIS
    Makes tests/JackcessDotNet.Tests/Assets/ace-extended-date-forms.accdb: Date/Time Extended values in every form
    Access reads a date from, keyed by ACE in its own indexes.

.DESCRIPTION
    Access stores a Date/Time Extended value as 42 bytes of text and keys it by those bytes, whatever date they say
    (#51). Its INSERT writes one form - the time of day in 100-nanosecond units and a count of 7 - and its SQL UPDATE
    another - the time in whole seconds and a count of 0:

      0000000000000737425:0000000110450000000:7\0   2 January 2020, 03:04:05, as INSERT writes it
      0000000000000737425:0000000000000011045:0\0   the same, as UPDATE writes it

    DAO reads others as dates too: a space or a plus sign before a number's digits, and anything but a digit in the
    last byte. The file holds rows in each of these forms, the last three patched into the file's bytes after ACE
    wrote them, and indexes ACE made over them afterwards, so that every entry is one ACE keyed from the bytes as
    they are:

      T   Id LONG primary key TPK, TForm TEXT(30), K DATETIME2; indexes KAsc (K), KDesc (K DESC), KForm (K DESC,
          TForm) and FormK (TForm, K). Fifteen rows:
            1  first           2020-01-02 03:04:05, by INSERT
            2  second          the same, by UPDATE
            3  space-day       the first form, a space for the day's first digit
            4  plus-day        the first form, a plus sign for the day's first digit
            5  x-last-byte     the first form, 'x' in the last byte
            6  space-time      the first form, a space for the time's first digit
            7  second-space    the second form, a space for the day's first digit
            8  fraction        2020-01-02 03:04:05.1234567, by INSERT
            9  second+1s       2020-01-02 03:04:06, by UPDATE
           10  day-before      2020-01-01 23:59:59, by INSERT
           11  midnight-2nd    2020-01-02 00:00:00, by UPDATE
           12  midnight-1st    the same, by INSERT
           13  one-tick        2020-01-02 00:00:00.0000001, by INSERT
           14  one-second-2nd  2020-01-02 00:00:01, by UPDATE
           15  one-second-1st  the same, by INSERT
          Every row is 2 January 2020 03:04:05 to DAO but 8 to 15. ACE's WHERE K = '2020-01-02 03:04:05' finds rows
          1 to 7 when no index is on K, and row 1 alone through KAsc. DAO walks KAsc in the order
          7, 3, 4, 10, 6, 11, 12, 14, 13, 2, 9, 15, 1, 5, 8: the order of the bytes.
      U   Id LONG primary key UPK, UForm TEXT(30), K DATETIME2 with a unique index KU, made after rows 1 to 4 - 2
          January 2020 03:04:05 as INSERT and UPDATE write it, a space for the day's first digit, 'x' in the last
          byte - which ACE takes: they are four keys. It refuses a fifth row with the date by INSERT, and a row
          given it by UPDATE.
      V   Id LONG, VForm TEXT(30), K DATETIME2 with the primary key VPK, made after rows 1 and 2 - the date as
          INSERT and as UPDATE write it - which ACE takes.
      W   Id LONG, WForm TEXT(30), K DATETIME2 and, in the end, no index: rows 1 to 4 of U, for a primary key to
          be added over.

    Made by ACE 16 (DAO.DBEngine.120) as an Access 2016 .accdb. The file holds the time it was made, so the fixture
    is committed, and this script is the record of how.

.EXAMPLE
    pwsh tools/ace-extended-date-forms.ps1
#>
[CmdletBinding()]
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\tests\JackcessDotNet.Tests\Assets\ace-extended-date-forms.accdb')
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class JetExtendedDateForms
{
    const int PageSize = 4096;

    static byte[] Page(byte[] file, int number)
    {
        var p = new byte[PageSize];
        Buffer.BlockCopy(file, number * PageSize, p, 0, PageSize);
        return p;
    }

    /// The page of the table definition that has a column of this name.
    public static int FindTdef(byte[] file, string columnName)
    {
        byte[] needle = Encoding.Unicode.GetBytes(columnName);
        for (int n = 2; n < file.Length / PageSize; n++)
        {
            int at = n * PageSize;
            if (file[at] != 0x02 || file[at + 1] != 0x01) continue;
            for (int i = at; i <= at + PageSize - needle.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && file[i + j] == needle[j]) j++;
                if (j == needle.Length) return n;
            }
        }
        throw new InvalidDataException("No table definition has a column named " + columnName + ".");
    }

    /// A column's fixed offset in a row, and an index's root page, from the table's definition.
    public static int FixedOffset(byte[] file, int tdef, string column)
    {
        byte[] p = Page(file, tdef);
        int numCols = BitConverter.ToInt16(p, 45);
        int numReal = BitConverter.ToInt32(p, 51);
        int pos = 63 + numReal * 12;
        var offsets = new int[numCols];
        for (int i = 0; i < numCols; i++, pos += 25) offsets[i] = BitConverter.ToInt16(p, pos + 21);
        for (int i = 0; i < numCols; i++)
        {
            int length = BitConverter.ToInt16(p, pos);
            if (Encoding.Unicode.GetString(p, pos + 2, length) == column) return offsets[i];
            pos += 2 + length;
        }
        throw new InvalidDataException("The table has no column " + column + ".");
    }

    public static int Root(byte[] file, int tdef, string index)
    {
        byte[] p = Page(file, tdef);
        int numCols = BitConverter.ToInt16(p, 45);
        int numIdx = BitConverter.ToInt32(p, 47);
        int numReal = BitConverter.ToInt32(p, 51);
        int pos = 63 + numReal * 12 + numCols * 25;
        for (int i = 0; i < numCols; i++) pos += 2 + BitConverter.ToInt16(p, pos);
        var roots = new int[numReal];
        for (int r = 0; r < numReal; r++, pos += 52) roots[r] = BitConverter.ToInt32(p, pos + 38);
        var real = new int[numIdx];
        for (int i = 0; i < numIdx; i++, pos += 28) real[i] = BitConverter.ToInt32(p, pos + 8);
        for (int i = 0; i < numIdx; i++)
        {
            int length = BitConverter.ToInt16(p, pos);
            if (Encoding.Unicode.GetString(p, pos + 2, length) == index) return roots[real[i]];
            pos += 2 + length;
        }
        throw new InvalidDataException("The table has no index " + index + ".");
    }

    /// Where each row of a table keyed by a Long is, by its key: the primary key's leaf entries.
    public static Dictionary<int, int> RowsByLong(byte[] file, int root)
    {
        var rows = new Dictionary<int, int>();
        int number = root;
        while (true)
        {
            byte[] p = Page(file, number);
            bool leaf = p[0] == 0x04;
            int trailer = leaf ? 4 : 8;
            int prefixLen = BitConverter.ToUInt16(p, 24);
            const int mask = 27, maskSize = 453, entriesStart = mask + maskSize;
            byte[] prefix = null;
            int start = 0, firstSub = 0, count = 0;
            for (int i = 0; i < maskSize; i++)
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((p[mask + i] & (1 << bit)) == 0) continue;
                    int end = i * 8 + bit;
                    var stored = new byte[end - start];
                    Buffer.BlockCopy(p, entriesStart + start, stored, 0, stored.Length);
                    byte[] full = stored;
                    if (count > 0 && prefix != null)
                    {
                        full = new byte[prefix.Length + stored.Length];
                        Buffer.BlockCopy(prefix, 0, full, 0, prefix.Length);
                        Buffer.BlockCopy(stored, 0, full, prefix.Length, stored.Length);
                    }
                    if (count == 0 && prefixLen > 0)
                    {
                        prefix = new byte[prefixLen];
                        Buffer.BlockCopy(stored, 0, prefix, 0, prefixLen);
                    }
                    int k = full.Length - trailer;
                    if (leaf)
                        rows[(int)((uint)((full[1] << 24) | (full[2] << 16) | (full[3] << 8) | full[4]) ^ 0x80000000u)] =
                            (full[k] << 24) | (full[k + 1] << 16) | (full[k + 2] << 8) | full[k + 3];
                    else if (count == 0)
                        firstSub = (full[k + 4] << 24) | (full[k + 5] << 16) | (full[k + 6] << 8) | full[k + 7];
                    count++;
                    start = end;
                }
            if (leaf)
            {
                int next = BitConverter.ToInt32(p, 16);
                if (next <= 0) return rows;
                number = next;
            }
            else number = count > 0 ? firstSub : BitConverter.ToInt32(p, 20);
        }
    }

    /// Where the row at a pointer (page << 8 | row) begins in the file.
    public static long RowStart(byte[] file, int pointer)
    {
        int pageNumber = pointer >> 8, row = pointer & 0xFF;
        byte[] p = Page(file, pageNumber);
        if (p[0] != 0x01) throw new InvalidDataException("Page " + pageNumber + " is not a data page.");
        int slot = BitConverter.ToUInt16(p, 14 + row * 2);
        if ((slot & 0xC000) != 0) throw new InvalidDataException("Row " + pointer + " is not where its slot says.");
        return (long)pageNumber * PageSize + (slot & 0x1FFF);
    }
}
'@

$outPath = [IO.Path]::GetFullPath($Out)
if (Test-Path $outPath) { Remove-Item $outPath }
$dbVersion140 = 256; $dbFailOnError = 128
$D0 = '2020-01-02 03:04:05'

# Each row: Id, the form it is in, the value an INSERT gives it, the value a later SQL UPDATE gives it (null: none),
# and the byte patched into its stored value afterwards (null: none), as offset=character.
$tRows = @(
    @(1,  'first',          $D0,                           $null,                 $null),
    @(2,  'second',         '2020-01-09 00:00:00',         $D0,                   $null),
    @(3,  'space-day',      $D0,                           $null,                 '0= '),
    @(4,  'plus-day',       $D0,                           $null,                 '0=+'),
    @(5,  'x-last-byte',    $D0,                           $null,                 '41=x'),
    @(6,  'space-time',     $D0,                           $null,                 '20= '),
    @(7,  'second-space',   '2020-01-09 00:00:00',         $D0,                   '0= '),
    @(8,  'fraction',       '2020-01-02 03:04:05.1234567', $null,                 $null),
    @(9,  'second+1s',      '2020-01-09 00:00:00',         '2020-01-02 03:04:06', $null),
    @(10, 'day-before',     '2020-01-01 23:59:59',         $null,                 $null),
    @(11, 'midnight-2nd',   '2020-01-09 00:00:00',         '2020-01-02 00:00:00', $null),
    @(12, 'midnight-1st',   '2020-01-02 00:00:00',         $null,                 $null),
    @(13, 'one-tick',       '2020-01-02 00:00:00.0000001', $null,                 $null),
    @(14, 'one-second-2nd', '2020-01-09 00:00:00',         '2020-01-02 00:00:01', $null),
    @(15, 'one-second-1st', '2020-01-02 00:00:01',         $null,                 $null)
)
$uRows = @(
    @(1, 'first',       $D0,                   $null, $null),
    @(2, 'second',      '2020-01-09 00:00:00', $D0,   $null),
    @(3, 'space-day',   $D0,                   $null, '0= '),
    @(4, 'x-last-byte', $D0,                   $null, '41=x')
)

$engine = New-Object -ComObject DAO.DBEngine.120
try {
    $db = $engine.CreateDatabase($outPath, ';LANGID=0x0409;CP=1252;COUNTRY=0', $dbVersion140)
    try {
        $db.Execute('CREATE TABLE T (Id LONG CONSTRAINT TPK PRIMARY KEY, TForm TEXT(30), K DATETIME2)', $dbFailOnError)
        foreach ($r in $tRows) {
            $db.Execute("INSERT INTO T (Id, TForm, K) VALUES ($($r[0]), '$($r[1])', '$($r[2])')", $dbFailOnError)
            if ($r[3]) { $db.Execute("UPDATE T SET K = '$($r[3])' WHERE Id = $($r[0])", $dbFailOnError) }
        }
        $db.Execute('CREATE TABLE U (Id LONG CONSTRAINT UPK PRIMARY KEY, UForm TEXT(30), K DATETIME2)', $dbFailOnError)
        foreach ($r in $uRows) {
            $db.Execute("INSERT INTO U (Id, UForm, K) VALUES ($($r[0]), '$($r[1])', '$($r[2])')", $dbFailOnError)
            if ($r[3]) { $db.Execute("UPDATE U SET K = '$($r[3])' WHERE Id = $($r[0])", $dbFailOnError) }
        }
        $db.Execute('CREATE TABLE V (Id LONG, VForm TEXT(30), K DATETIME2)', $dbFailOnError)
        $db.Execute("INSERT INTO V (Id, VForm, K) VALUES (1, 'first', '$D0')", $dbFailOnError)
        $db.Execute("INSERT INTO V (Id, VForm, K) VALUES (2, 'second', '2020-01-09 00:00:00')", $dbFailOnError)
        $db.Execute("UPDATE V SET K = '$D0' WHERE Id = 2", $dbFailOnError)
        # W is keyed by Id while it is filled, so that its rows can be found to be patched; the key goes after.
        $db.Execute('CREATE TABLE W (Id LONG CONSTRAINT WPK PRIMARY KEY, WForm TEXT(30), K DATETIME2)', $dbFailOnError)
        foreach ($r in $uRows) {
            $db.Execute("INSERT INTO W (Id, WForm, K) VALUES ($($r[0]), '$($r[1])', '$($r[2])')", $dbFailOnError)
            if ($r[3]) { $db.Execute("UPDATE W SET K = '$($r[3])' WHERE Id = $($r[0])", $dbFailOnError) }
        }
    }
    finally { $db.Close() }

    # The forms DAO reads a date from but ACE does not write, patched into the bytes ACE wrote.
    $file = [IO.File]::ReadAllBytes($outPath)
    foreach ($spec in @(@('TForm', 'TPK', $tRows), @('UForm', 'UPK', $uRows), @('WForm', 'WPK', $uRows))) {
        $tdef = [JetExtendedDateForms]::FindTdef($file, $spec[0])
        $offset = [JetExtendedDateForms]::FixedOffset($file, $tdef, 'K')
        $rows = [JetExtendedDateForms]::RowsByLong($file, [JetExtendedDateForms]::Root($file, $tdef, $spec[1]))
        foreach ($r in $spec[2]) {
            if (-not $r[4]) { continue }
            $at, $char = $r[4] -split '=', 2
            # A row begins with its column count (two bytes), then the fixed-length values at their offsets.
            $where = [JetExtendedDateForms]::RowStart($file, $rows[$r[0]]) + 2 + $offset + [int]$at
            $file[$where] = [byte][char]$char
        }
    }
    [IO.File]::WriteAllBytes($outPath, $file)

    # The indexes, made by ACE over the bytes as they now are.
    $db = $engine.OpenDatabase($outPath)
    try {
        foreach ($sql in @(
            'CREATE INDEX KAsc ON T (K)', 'CREATE INDEX KDesc ON T (K DESC)',
            'CREATE INDEX KForm ON T (K DESC, TForm)', 'CREATE INDEX FormK ON T (TForm, K)',
            'CREATE UNIQUE INDEX KU ON U (K)',
            'ALTER TABLE V ADD CONSTRAINT VPK PRIMARY KEY (K)',
            'DROP INDEX WPK ON W')) { $db.Execute($sql, $dbFailOnError) }
    }
    finally { $db.Close() }
}
finally {
    $db = $null
    try { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine) } catch { }
    $engine = $null; [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}
"{0} ({1:N0} bytes), made by ACE" -f $outPath, (Get-Item $outPath).Length
