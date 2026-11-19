<#
.SYNOPSIS
    Makes tests/JackcessDotNet.Tests/Assets/dao-properties.mdb: an Access 2000 database made by DAO alone, with
    the properties Access keeps for columns, a table and the database. And dao-swedish.mdb beside it: a database
    whose catalog sorts in an order this library writes no keys for.

.DESCRIPTION
    Access keeps what a field is asked for beyond its type - Required, Allow Zero Length, a default value - as
    properties, in the LvProp value of the table's row in MSysObjects; nothing of them is in the table's
    definition. The fixture has them as Access writes them three ways, for the library to read without Access
    (PropertyWriteTests, ColumnRuleTests):

      DaoMade   made with DAO's CreateTableDef. Id is an AutoNumber. ReqLong, TextReq, TextReqAzl, MemoReq, BoolReq
                and DateReq are required; TextAzl, TextReqAzl and MemoAzl allow a zero-length string and the other
                Text and Memo fields do not (DAO's default). Defaulted has the default value 7. DAO gives every
                field but the AutoNumber a Required property, and a Text or Memo field an AllowZeroLength one
                before it. The table has a Description; OptLong has a Description, a Caption and DecimalPlaces;
                TextAzl has four properties of other types, a Memo of 300 characters among them.
      DaoGuid   Key is a Replication ID with the default value GenGUID(), which is what Access generates one from;
                Plain is a Replication ID without it.
      SqlMade   made with CREATE TABLE. Only a NOT NULL column has a property at all - Required, stored as 1
                where DAO stores 0xFF - and no column has AllowZeroLength: Access takes a zero-length string in
                TextReq, TextOpt and MemoOpt, though DAO reports the property as False.

    The database has two properties of its own, AppTitle and CustomDbLong. It has no SummaryInfo or UserDefined
    document: Access's own interface adds those, not DAO.

    dao-swedish.mdb is made with the Swedish/Finnish sort order (LANGID 0x041D). Its catalog's names are keyed
    in that order, so this library adds no row to MSysObjects and rewrites none: no table is created there and
    no property written. It has one table, People (Id LONG, Name TEXT(30)) with one row, made by DAO, which
    gives each field its Required and AllowZeroLength properties. A column is still added to it.

    The files are not the same bytes on every run (each holds the time it was made), so they are committed, and
    this script is how they were made. It needs DAO (Microsoft Access, or the Access Database Engine
    redistributable) of the bitness of the PowerShell that runs it.

.PARAMETER Path
    The file to make. An existing file is replaced.
#>
param(
    [string]$Path = (Join-Path $PSScriptRoot '..\tests\JackcessDotNet.Tests\Assets\dao-properties.mdb')
)

$ErrorActionPreference = 'Stop'
$Path = [IO.Path]::GetFullPath($Path)
if (Test-Path $Path) { Remove-Item $Path }

$dbBoolean = 1; $dbByte = 2; $dbLong = 4; $dbDate = 8; $dbText = 10; $dbMemo = 12; $dbGUID = 15
$dbAutoIncrField = 16
$dbVersion40 = 64
$general = ';LANGID=0x0409;CP=1252;COUNTRY=0'

function Add-Field($table, [string]$name, [int]$type, [int]$size = 0, [bool]$required = $false, [bool]$allowZeroLength = $false, [string]$default = '', [int]$attributes = 0) {
    $field = if ($size -gt 0) { $table.CreateField($name, $type, $size) } else { $table.CreateField($name, $type) }
    if ($attributes -ne 0) { $field.Attributes = $attributes }
    if ($required) { $field.Required = $true }
    if ($allowZeroLength) { $field.AllowZeroLength = $true }
    if ($default) { $field.DefaultValue = $default }
    $table.Fields.Append($field)
}

function Add-Property($owner, [string]$name, [int]$type, $value) {
    $owner.Properties.Append($owner.CreateProperty($name, $type, $value))
}

$engine = New-Object -ComObject DAO.DBEngine.120
try {
    $db = $engine.CreateDatabase($Path, $general, $dbVersion40)

    $table = $db.CreateTableDef('DaoMade')
    Add-Field $table 'Id' $dbLong -attributes $dbAutoIncrField
    Add-Field $table 'OptLong' $dbLong
    Add-Field $table 'ReqLong' $dbLong -required $true
    Add-Field $table 'TextDefault' $dbText 20
    Add-Field $table 'TextAzl' $dbText 20 -allowZeroLength $true
    Add-Field $table 'TextReq' $dbText 20 -required $true
    Add-Field $table 'TextReqAzl' $dbText 20 -required $true -allowZeroLength $true
    Add-Field $table 'MemoDefault' $dbMemo
    Add-Field $table 'MemoAzl' $dbMemo -allowZeroLength $true
    Add-Field $table 'MemoReq' $dbMemo -required $true
    Add-Field $table 'BoolReq' $dbBoolean -required $true
    Add-Field $table 'DateReq' $dbDate -required $true
    Add-Field $table 'Defaulted' $dbLong -default '7'
    $db.TableDefs.Append($table)

    $table = $db.CreateTableDef('DaoGuid')
    Add-Field $table 'Key' $dbGUID -default 'GenGUID()'
    Add-Field $table 'Plain' $dbGUID
    Add-Field $table 'Txt' $dbText 30
    $db.TableDefs.Append($table)

    $db.Execute('CREATE TABLE SqlMade (Id COUNTER CONSTRAINT pk PRIMARY KEY, ReqLong LONG NOT NULL, OptLong LONG, ' +
                'TextReq TEXT(20) NOT NULL, TextOpt TEXT(20), MemoOpt MEMO, G GUID)')

    $table = $db.TableDefs.Item('DaoMade')
    Add-Property $table 'Description' $dbText 'A table DAO made'
    $field = $table.Fields.Item('OptLong')
    Add-Property $field 'Description' $dbText 'An optional long'
    Add-Property $field 'Caption' $dbText 'Optional'
    Add-Property $field 'DecimalPlaces' $dbByte 2
    $field = $table.Fields.Item('TextAzl')
    Add-Property $field 'Custom Bool' $dbBoolean $true
    Add-Property $field 'CustomLong' $dbLong 123456
    Add-Property $field 'CustomDate' $dbDate ([datetime]'2026-11-19 10:20:30')
    Add-Property $field 'CustomMemo' $dbMemo ('m' * 300)

    Add-Property $db 'AppTitle' $dbText 'Made by DAO'
    Add-Property $db 'CustomDbLong' $dbLong 42

    $db.Close()
}
finally {
    $table = $null; $field = $null; $db = $null
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine)
}
"made $Path"

$swedish = Join-Path (Split-Path $Path) 'dao-swedish.mdb'
if (Test-Path $swedish) { Remove-Item $swedish }
$engine = New-Object -ComObject DAO.DBEngine.120
try {
    $db = $engine.CreateDatabase($swedish, ';LANGID=0x041D;CP=1252;COUNTRY=0', $dbVersion40)
    $table = $db.CreateTableDef('People')
    Add-Field $table 'Id' $dbLong
    Add-Field $table 'Name' $dbText 30
    $db.TableDefs.Append($table)
    $db.Execute("INSERT INTO People (Id, Name) VALUES (1, 'Asa')")
    $db.Close()
}
finally {
    $table = $null; $db = $null
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($engine)
}
"made $swedish"
