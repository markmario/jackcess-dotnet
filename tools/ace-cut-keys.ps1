<#
.SYNOPSIS
    Has Microsoft's own database engine index rows whose keys of several columns are cut at 510 bytes, and writes
    the entries it made to a .tsv file.

.DESCRIPTION
    Access keeps no index key longer than 510 bytes: it cuts a longer one there and ends it in a two-byte digest
    of what it cut off. The golden files tests/JackcessDotNet.Tests/IndexKeys/cut-keys-*.tsv are made by this
    script, and CutKeyTests holds this library's keys to them byte for byte (#56).

    The script creates a database with DAO, and in it a table CutKeys (CutKeyRow LONG primary key, A TEXT(255),
    B TEXT(255), K LONG, J LONG) with seven indexes: a text key cut in the first, middle or last column, before a
    Long ascending and descending, after another text, and in a unique index. It adds 32 rows, closes the
    database, and reads each index's entries back from the file itself, pairing each with its row through the
    primary key. Nothing of this library takes part.

    The rows: sixteen values of A, each twice - once with K 1, B 'b', and once with K 70000, B 255 of U+00E8 - and
    J three times the row's number. The values of A have keys that end either side of 508 and 510 bytes in each
    order (so that the cut falls in A, or in the K after it), and longer: 'short', 255 of U+03A9, 200 of U+03AA,
    255 of 'a', U+00E9 (247 to 253 of them) then 'a' to 255 characters, 'a' 252 and 253 times, and one U+00E9 then
    251 to 253 of 'a'. The two rows of a value of A have values of J that differ only in their last byte.

    -Order chooses the database and with it the sort order of its text, as tools/ace-text-keys.ps1 does:
      General        a copy of tests/corpus/V2010/common1V2010.accdb (Access 2010's General, 1033 version 1)
      GeneralLegacy  a Jet4 .mdb made by ACE: General - Legacy, 1033 version 0
    The script stops if the text columns' definitions do not hold the order asked for.

.PARAMETER Out
    The .tsv to write: a header of # lines, then one line per entry: the index, its columns (" desc" after a
    descending one), the row's number, A and B as runs of code points ("00E9*248 0061*7"; "-" for none), K, J, and
    the key Access wrote, in hex.

.EXAMPLE
    pwsh tools/ace-cut-keys.ps1 -Order GeneralLegacy -Out tests/JackcessDotNet.Tests/IndexKeys/cut-keys-general-legacy-ace16.tsv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('General', 'GeneralLegacy')] [string]$Order,
    [Parameter(Mandatory)] [string]$Out,
    # Keep the database the entries were read from, at this path.
    [string]$KeepDatabase
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class JetCutKeys
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

    public sealed class Index
    {
        public string Name;
        public int Root;
        public List<string> Columns = new List<string>();
    }

    /// The table's columns' sort orders (hex, by name) and its indexes: name, root page, and columns in key order.
    public static List<Index> Indexes(byte[] file, int tdef, Dictionary<string, string> sortOrders)
    {
        byte[] p = Page(file, tdef);
        int numCols = BitConverter.ToInt16(p, 45);
        int numIdx = BitConverter.ToInt32(p, 47);
        int numReal = BitConverter.ToInt32(p, 51);
        int pos = 63 + numReal * 12;
        var numbers = new int[numCols];
        var orders = new string[numCols];
        for (int i = 0; i < numCols; i++, pos += 25)
        {
            numbers[i] = BitConverter.ToInt16(p, pos + 5);
            orders[i] = BitConverter.ToString(p, pos + 11, 4).Replace("-", "");
        }
        var names = new Dictionary<int, string>();
        for (int i = 0; i < numCols; i++)
        {
            int length = BitConverter.ToInt16(p, pos);
            names[numbers[i]] = Encoding.Unicode.GetString(p, pos + 2, length);
            sortOrders[names[numbers[i]]] = orders[i];
            pos += 2 + length;
        }
        var roots = new int[numReal];
        var columns = new List<string>[numReal];
        for (int r = 0; r < numReal; r++, pos += 52)
        {
            columns[r] = new List<string>();
            for (int c = 0; c < 10; c++)
            {
                int number = BitConverter.ToUInt16(p, pos + 4 + c * 3);
                if (number == 0xFFFF) continue;
                columns[r].Add(names[number] + ((p[pos + 4 + c * 3 + 2] & 0x01) != 0 ? "" : " desc"));
            }
            roots[r] = BitConverter.ToInt32(p, pos + 38);
        }
        var indexes = new List<Index>();
        var real = new int[numIdx];
        for (int i = 0; i < numIdx; i++, pos += 28) real[i] = BitConverter.ToInt32(p, pos + 8);
        for (int i = 0; i < numIdx; i++)
        {
            int length = BitConverter.ToInt16(p, pos);
            var index = new Index { Name = Encoding.Unicode.GetString(p, pos + 2, length), Root = roots[real[i]] };
            index.Columns.AddRange(columns[real[i]]);
            indexes.Add(index);
            pos += 2 + length;
        }
        return indexes;
    }

    /// Every leaf entry under a root, as (row pointer, key in hex), along the leaf chain.
    public static List<KeyValuePair<int, string>> LeafEntries(byte[] file, int root)
    {
        var all = new List<KeyValuePair<int, string>>();
        int number = root;
        while (true)
        {
            byte[] p = Page(file, number);
            if (p[0] != 0x03 && p[0] != 0x04) throw new InvalidDataException("Page " + number + " is not an index page.");
            bool leaf = p[0] == 0x04;
            int trailer = leaf ? 4 : 8;
            int prefixLen = BitConverter.ToUInt16(p, 24);
            const int mask = 27, maskSize = 453, entriesStart = mask + maskSize;
            byte[] prefix = null;
            int start = 0, firstSub = 0, count = 0;
            for (int i = 0; i < maskSize; i++)
            {
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
                int next = BitConverter.ToInt32(p, 16);
                if (next <= 0) return all;
                number = next;
            }
            else number = count > 0 ? firstSub : BitConverter.ToInt32(p, 20);
        }
    }
}
'@

# The rows: a value as runs of one code point, "00E9*248 0061*7".
function Text([string]$runs) {
    -join ($runs -split ' ' | ForEach-Object { $cp, $n = $_ -split '\*'; [string][char][Convert]::ToInt32($cp, 16) * $(if ($n) { [int]$n } else { 1 }) })
}
$aValues = @(
    '0073 0068 006F 0072 0074', '03A9*255', '03AA*200', '0061*255',
    '00E9*247 0061*8', '00E9*248 0061*7', '00E9*249 0061*6', '00E9*250 0061*5', '00E9*251 0061*4', '00E9*252 0061*3',
    '00E9*253 0061*2', '0061*252', '0061*253', '00E9 0061*251', '00E9 0061*252', '00E9 0061*253')
$rows = New-Object System.Collections.Generic.List[object]
foreach ($a in $aValues) {
    foreach ($second in $false, $true) {
        $id = $rows.Count + 1
        $rows.Add([pscustomobject]@{ Id = $id; A = $a; B = $(if ($second) { '00E8*255' } else { '0062' }); K = $(if ($second) { 70000 } else { 1 }); J = $id * 3 })
    }
}
$indexes = [ordered]@{
    ByAK = 'A, K'; ByADescK = 'A DESC, K'; ByAKDesc = 'A, K DESC'; ByKA = 'K, A'; ByKAJ = 'K, A, J'; ByABK = 'A, B, K'
    UniqueAJ = 'A, J'
}

switch ($Order) {
    'General'       { $extension = '.accdb'; $expected = '09040001' }
    'GeneralLegacy' { $extension = '.mdb';   $expected = '09040000' }
}
$path = if ($KeepDatabase) { [IO.Path]::GetFullPath($KeepDatabase) }
        else { Join-Path ([IO.Path]::GetTempPath()) ("ace-cut-keys-{0}{1}" -f [Guid]::NewGuid().ToString('N'), $extension) }
if (Test-Path $path) { Remove-Item $path }

$engine = New-Object -ComObject DAO.DBEngine.120
$engineVersion = $engine.Version
try {
    if ($Order -eq 'General') {
        Copy-Item (Join-Path $PSScriptRoot '..\tests\corpus\V2010\common1V2010.accdb') $path
        Set-ItemProperty $path -Name IsReadOnly -Value $false
        $db = $engine.OpenDatabase($path)
    }
    else { $db = $engine.CreateDatabase($path, ';LANGID=0x0409;CP=1252;COUNTRY=0', 64) }
    try {
        $db.Execute('CREATE TABLE CutKeys (CutKeyRow LONG CONSTRAINT PrimaryKey PRIMARY KEY, A TEXT(255), B TEXT(255), K LONG, J LONG)')
        foreach ($name in $indexes.Keys) {
            $db.Execute("CREATE $(if ($name -like 'Unique*') { 'UNIQUE ' })INDEX $name ON CutKeys ($($indexes[$name]))")
        }
        $rs = $db.OpenRecordset('CutKeys', 1)   # dbOpenTable
        foreach ($row in $rows) {
            $rs.AddNew()
            # Each value cast where it is set: PowerShell otherwise hands a Field's Value the type of the last one set.
            $rs.Fields.Item('CutKeyRow').Value = [int]$row.Id
            $rs.Fields.Item('A').Value = [string](Text $row.A)
            $rs.Fields.Item('B').Value = [string](Text $row.B)
            $rs.Fields.Item('K').Value = [int]$row.K
            $rs.Fields.Item('J').Value = [int]$row.J
            $rs.Update()
        }
        $rs.Close()
    }
    finally { $db.Close() }
}
finally {
    try { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($engine) } catch { }
    $engine = $null; [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}

$file = [IO.File]::ReadAllBytes($path)
$tdef = [JetCutKeys]::FindTdef($file, 'CutKeyRow')
$sortOrders = New-Object 'System.Collections.Generic.Dictionary[string,string]'
$found = [JetCutKeys]::Indexes($file, $tdef, $sortOrders)
foreach ($column in 'A', 'B') {
    if ($sortOrders[$column] -ne $expected) { throw "$column sorts in $($sortOrders[$column]), not $expected ($Order): no keys written." }
}

# The row of each entry, through the primary key: 7F and the number with its sign bit flipped.
$primary = $found | Where-Object { $_.Name -eq 'PrimaryKey' }
$idAt = @{}
foreach ($e in [JetCutKeys]::LeafEntries($file, $primary.Root)) { $idAt[$e.Key] = [Convert]::ToInt32($e.Value.Substring(2), 16) -bxor 0x80000000 }
if ($idAt.Count -ne $rows.Count) { throw "The primary key holds $($idAt.Count) entries for $($rows.Count) rows." }

$output = New-Object System.Collections.Generic.List[string]
$made = switch ($Order) {
    'General'       { "General (sort order 1033 version 1) keys written by ACE $engineVersion (DAO.DBEngine.120) into a copy of the corpus's common1V2010.accdb" }
    'GeneralLegacy' { "General - Legacy (sort order 1033 version 0) keys written by ACE $engineVersion (DAO.DBEngine.120) into a Jet4 .mdb it created" }
}
$output.Add("# Entries of indexes of several columns whose keys are cut at 510 bytes: $made.")
$output.Add('# One line per entry: the index, its columns, the row, A and B as runs of code points, K, J, and the key in hex.')
$output.Add('# Made by tools/ace-cut-keys.ps1, which has the engine index the rows and reads the index pages back.')
foreach ($name in $indexes.Keys) {
    $index = $found | Where-Object { $_.Name -eq $name }
    $entries = [JetCutKeys]::LeafEntries($file, $index.Root)
    if ($entries.Count -ne $rows.Count) { throw "$name holds $($entries.Count) entries for $($rows.Count) rows." }
    foreach ($e in ($entries | Sort-Object { $idAt[$_.Key] })) {
        $row = $rows[$idAt[$e.Key] - 1]
        $output.Add(("{0}`t{1}`t{2}`t{3}`t{4}`t{5}`t{6}`t{7}" -f $name, ($index.Columns -join ','), $row.Id, $row.A, $row.B, $row.K, $row.J, $e.Value))
    }
}
# Line feeds, as the repository keeps text.
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Out), ($output -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))
if (-not $KeepDatabase) { Remove-Item $path }
"$($output.Count - 3) entries, sort order $expected, ACE $engineVersion -> $Out"
