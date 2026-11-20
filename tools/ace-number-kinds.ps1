<#
.SYNOPSIS
    Has Microsoft's own database engine store a Boolean, text, a date, a GUID and bytes in a column of every number
    type, and writes what it stored to a .tsv file.

.DESCRIPTION
    tests/JackcessDotNet.Tests/Assets/ace-number-kinds.tsv is made by this script (#75). It creates an .accdb with
    DAO (version 256, for a Large Number), a table for each number type - Byte, Integer, Long Integer, Large Number,
    Single, Double, Currency and Decimal(18, 4) - and has ACE's OLE DB provider INSERT each value with a typed
    parameter of its own kind, and DAO's AddNew assign it to the column. It writes what each stored, as ACE and DAO
    read it back in the invariant culture, or that it refused the value. Text is read by Access in the machine's
    locale: the header says the culture the script ran in. Nothing of this library takes part.

    Run it in Windows PowerShell 5.1, which has System.Data.OleDb, with a 64-bit ACE:
      powershell.exe -File tools\ace-number-kinds.ps1 -Out <full path>\ace-number-kinds.tsv
#>
param(
    [Parameter(Mandatory = $true)] [string] $Out,
    [string] $Provider = 'Microsoft.ACE.OLEDB.16.0'
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($Out)) { throw "Out must be a full path." }
Add-Type -AssemblyName System.Data
$inv = [Globalization.CultureInfo]::InvariantCulture

$types = [ordered]@{ Byte = 'BYTE'; Integer = 'SHORT'; Long = 'LONG'; LargeNumber = 'BIGINT'; Single = 'REAL'; Double = 'FLOAT'; Currency = 'CURRENCY'; Decimal = 'DECIMAL(18, 4)' }
$date = New-Object DateTime 2020, 1, 2, 12, 0, 0
$cases = @(
    @('bool', 'True', [System.Data.OleDb.OleDbType]::Boolean, $true),
    @('bool', 'False', [System.Data.OleDb.OleDbType]::Boolean, $false),
    @('text', '12', [System.Data.OleDb.OleDbType]::VarWChar, '12'),
    @('text', '1,5', [System.Data.OleDb.OleDbType]::VarWChar, '1,5'),
    @('text', '1.5', [System.Data.OleDb.OleDbType]::VarWChar, '1.5'),
    @('text', '2.5', [System.Data.OleDb.OleDbType]::VarWChar, '2.5'),
    @('text', ' 7 ', [System.Data.OleDb.OleDbType]::VarWChar, ' 7 '),
    @('text', '-3', [System.Data.OleDb.OleDbType]::VarWChar, '-3'),
    @('text', '1e2', [System.Data.OleDb.OleDbType]::VarWChar, '1e2'),
    @('text', '$5', [System.Data.OleDb.OleDbType]::VarWChar, '$5'),
    @('text', '(5)', [System.Data.OleDb.OleDbType]::VarWChar, '(5)'),
    @('text', 'True', [System.Data.OleDb.OleDbType]::VarWChar, 'True'),
    @('text', 'abc', [System.Data.OleDb.OleDbType]::VarWChar, 'abc'),
    @('text', '(empty)', [System.Data.OleDb.OleDbType]::VarWChar, ''),
    @('text', '1 000', [System.Data.OleDb.OleDbType]::VarWChar, '1 000'),
    @('date', '2020-01-02 12:00', [System.Data.OleDb.OleDbType]::Date, $date),
    @('guid', '{guid}', [System.Data.OleDb.OleDbType]::Guid, [guid]'6f9619ff-8b86-d011-b42d-00c04fc964ff'),
    @('bytes', '0x0102', [System.Data.OleDb.OleDbType]::VarBinary, [byte[]](1, 2))
)

$dir = Join-Path ([IO.Path]::GetTempPath()) ('ace-kinds-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir | Out-Null
$path = Join-Path $dir 'kinds.accdb'
$dbe = New-Object -ComObject DAO.DBEngine.120
$db = $dbe.CreateDatabase($path, ';LANGID=0x0409;CP=1252;COUNTRY=0', 256)
$db.Close()

$lines = New-Object 'System.Collections.Generic.List[string]'
$lines.Add("# culture $([Globalization.CultureInfo]::CurrentCulture.Name); $Provider; who`ttype`tkind`tgiven`tstored or refused")
$connection = New-Object System.Data.OleDb.OleDbConnection("Provider=$Provider;OLE DB Services=-4;Data Source=$path")
$connection.Open()
$id = 0
try {
    foreach ($name in $types.Keys) {
        $c = $connection.CreateCommand()
        $c.CommandText = "CREATE TABLE [$name] (Id LONG CONSTRAINT [pk$name] PRIMARY KEY, V $($types[$name]))"
        [void]$c.ExecuteNonQuery()
        foreach ($case in $cases) {
            $id++
            $ins = $connection.CreateCommand()
            $ins.CommandText = "INSERT INTO [$name] (Id, V) VALUES (?, ?)"
            $ip = New-Object System.Data.OleDb.OleDbParameter('id', [System.Data.OleDb.OleDbType]::Integer); $ip.Value = $id
            $vp = New-Object System.Data.OleDb.OleDbParameter('v', $case[2]); $vp.Value = $case[3]
            [void]$ins.Parameters.Add($ip); [void]$ins.Parameters.Add($vp)
            $result = $null
            try { [void]$ins.ExecuteNonQuery() } catch { $result = 'refused: ' + $_.Exception.InnerException.Message }
            if ($result -eq $null) {
                $sel = $connection.CreateCommand(); $sel.CommandText = "SELECT V FROM [$name] WHERE Id = $id"
                $v = $sel.ExecuteScalar()
                if ($v -is [DBNull]) { $result = 'null' } else { $result = [Convert]::ToString($v, $inv) }
            }
            $lines.Add("ace`t$name`t$($case[0])`t$($case[1])`t$result")
        }
    }
} finally { $connection.Dispose() }

$db = $dbe.OpenDatabase($path)
try {
    $n = 1000
    foreach ($name in $types.Keys) {
        foreach ($case in $cases) {
            $n++
            $rs = $db.OpenRecordset($name, 1)
            $result = ''
            try {
                $rs.AddNew()
                $rs.Fields.Item('Id').Value = [int]$n
                $field = $rs.Fields.Item('V')
                [void]$field.GetType().InvokeMember('Value', [Reflection.BindingFlags]::SetProperty, $null, $field, @($case[3]))
                $rs.Update()
                $r2 = $db.OpenRecordset("SELECT V FROM [$name] WHERE Id = $n", 4)
                $v = $r2.Fields.Item('V').Value
                if ($v -is [DBNull]) { $result = 'null' } else { $result = [Convert]::ToString($v, $inv) }
                $r2.Close()
            } catch {
                $m = $_.Exception.InnerException.Message; if (-not $m) { $m = $_.Exception.Message }
                $result = 'refused: ' + $m
                try { $rs.CancelUpdate() } catch {}
            }
            $rs.Close()
            $lines.Add("dao`t$name`t$($case[0])`t$($case[1])`t$result")
        }
    }
} finally { $db.Close() }

[IO.File]::WriteAllText($Out, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
Write-Host "written $($lines.Count) lines to $Out"
