<#
.SYNOPSIS
    Has Microsoft's own database engine index text values, and writes the keys it made to a .tsv file.

.DESCRIPTION
    The golden files under tests/JackcessDotNet.Tests/IndexKeys are made by this script. Microsoft's engine is the
    referee for text index keys: this library's keys must be the same bytes, or Access cannot find a row by them.

    The script creates a database with DAO, a table in it with a Long column and a Text(255) column, and three
    indexes: the number ascending, the text ascending and the text descending. It adds one row per value, closes the
    database, and reads the index pages back from the file itself, pairing each text key with its value through the
    number's index. Nothing of this library takes part, so the file it writes is evidence of what Microsoft's engine
    does and of nothing else.

    -Order chooses the database and with it the sort order of its text:
      General        a copy of tests/corpus/V2010/common1V2010.accdb, which Access 2010 made in its General order
                     (1033 version 1), opened by ACE (DAO.DBEngine.120). ACE gives a new table's text the
                     database's order; DAO's CreateDatabase and Access's default for a new database both
                     give General - Legacy, and no locale string was found that gives General
      GeneralLegacy  a Jet4 .mdb made by ACE: General - Legacy, 1033 version 0
      Access97       a Jet3 .mdb made by the 32-bit ACE 12.0 of Access 2007 (DAO.DBEngine.120): Access 97's order.
                     ACE 16 does not make or open a Jet3 file ("Could not find installable ISAM"). Jet 4.0
                     (-EngineProgId DAO.DBEngine.36) writes the same keys
    The script stops if the Text column's definition does not hold the order asked for.

    Access97 needs a 32-bit process where that ACE is the 32-bit one beside a 64-bit Office, as Jet 4.0 always is:
      C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -File tools\ace-text-keys.ps1 -Order Access97 ...

.PARAMETER Values
    A text file with one value per line, as its code points in hex separated by spaces ("0061 00E9 1F600"; a lone surrogate is allowed). Lines
    starting with # and empty lines are skipped.

.PARAMETER Out
    The .tsv to write: a header of # lines, then one line per value: the code points, the ascending key and the
    descending key, in hex.

.EXAMPLE
    pwsh tools/ace-text-keys.ps1 -Order General -Values values.txt -Out general-ace16.tsv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('General', 'GeneralLegacy', 'Access97')] [string]$Order,
    [Parameter(Mandatory)] [string]$Values,
    [Parameter(Mandatory)] [string]$Out,
    # Keep the database the keys were read from, at this path.
    [string]$KeepDatabase,
    # The DAO engine to use in place of the order's own, by ProgID: DAO.DBEngine.36 is Jet 4.0.
    [string]$EngineProgId,
    # Index the values in a Memo column in place of a Text(255) one: Access keys a Memo by its first 255 characters.
    [switch]$Memo
)

$ErrorActionPreference = 'Stop'

# C# 5, so that Windows PowerShell 5.1 (the only 32-bit host on most machines) compiles it too.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class JetIndexPages
{
    public sealed class Layout
    {
        public int PageSize, TdefNumCols, TdefNumRealIdx, TdefColsStart, ColSize, ColSortOrder, SortOrderSize;
        public int RealIdxSize, NameLenSize, IdxBlockSize, IdxBlockRoot;
        public int IdxPrev, IdxNext, IdxTail, IdxPrefix, IdxMask, IdxMaskSize;
        public Encoding Names;
    }

    public static Layout LayoutOf(byte[] file)
    {
        var l = new Layout();
        if (file[0x14] == 0)
        {
            // Jet3 (Access 97)
            l.PageSize = 2048; l.TdefNumCols = 25; l.TdefNumRealIdx = 31; l.TdefColsStart = 43; l.ColSize = 18;
            l.ColSortOrder = 9; l.SortOrderSize = 2; l.RealIdxSize = 8; l.NameLenSize = 1; l.IdxBlockSize = 39;
            l.IdxBlockRoot = 34; l.IdxPrev = 8; l.IdxNext = 12; l.IdxTail = 16; l.IdxPrefix = 20; l.IdxMask = 22;
            l.IdxMaskSize = 226; l.Names = Encoding.GetEncoding(1252);
        }
        else
        {
            l.PageSize = 4096; l.TdefNumCols = 45; l.TdefNumRealIdx = 51; l.TdefColsStart = 63; l.ColSize = 25;
            l.ColSortOrder = 11; l.SortOrderSize = 4; l.RealIdxSize = 12; l.NameLenSize = 2; l.IdxBlockSize = 52;
            l.IdxBlockRoot = 38; l.IdxPrev = 12; l.IdxNext = 16; l.IdxTail = 20; l.IdxPrefix = 24; l.IdxMask = 27;
            l.IdxMaskSize = 453; l.Names = Encoding.Unicode;
        }
        return l;
    }

    static byte[] Page(byte[] file, Layout l, int number)
    {
        var p = new byte[l.PageSize];
        Buffer.BlockCopy(file, number * l.PageSize, p, 0, l.PageSize);
        return p;
    }

    /// The page of the table definition that has a column of this name.
    public static int FindTdef(byte[] file, Layout l, string columnName)
    {
        byte[] needle = l.Names.GetBytes(columnName);
        for (int n = 2; n < file.Length / l.PageSize; n++)
        {
            int at = n * l.PageSize;
            if (file[at] != 0x02 || file[at + 1] != 0x01) continue;
            for (int i = at; i <= at + l.PageSize - needle.Length; i++)
            {
                int j = 0;
                while (j < needle.Length && file[i + j] == needle[j]) j++;
                if (j == needle.Length) return n;
            }
        }
        throw new InvalidDataException("No table definition has a column named " + columnName + ".");
    }

    /// A column's sort order as its definition holds it, in hex.
    public static string SortOrder(byte[] file, Layout l, int tdef, int column)
    {
        byte[] p = Page(file, l, tdef);
        int numReal = BitConverter.ToInt32(p, l.TdefNumRealIdx);
        return BitConverter.ToString(p, l.TdefColsStart + numReal * l.RealIdxSize + column * l.ColSize + l.ColSortOrder, l.SortOrderSize).Replace("-", "");
    }

    /// The root pages of the table's indexes, in the order its definition lists them.
    public static int[] IndexRoots(byte[] file, Layout l, int tdef)
    {
        byte[] p = Page(file, l, tdef);
        int numCols = BitConverter.ToInt16(p, l.TdefNumCols);
        int numReal = BitConverter.ToInt32(p, l.TdefNumRealIdx);
        int pos = l.TdefColsStart + numReal * l.RealIdxSize + numCols * l.ColSize;
        for (int i = 0; i < numCols; i++)
            pos += l.NameLenSize + (l.NameLenSize == 1 ? p[pos] : BitConverter.ToInt16(p, pos));
        var roots = new int[numReal];
        for (int i = 0; i < numReal; i++, pos += l.IdxBlockSize)
            roots[i] = BitConverter.ToInt32(p, pos + l.IdxBlockRoot);
        return roots;
    }

    /// Every leaf entry under a root, as (row pointer, key in hex), along the leaf chain.
    public static List<KeyValuePair<int, string>> LeafEntries(byte[] file, Layout l, int root)
    {
        var all = new List<KeyValuePair<int, string>>();
        int number = root;
        while (true)
        {
            byte[] p = Page(file, l, number);
            if (p[0] != 0x03 && p[0] != 0x04) throw new InvalidDataException("Page " + number + " is not an index page.");
            bool leaf = p[0] == 0x04;
            int trailer = leaf ? 4 : 8;
            // One byte in Jet3, where the next is the page's level; two in Jet4.
            int prefixLen = l.PageSize == 2048 ? p[l.IdxPrefix] : BitConverter.ToUInt16(p, l.IdxPrefix);
            int entriesStart = l.IdxMask + l.IdxMaskSize;
            byte[] prefix = null;
            int start = 0, firstSub = 0, count = 0;
            for (int i = 0; i < l.IdxMaskSize; i++)
            {
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((p[l.IdxMask + i] & (1 << bit)) == 0) continue;
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
                        all.Add(new KeyValuePair<int, string>(
                            (full[k] << 24) | (full[k + 1] << 16) | (full[k + 2] << 8) | full[k + 3],
                            BitConverter.ToString(full, 0, k).Replace("-", "")));
                    else if (count == 0)
                        firstSub = (full[k + 4] << 24) | (full[k + 5] << 16) | (full[k + 6] << 8) | full[k + 7];
                    count++;
                    start = end;
                }
            }
            if (leaf)
            {
                int next = BitConverter.ToInt32(p, l.IdxNext);
                if (next <= 0) return all;
                number = next;
            }
            else
            {
                // Down the leftmost child to the first leaf.
                number = count > 0 ? firstSub : BitConverter.ToInt32(p, l.IdxTail);
            }
        }
    }
}
'@

$valuesPath = (Resolve-Path $Values).Path
$lines = @(Get-Content $valuesPath | Where-Object { $_ -and -not $_.StartsWith('#') })
$texts = foreach ($line in $lines) {
    -join ($line.Trim() -split ' +' | ForEach-Object {
        $codePoint = [Convert]::ToInt32($_, 16)
        if ($codePoint -ge 0xD800 -and $codePoint -le 0xDFFF) { [string][char]$codePoint } else { [char]::ConvertFromUtf32($codePoint) }
    })
}

switch ($Order) {
    'General'       { $progId = 'DAO.DBEngine.120'; $version = 128; $extension = '.accdb'; $expected = '09040001' }
    'GeneralLegacy' { $progId = 'DAO.DBEngine.120'; $version = 64;  $extension = '.mdb';   $expected = '09040000' }
    'Access97'      { $progId = 'DAO.DBEngine.120'; $version = 32;  $extension = '.mdb';   $expected = '0904' }
}
if ($EngineProgId) { $progId = $EngineProgId }

$path = if ($KeepDatabase) { [IO.Path]::GetFullPath($KeepDatabase) }
        else { Join-Path ([IO.Path]::GetTempPath()) ("ace-text-keys-{0}{1}" -f [Guid]::NewGuid().ToString('N'), $extension) }
if (Test-Path $path) { Remove-Item $path }

$engine = New-Object -ComObject $progId
$engineVersion = $engine.Version
try {
    if ($Order -eq 'General') {
        Copy-Item (Join-Path $PSScriptRoot '..\tests\corpus\V2010\common1V2010.accdb') $path
        $db = $engine.OpenDatabase($path)
    }
    else { $db = $engine.CreateDatabase($path, ';LANGID=0x0409;CP=1252;COUNTRY=0', $version) }
    try {
        $db.Execute('CREATE TABLE TextKeys (KeyNumber LONG, KeyText ' + $(if ($Memo) { 'MEMO' } else { 'TEXT(255)' }) + ')')
        $db.Execute('CREATE INDEX ByNumber ON TextKeys (KeyNumber)')
        $db.Execute('CREATE INDEX Ascending ON TextKeys (KeyText ASC)')
        $db.Execute('CREATE INDEX Descending ON TextKeys (KeyText DESC)')
        $rs = $db.OpenRecordset('TextKeys', 1)   # dbOpenTable
        $number = $rs.Fields.Item('KeyNumber'); $text = $rs.Fields.Item('KeyText')
        for ($i = 0; $i -lt $texts.Count; $i++) {
            $rs.AddNew()
            $number.Value = $i
            $text.Value = [string]$texts[$i]
            $rs.Update()
        }
        $rs.Close()
    }
    finally { $db.Close() }
}
finally {
    # Windows PowerShell hands back a wrapper that is not always a COM object to release; collecting frees it.
    try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($engine) } catch { }
    $engine = $null; [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}

$file = [IO.File]::ReadAllBytes($path)
$layout = [JetIndexPages]::LayoutOf($file)
$tdef = [JetIndexPages]::FindTdef($file, $layout, 'KeyNumber')
$sortOrder = [JetIndexPages]::SortOrder($file, $layout, $tdef, 1)
if ($sortOrder -ne $expected) { throw "KeyText sorts in $sortOrder, not $expected ($Order): no keys written." }

$byNumber = $null; $ascending = @{}; $descending = @{}
foreach ($root in [JetIndexPages]::IndexRoots($file, $layout, $tdef)) {
    $entries = [JetIndexPages]::LeafEntries($file, $layout, $root)
    if ($entries.Count -ne $texts.Count) { throw "An index holds $($entries.Count) entries for $($texts.Count) values." }
    $first = $entries[0].Value
    if ($first.StartsWith('80')) { foreach ($e in $entries) { $descending[$e.Key] = $e.Value } }
    elseif ($first -eq '7F80000000') { $byNumber = $entries }
    else { foreach ($e in $entries) { $ascending[$e.Key] = $e.Value } }
}
if (-not $byNumber -or $ascending.Count -ne $texts.Count -or $descending.Count -ne $texts.Count) { throw 'The three indexes were not all found.' }

$made = switch ($Order) {
    'General'       { "General (Access 2010 and later; sort order 1033 version 1) text index keys written by ACE $engineVersion ($progId) into a copy of the corpus's common1V2010.accdb, an Access 2010 database in that order" }
    'GeneralLegacy' { "General - Legacy (sort order 1033 version 0) text index keys written by ACE $engineVersion ($progId) into a Jet4 .mdb it created" }
    'Access97'      { "Access 97 (Jet3; sort order 1033) text index keys written by $(if ($progId -eq 'DAO.DBEngine.36') { 'Jet 4.0, DAO' } else { 'ACE' }) $engineVersion ($progId, 32-bit) into a Jet3 .mdb it created" }
}
$output = New-Object System.Collections.Generic.List[string]
$output.Add("# $made,")
$output.Add('# ascending (7F...) and descending (80...). One value per line: its code points in hex, then the two')
$output.Add('# keys. Made by tools/ace-text-keys.ps1, which has the engine index each value and reads the index pages back.')
foreach ($e in $byNumber) {
    $i = [Convert]::ToInt32($e.Value.Substring(2), 16) -bxor 0x80000000
    $output.Add(("{0}`t{1}`t{2}" -f $lines[$i].Trim(), $ascending[$e.Key], $descending[$e.Key]))
}
# Line feeds, as the repository keeps text.
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Out), ($output -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))
if (-not $KeepDatabase) { Remove-Item $path }
"$($texts.Count) values, sort order $sortOrder, $progId $engineVersion -> $Out"
