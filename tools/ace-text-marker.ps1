<#
.SYNOPSIS
    Has Microsoft's own database engine store text whose bytes begin FF FE, and writes what it stored and what it
    reads back to a .tsv file.

.DESCRIPTION
    tests/JackcessDotNet.Tests/Assets/ace-text-marker.tsv is made by this script (#93). FF FE at the start of a text
    value's bytes is the mark of compressed text, and the UTF-16LE of U+FEFF is FF FE too; the script finds out
    which bytes Access writes for text that begins with U+FEFF, or with another character whose low byte is FF, and
    how it reads such bytes back.

    For each file format - Access 2000 (.mdb, DAO version 64) and Access 2007 (.accdb, version 128) - each kind of
    column - Text(255) and Memo, each with Unicode compression (TC, MC) and without (TU, MU) - each writer - ACE's
    OLE DB provider, an INSERT with the value as a parameter, and DAO's AddNew - and each value, it makes a new
    database with one table holding one row. It then reads the value's bytes from the file itself, from the row and
    from the long-value pages, and the value as ACE and as DAO read it back from the table, each opening the file
    afresh: ACE with a SELECT that does not sort, DAO with a table-type recordset. Nothing of this library takes
    part. (A query that sorts, and a DAO snapshot, read a copy ACE has written again with its own rules, and can
    read a Text value of a column without Unicode compression differently from the table.)

    Each line: format, column, writer, the value (its UTF-16 code units in hex, "0061*3" for three of one), where
    the bytes are (row: a Text value; inline: a Memo kept in the row; page: a Memo in one long-value row; chain: a
    Memo in several), the bytes (plain: UTF-16LE; runs: compressed, 0xFF 0xFE then runs of one byte a character for
    0x01-0xFF and two for the rest, a 0x00 between runs; otherwise hex), and what ACE and DAO read ("same", the
    code units read, or the error).

    With -Template it also saves an Access 2000 .mdb that DAO made, with an empty table T of the four columns (Id,
    TC, TU, MC, MU), in which the tests have the engine write the same values
    (tests/JackcessDotNet.Tests/Assets/ace-text-marker.mdb).

    Run it in Windows PowerShell 5.1, which has System.Data.OleDb, with a 64-bit ACE:
      powershell.exe -File tools\ace-text-marker.ps1 -Out <full path>\ace-text-marker.tsv -Template <full path>\ace-text-marker.mdb
#>
param(
    [Parameter(Mandatory = $true)] [string] $Out,
    [string] $Template,
    [string] $Provider = 'Microsoft.ACE.OLEDB.16.0'
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($Out)) { throw "Out must be a full path." }
Add-Type -AssemblyName System.Data

# C# 5, so that Windows PowerShell 5.1 compiles it.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

public static class TextMarkerFile
{
    const int PageSize = 4096;
    static int U16(byte[] b, int i) { return b[i] | b[i + 1] << 8; }
    static int U24(byte[] b, int i) { return b[i] | b[i + 1] << 8 | b[i + 2] << 16; }
    static int I32(byte[] b, int i) { return b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24; }

    public static string Parse(string spec)
    {
        var text = new StringBuilder();
        foreach (var token in spec.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split('*');
            text.Append((char)Convert.ToInt32(parts[0], 16), parts.Length > 1 ? int.Parse(parts[1]) : 1);
        }
        return text.ToString();
    }

    public static string Units(string text)
    {
        return string.Join(" ", text.Select(c => ((int)c).ToString("X4")).ToArray());
    }

    public static byte[] Plain(string text)
    {
        var bytes = new byte[text.Length * 2];
        for (int i = 0; i < text.Length; i++) { bytes[2 * i] = (byte)text[i]; bytes[2 * i + 1] = (byte)(text[i] >> 8); }
        return bytes;
    }

    public static byte[] Runs(string text)
    {
        var bytes = new List<byte> { 0xFF, 0xFE };
        bool compressed = true;
        foreach (char c in text)
        {
            bool latin = c >= 0x01 && c <= 0xFF;
            if (latin != compressed) { bytes.Add(0); compressed = latin; }
            bytes.Add((byte)c);
            if (!latin) bytes.Add((byte)(c >> 8));
        }
        return bytes.ToArray();
    }

    // Row 'row' of page 'page': rows are kept from the end of the page backwards.
    static byte[] RowBytes(byte[] file, int page, int row)
    {
        int at = page * PageSize;
        if (row >= U16(file, at + 12)) throw new InvalidDataException("no row " + row + " on page " + page);
        int start = U16(file, at + 14 + 2 * row) & 0x1FFF;
        int end = row == 0 ? PageSize : U16(file, at + 14 + 2 * (row - 1)) & 0x1FFF;
        var bytes = new byte[end - start];
        Array.Copy(file, at + start, bytes, 0, bytes.Length);
        return bytes;
    }

    static int Find(byte[] hay, byte[] needle, int from)
    {
        for (int i = from; i + needle.Length <= hay.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    // The bytes the file keeps for the value of column 'column' in the one row of the table that has it: the
    // table definition naming the column, the data page of that definition, its row 0, the row's one
    // variable-length value, and for a Memo what its reference points to.
    public static byte[] Stored(string path, string column, bool memo, out string place)
    {
        byte[] file = File.ReadAllBytes(path);
        byte[] name = Encoding.Unicode.GetBytes(column);
        int tdef = -1;
        for (int at = Find(file, name, 0); at >= 0 && tdef < 0; at = Find(file, name, at + 1))
            if (file[at / PageSize * PageSize] == 0x02) tdef = at / PageSize;
        if (tdef < 0) throw new InvalidDataException("no table definition names " + column);
        int data = -1;
        for (int page = 1; page < file.Length / PageSize && data < 0; page++)
            if (file[page * PageSize] == 0x01 && I32(file, page * PageSize + 4) == tdef) data = page;
        if (data < 0) throw new InvalidDataException("no data page for the table at page " + tdef);
        byte[] row = RowBytes(file, data, 0);
        int columns = U16(row, 0);
        int varLenAt = row.Length - (columns + 7) / 8 - 2;
        if (U16(row, varLenAt) != 1) throw new InvalidDataException("the row has " + U16(row, varLenAt) + " variable-length values, not one");
        int start = U16(row, varLenAt - 2), end = U16(row, varLenAt - 4);
        byte[] value = row.Skip(start).Take(end - start).ToArray();
        if (!memo) { place = "row"; return value; }

        int length = U24(value, 0), type = value[3] & 0xC0;
        if (type == 0x80) { place = "inline"; return value.Skip(12).Take(length).ToArray(); }
        if (type == 0x40) { place = "page"; return RowBytes(file, U24(value, 5), value[4]).Take(length).ToArray(); }
        place = "chain";
        var bytes = new List<byte>();
        int lvalRow = value[4], lvalPage = U24(value, 5);
        while (bytes.Count < length && lvalPage != 0)
        {
            byte[] chunk = RowBytes(file, lvalPage, lvalRow);
            lvalRow = chunk[0];
            lvalPage = U24(chunk, 1);
            bytes.AddRange(chunk.Skip(4));
        }
        return bytes.Take(length).ToArray();
    }

    public static string Describe(string text, byte[] stored)
    {
        if (stored.SequenceEqual(Plain(text))) return "plain";
        if (stored.SequenceEqual(Runs(text))) return "runs";
        return "hex " + BitConverter.ToString(stored).Replace("-", "");
    }

    public static string Read(string text, object read)
    {
        if (read == null || read is DBNull) return "null";
        string got = (string)read;
        return got == text ? "same" : Units(got);
    }
}
'@ -Language CSharp

$values = @(
    'FEFF',
    'FEFF 0061',
    'FEFF 0061*3',
    'FEFF 0061*4',
    'FEFF 0061 0062 0051 0031 005A',
    'FEFF 0061 0062 30C6 0051 0032 005A',
    'FEFF 0061*5 30C6',
    'FEFF 30C6 0045 30C6 0045*6',
    'FEFF FEFF 0061*4',
    'FEFF 30C6*3',
    'FEFF 4141*10',
    'FEFF 0100 0061*6',
    'FEFF 0061*6 0000',
    '01FF 0061 0062',
    '01FF 0051 0039 005A 30C6',
    '00FF 0061 0062',
    'FEFF 30C6*40',
    'FEFF 30C6*40 0061*4',
    'FEFF 4141*50',
    'FEFF 0061*1000',
    'FEFF 0061*1907',
    'FEFF 0061*1908',
    'FEFF 30C6*1906',
    'FEFF 30C6*3000'
)
$columns = [ordered]@{ TC = 'VARCHAR(255) WITH COMPRESSION'; TU = 'VARCHAR(255)'; MC = 'LONGTEXT WITH COMPRESSION'; MU = 'LONGTEXT' }
$formats = [ordered]@{ mdb = 64; accdb = 128 }

$dir = Join-Path ([IO.Path]::GetTempPath()) ('ace-text-marker-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir | Out-Null
$dbe = New-Object -ComObject DAO.DBEngine.120
$lines = New-Object 'System.Collections.Generic.List[string]'
$lines.Add("# tools/ace-text-marker.ps1, $Provider and DAO.DBEngine.120")
$lines.Add("# format`tcolumn`twriter`tvalue`tplace`tstored`tace reads`tdao reads")
$n = 0
try {
    foreach ($format in $formats.Keys) {
        foreach ($column in $columns.Keys) {
            $memo = $column.StartsWith('M')
            foreach ($writer in 'ace', 'dao') {
                foreach ($spec in $values) {
                    $text = [TextMarkerFile]::Parse($spec)
                    if (-not $memo -and $text.Length -gt 255) { continue }
                    $n++
                    $path = Join-Path $dir "v$n.$format"
                    $db = $dbe.CreateDatabase($path, ';LANGID=0x0409;CP=1252;COUNTRY=0', $formats[$format])
                    $db.Close()
                    $connection = New-Object System.Data.OleDb.OleDbConnection("Provider=$Provider;OLE DB Services=-4;Data Source=$path")
                    $connection.Open()
                    try {
                        $c = $connection.CreateCommand()
                        $c.CommandText = "CREATE TABLE T (Id LONG CONSTRAINT pk PRIMARY KEY, MarkerValue $($columns[$column]))"
                        [void]$c.ExecuteNonQuery()
                        if ($writer -eq 'ace') {
                            $ins = $connection.CreateCommand()
                            $ins.CommandText = 'INSERT INTO T (Id, MarkerValue) VALUES (1, ?)'
                            $p = New-Object System.Data.OleDb.OleDbParameter('v', [System.Data.OleDb.OleDbType]::LongVarWChar); $p.Value = $text
                            [void]$ins.Parameters.Add($p)
                            [void]$ins.ExecuteNonQuery()
                        }
                    } finally { $connection.Dispose() }
                    if ($writer -eq 'dao') {
                        $db = $dbe.OpenDatabase($path)
                        $rs = $db.OpenRecordset('T', 1)
                        $rs.AddNew()
                        $rs.Fields.Item('Id').Value = 1
                        $field = $rs.Fields.Item('MarkerValue')
                        [void]$field.GetType().InvokeMember('Value', [Reflection.BindingFlags]::SetProperty, $null, $field, @($text))
                        $rs.Update()
                        $rs.Close(); $db.Close()
                    }
                    [System.Data.OleDb.OleDbConnection]::ReleaseObjectPool()

                    $place = ''
                    $stored = [TextMarkerFile]::Describe($text, [TextMarkerFile]::Stored($path, 'MarkerValue', $memo, [ref]$place))

                    $connection = New-Object System.Data.OleDb.OleDbConnection("Provider=$Provider;OLE DB Services=-4;Data Source=$path")
                    try {
                        $connection.Open()
                        $sel = $connection.CreateCommand(); $sel.CommandText = 'SELECT MarkerValue FROM T'
                        $ace = [TextMarkerFile]::Read($text, $sel.ExecuteScalar())
                    } catch { $ace = 'error' } finally { $connection.Dispose() }
                    [System.Data.OleDb.OleDbConnection]::ReleaseObjectPool()

                    $db = $dbe.OpenDatabase($path, $false, $true)
                    try {
                        $rs = $db.OpenRecordset('T', 1)
                        $dao = [TextMarkerFile]::Read($text, $rs.Fields.Item('MarkerValue').Value)
                        $rs.Close()
                    } catch { $dao = 'error' } finally { $db.Close() }

                    $lines.Add("$format`t$column`t$writer`t$spec`t$place`t$stored`t$ace`t$dao")
                }
            }
        }
    }
    if ($Template) {
        if (Test-Path $Template) { Remove-Item $Template }
        $db = $dbe.CreateDatabase($Template, ';LANGID=0x0409;CP=1252;COUNTRY=0', 64)
        $db.Close()
        $connection = New-Object System.Data.OleDb.OleDbConnection("Provider=$Provider;OLE DB Services=-4;Data Source=$Template")
        $connection.Open()
        try {
            $c = $connection.CreateCommand()
            $c.CommandText = 'CREATE TABLE T (Id LONG CONSTRAINT pk PRIMARY KEY, ' + (($columns.Keys | ForEach-Object { "$_ $($columns[$_])" }) -join ', ') + ')'
            [void]$c.ExecuteNonQuery()
        } finally { $connection.Dispose() }
        [System.Data.OleDb.OleDbConnection]::ReleaseObjectPool()
    }
} finally {
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($dbe)
    Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
}

[IO.File]::WriteAllText($Out, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
Write-Host "written $($lines.Count) lines to $Out"
