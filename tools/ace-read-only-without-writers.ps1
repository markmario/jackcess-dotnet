<#
.SYNOPSIS
    Has ACE and DAO open a database file held for reading and shared for reading only, and holds such a file while
    ACE or DAO has it open, and writes what each did.

.DESCRIPTION
    The measurement behind DatabaseOpenOptions.KeepWritersOut (#84). A database opened with it is held by a handle
    that reads (FileAccess.Read) and shares the file for reading only (FileShare.Read), so that no program can open
    the file to write while it is held. This script holds a file exactly so, with a FileStream of its own - nothing of
    this library takes part - and asks:

    1. With the file held so: which of ACE's modes (its default, Mode=Read, Mode=Share Exclusive and
       Mode=Share Deny Write) and DAO's (OpenDatabase shared, read-only, exclusive and exclusive read-only) open
       it; what one that is refused says; whether one that opens can write; whether Access's lock file (.ldb,
       .laccdb) is made while it is open and is gone once it has closed; and whether the file is the same bytes after.
    2. With ACE or DAO holding the file in each of those modes: whether a handle that reads and shares reading only
       opens it, and how ACE's or DAO's own handle shares the file, by four probes beside it - read sharing reading
       and writing, read sharing reading, write sharing reading and writing, write sharing reading.
    3. Whether a second handle opened beside the held one gets in: one that reads and shares reading only (a second
       such open), one that reads and shares reading and writing (an open with ReadOnly alone), and one that writes
       and shares reading (an open to write).

    4. ACE's default mode, let in beside the held file: how its handle shares the file once the held one is let go,
       and whether it can write then.

    Each is done for an .mdb (Access 2000) and an .accdb, each made anew by DAO with a table T (Id LONG primary key,
    V TEXT(100)) of three rows; with -Access97, for an Access 97 .mdb alone.

    Run it in Windows PowerShell 5.1, which has System.Data.OleDb, with a 64-bit ACE:
      powershell.exe -File tools\ace-read-only-without-writers.ps1
    and for an Access 97 file, which only a 32-bit engine opens, in the 32-bit Windows PowerShell with Jet 4.0 and
    DAO 3.6:
      C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -File tools\ace-read-only-without-writers.ps1 `
        -Access97 -Provider Microsoft.Jet.OLEDB.4.0 -DaoEngine DAO.DBEngine.36
#>
param(
    [string] $Provider = 'Microsoft.ACE.OLEDB.16.0',
    [string] $DaoEngine = 'DAO.DBEngine.120',
    [switch] $Access97,
    [string] $Dir = (Join-Path ([IO.Path]::GetTempPath()) ('kwo-' + [Guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Data
New-Item -ItemType Directory -Force $Dir | Out-Null

function New-Database([string] $extension) {
    $path = Join-Path $Dir ([Guid]::NewGuid().ToString('N') + $extension)
    $dbe = New-Object -ComObject $DaoEngine
    $version = if ($Access97) { 32 } elseif ($extension -eq '.mdb') { 64 } else { 128 }
    $db = $dbe.CreateDatabase($path, ';LANGID=0x0409;CP=1252;COUNTRY=0', $version)
    $db.Execute('CREATE TABLE T (Id LONG CONSTRAINT PK PRIMARY KEY, V TEXT(100))')
    foreach ($i in 1..3) { $db.Execute("INSERT INTO T (Id, V) VALUES ($i, 'row $i')", 128) }
    $db.Close()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($dbe)
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    return $path
}

function Lock-File([string] $path) {
    $ext = if ($path.EndsWith('.mdb')) { '.ldb' } else { '.laccdb' }
    return [IO.Path]::ChangeExtension($path, $ext)
}

function Lock-State([string] $path) {
    $lock = Lock-File $path
    if (Test-Path $lock) { return "lock file $((Get-Item $lock).Length) bytes" } else { return 'no lock file' }
}

function Hash([string] $path) {
    $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try { return [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($fs)) } finally { $fs.Dispose() }
}

# A handle as the library's KeepWritersOut open holds the file.
function Hold([string] $path) { return [IO.File]::Open($path, 'Open', 'Read', 'Read') }

function Try-Open([string] $path, [string] $access, [string] $share) {
    try { $fs = [IO.File]::Open($path, 'Open', $access, $share); $fs.Dispose(); return 'opens' }
    catch [IO.IOException] { return 'refused' }
}

function Probe([string] $path) {
    return ('read/share RW: ' + (Try-Open $path 'Read' 'ReadWrite') +
            '; read/share R: ' + (Try-Open $path 'Read' 'Read') +
            '; write/share RW: ' + (Try-Open $path 'ReadWrite' 'ReadWrite') +
            '; write/share R: ' + (Try-Open $path 'ReadWrite' 'Read'))
}

function Ace-Open([string] $path, [string] $mode) {
    $cs = "Provider=$Provider;Data Source=$path;Persist Security Info=False"
    if ($mode) { $cs += ";Mode=$mode" }
    $c = New-Object System.Data.OleDb.OleDbConnection $cs
    $c.Open()
    return $c
}

function Ace-Count($c) { $cmd = $c.CreateCommand(); $cmd.CommandText = 'SELECT COUNT(*) FROM T'; return [int]$cmd.ExecuteScalar() }

function Ace-TryInsert($c) {
    $cmd = $c.CreateCommand(); $cmd.CommandText = "INSERT INTO T (Id, V) VALUES (99, 'x')"
    try { [void]$cmd.ExecuteNonQuery(); return 'INSERT succeeded' } catch { return 'INSERT refused: ' + $_.Exception.InnerException.Message + $_.Exception.Message }
}

function Dao-Open([string] $path, [bool] $exclusive, [bool] $readOnly) {
    $dbe = New-Object -ComObject $DaoEngine
    $db = $dbe.OpenDatabase($path, $exclusive, $readOnly)
    return @{ Engine = $dbe; Db = $db }
}

function Dao-Close($h) {
    $h.Db.Close()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($h.Db)
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($h.Engine)
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}

$aceModes = @($null, 'Read', 'Share Exclusive', 'Share Deny Write')
$daoModes = @(@($false, $false, 'shared'), @($false, $true, 'read-only'), @($true, $false, 'exclusive'), @($true, $true, 'exclusive read-only'))

$extensions = if ($Access97) { @('.mdb') } else { @('.mdb', '.accdb') }
foreach ($ext in $extensions) {
    "=== $ext$(if ($Access97) { ' (Access 97)' }) ($Provider, $DaoEngine) ==="

    # 1. The file held, read and shared for reading only; ACE and DAO asked to open it.
    $path = New-Database $ext
    $before = Hash $path
    $held = Hold $path
    try {
        foreach ($mode in $aceModes) {
            $name = if ($mode) { "ACE Mode=$mode" } else { 'ACE default' }
            try {
                $c = Ace-Open $path $mode
                try { "1. held; $name opens: count $(Ace-Count $c); $(Ace-TryInsert $c); while open $(Lock-State $path)" }
                finally { $c.Dispose(); [System.Data.OleDb.OleDbConnection]::ReleaseObjectPool() }
                "   after it closed: $(Lock-State $path)"
            }
            catch { "1. held; $name refused: $($_.Exception.InnerException.Message)$($_.Exception.Message)" }
        }
        foreach ($m in $daoModes) {
            try {
                $h = Dao-Open $path $m[0] $m[1]
                try {
                    $rs = $h.Db.OpenRecordset('SELECT COUNT(*) FROM T')
                    $n = $rs.Fields.Item(0).Value; $rs.Close()
                    $write = try { $h.Db.Execute("INSERT INTO T (Id, V) VALUES (98, 'y')", 128); 'INSERT succeeded' } catch { 'INSERT refused: ' + $_.Exception.Message }
                    "1. held; DAO $($m[2]) opens: count $n; Updatable $($h.Db.Updatable); $write; while open $(Lock-State $path)"
                }
                finally { Dao-Close $h }
                "   after it closed: $(Lock-State $path)"
            }
            catch { "1. held; DAO $($m[2]) refused: $($_.Exception.Message)" }
        }

        # 3. A second handle beside the held one.
        "3. held; beside it: second read/share R: $(Try-Open $path 'Read' 'Read'); read/share RW (ReadOnly alone): $(Try-Open $path 'Read' 'ReadWrite'); write/share R (open to write): $(Try-Open $path 'ReadWrite' 'Read')"
    }
    finally { $held.Dispose() }
    $after = Hash $path
    "1. the file after: $(if ($before -eq $after) { 'the same bytes' } else { 'CHANGED' }); $(Lock-State $path)"

    # 2. ACE or DAO first, then the held handle.
    foreach ($mode in $aceModes) {
        $name = if ($mode) { "ACE Mode=$mode" } else { 'ACE default' }
        $path = New-Database $ext
        try { $c = Ace-Open $path $mode } catch { "2. $name did not open a free file: $($_.Exception.Message)"; continue }
        try {
            $hold = try { (Hold $path).Dispose(); 'opens' } catch [IO.IOException] { 'refused: ' + $_.Exception.Message }
            "2. $name holds it ($(Lock-State $path)); read/share R: $hold; its handle by probes: $(Probe $path)"
        }
        finally { $c.Dispose(); [System.Data.OleDb.OleDbConnection]::ReleaseObjectPool() }
    }
    foreach ($m in $daoModes) {
        $path = New-Database $ext
        $h = Dao-Open $path $m[0] $m[1]
        try {
            $hold = try { (Hold $path).Dispose(); 'opens' } catch [IO.IOException] { 'refused: ' + $_.Exception.Message }
            "2. DAO $($m[2]) holds it ($(Lock-State $path)); read/share R: $hold; its handle by probes: $(Probe $path)"
        }
        finally { Dao-Close $h }
    }

    # 4. ACE's default mode, let in beside the held file, once the held handle has been let go: how its own handle
    #    shares the file, and whether it writes then.
    $path = New-Database $ext
    $held = Hold $path
    $c = Ace-Open $path $null
    $held.Dispose()
    try { "4. ACE default, opened beside the held file, after it was let go: its handle by probes: $(Probe $path); $(Ace-TryInsert $c); $(Lock-State $path)" }
    finally { $c.Dispose(); [System.Data.OleDb.OleDbConnection]::ReleaseObjectPool() }
}
Remove-Item -Recurse -Force $Dir -ErrorAction SilentlyContinue
