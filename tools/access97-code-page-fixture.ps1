<#
.SYNOPSIS
    Makes Access 97 files on machines of other code pages - code page 1251 (Cyrillic) and 932 (Japanese) - and
    records what Jet 4.0 reads of them there and on a machine of code page 1252.

.DESCRIPTION
    Jet keeps an Access 97 (Jet 3) file's text - values, and the names of tables, columns and indexes - in a code
    page, a byte or two to a character. Which code page is the machine's: the process's ANSI code page (GetACP), the
    one Windows converts text with. The file does not record it. Its header has a sort order (offset 0x3A) and a code
    page (0x3C) under the mask, and both are what CreateDatabase was given, whatever code page the text is then
    written in: access97-cp1251.mdb and access97-cp932.mdb say 1033 and 1252, General, and hold text in 1251 and
    932. The one thing seen to follow the header's code page is the database password NewPassword sets: given
    "Пароль1" with code page 1251 in a file whose header says 1252, it kept "??????1". Opening converts the password
    given with the process's code page, so access97-cp1251-password.mdb is made with dbLangCyrillic, whose header
    says 1049 and 1251. (A header of General with code page 1251 - not one of DAO's constants - made a file that
    DAO with code page 1252 could not list the tables of.)

    A process can be given a code page of its own: Windows 10 1903 and later read <activeCodePage> in an
    application's manifest, and Windows 11 takes a locale's name there (ru-RU, ja-JP). So the script builds a small
    32-bit .NET Framework program - DAO 3.6, Jet 4.0's DAO, is 32-bit only - once for each code page, with that
    code page in its manifest, and has it make and read the files. Nothing of this library takes part.

      access97-cp1251.mdb           made with code page 1251: a table, a Text column and an index named in Cyrillic,
                                    and rows of Cyrillic, Ukrainian, Western and Greek text.
      access97-cp932.mdb            made with code page 932: names in Japanese, and rows of kanji, katakana of both
                                    widths, Cyrillic (which code page 932 has, two bytes a letter) and Latin.
      access97-cp1251-password.mdb  made with code page 1251 and dbLangCyrillic, with a database password in Cyrillic.

    Each file's .tsv has, for each code page it was read with (the one it was made with, and 1252), every line
    prefixed by that code page:
      table / column / index        the names, as DAO's TableDefs, Fields and Indexes give them;
      row     <Id> <value>          every row of the table;
      seek    <asked> <Id or ->     a Seek on the text index for each value, and for its upper- and lower-case forms;
      tabledef <asked> <name or ->  TableDefs looked up by the table's name, and by its upper- and lower-case forms;
      password <asked> <opens or refused>.
    A value that DAO converted to the code page is in the file as the code page has it: a character the code page
    lacks became the character Windows' best fit gives, or a question mark.

    The files are not the same bytes on every run, so they are committed, and this script is the record of how they
    were made. It needs Windows 11 (or a Windows 10 whose activeCodePage takes a locale's name), the .NET SDK, and
    32-bit DAO 3.6 (DAO.DBEngine.36, part of Windows):
      pwsh tools/access97-code-page-fixture.ps1

    Measured with it, on Windows 11 with Jet 4.0's DAO 3.6 and ACE 12's DAO (-EngineProgId DAO.DBEngine.120): both
    engines write and read the text in the process's code page, whatever the header says - a file made with code
    page 1251 reads as "Ïðèâåò" with 1252 for the "Привет" it was given - and they key a text index in Access 97's
    General order on the bytes, so that one table of keys serves every code page. With the code page set to UTF-8
    (65001), DAO 3.6's CreateDatabase fails ("Not a valid password").

.EXAMPLE
    pwsh tools/access97-code-page-fixture.ps1
#>
[CmdletBinding()]
param(
    # Where to write the files (tests/JackcessDotNet.Tests/Assets when not given).
    [string]$OutDir,
    # The DAO engine, by ProgID: DAO.DBEngine.36 is Jet 4.0, DAO.DBEngine.120 the ACE 12 of Access 2007.
    [string]$EngineProgId = 'DAO.DBEngine.36'
)

$ErrorActionPreference = 'Stop'
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot '..\tests\JackcessDotNet.Tests\Assets' }
$OutDir = [IO.Path]::GetFullPath($OutDir)

$program = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

// Fixture make <fixture> <path> <engine>      makes the file
// Fixture read <fixture> <path> <engine> <tsv> appends what DAO reads of it to the .tsv
static class Fixture
{
    [DllImport("kernel32.dll")] static extern uint GetACP();

    // dbLangGeneral: the header says sort order 1033 and code page 1252, whatever the code page of the process.
    const string General = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    // dbLangCyrillic: sort order 1049 and code page 1251.
    const string Cyrillic = ";LANGID=0x0419;CP=1251;COUNTRY=0";
    const int DbVersion30 = 32, DbOpenTable = 1;

    sealed class Spec
    {
        public string Locale = General, Table, Column, Index, Password;
        public string[] Values;
    }

    static Spec For(string name)
    {
        switch (name)
        {
            case "cp1251": return new Spec
            {
                Table  = "Города",
                Column = "Название",
                Index  = "ПоНазванию",
                Values = new[]
                {
                    "Москва",
                    "Санкт-Петербург",
                    "Ёлки",
                    "ёлки",
                    "Київ",
                    "café",
                    "€100",
                    "Ελλάδα",
                    "Abc",
                },
            };
            case "cp932": return new Spec
            {
                Table  = "都市",
                Column = "名前",
                Index  = "名前順",
                Values = new[]
                {
                    "東京",
                    "大阪",
                    "ｶﾀｶﾅ",
                    "カタカナ",
                    "Москва",
                    "москва",
                    "naïve",
                    "ABC",
                    "ＡＢＣ",
                },
            };
            case "cp1251-password": return new Spec
            {
                Locale   = Cyrillic,
                Table    = "Т",
                Column   = "Имя",
                Index    = "ПоИмени",
                Password = "Пароль1",
                Values   = new[] { "Да" },
            };
        }
        throw new ArgumentException(name);
    }

    static int Main(string[] args)
    {
        var spec = For(args[1]);
        string path = args[2];
        dynamic engine = Activator.CreateInstance(Type.GetTypeFromProgID(args[3], true));
        if (args[0] == "make") Make(engine, spec, path);
        else Read(engine, spec, path, args[4]);
        return 0;
    }

    static void Make(dynamic engine, Spec spec, string path)
    {
        if (File.Exists(path)) File.Delete(path);
        dynamic db = engine.CreateDatabase(path, spec.Locale, DbVersion30);
        db.Execute("CREATE TABLE [" + spec.Table + "] (Id LONG CONSTRAINT PrimaryKey PRIMARY KEY, [" + spec.Column + "] TEXT(50))");
        db.Execute("CREATE INDEX [" + spec.Index + "] ON [" + spec.Table + "] ([" + spec.Column + "])");
        dynamic rs = db.OpenRecordset(spec.Table, DbOpenTable);
        for (int i = 0; i < spec.Values.Length; i++)
        {
            rs.AddNew();
            rs.Fields["Id"].Value = i + 1;
            rs.Fields[spec.Column].Value = spec.Values[i];
            rs.Update();
        }
        rs.Close();
        db.Close();
        if (spec.Password != null)
        {
            db = engine.OpenDatabase(path, true, false);
            db.NewPassword("", spec.Password);
            db.Close();
        }
    }

    static void Read(dynamic engine, Spec spec, string path, string tsv)
    {
        string acp = GetACP().ToString();
        var lines = new List<string>();
        void Line(params object[] cells) => lines.Add(acp + "\t" + string.Join("\t", cells));

        string connect = "";
        if (spec.Password != null)
        {
            string asWestern = Encoding.GetEncoding(1252).GetString(Encoding.GetEncoding(1251).GetBytes(spec.Password));
            foreach (string asked in new[] { spec.Password, spec.Password.ToLowerInvariant(), asWestern, "" }.Distinct())
            {
                bool opens;
                try { engine.OpenDatabase(path, false, true, ";pwd=" + asked).Close(); opens = true; }
                catch (COMException) { opens = false; }
                Line("password", asked, opens ? "opens" : "refused");
                if (opens) connect = ";pwd=" + asked;
            }
            if (connect.Length == 0) { File.AppendAllLines(tsv, lines, new UTF8Encoding(false)); return; }
        }

        dynamic db = engine.OpenDatabase(path, false, true, connect);
        string table = null;
        foreach (dynamic td in db.TableDefs)
        {
            string name = td.Name;
            if (name.StartsWith("MSys", StringComparison.Ordinal)) continue;
            table = name;
            Line("table", name);
            foreach (dynamic f in td.Fields) Line("column", name, (string)f.Name);
            foreach (dynamic ix in td.Indexes)
            {
                var fields = new List<string>();
                foreach (dynamic f in ix.Fields) fields.Add((string)f.Name);
                Line("index", name, (string)ix.Name, string.Join(",", fields));
            }
        }

        foreach (string asked in new[] { spec.Table, spec.Table.ToUpperInvariant(), spec.Table.ToLowerInvariant() }.Distinct())
        {
            string found = "-";
            try { found = (string)db.TableDefs[asked].Name; } catch (COMException) { }
            Line("tabledef", asked, found);
        }

        dynamic rs = db.OpenRecordset("SELECT * FROM [" + table + "] ORDER BY Id");
        while (!rs.EOF)
        {
            object value = rs.Fields[1].Value;
            Line("row", (int)rs.Fields["Id"].Value, value is string s ? s : "\\N");
            rs.MoveNext();
        }
        rs.Close();

        dynamic seek = db.OpenRecordset(table, DbOpenTable);
        string index = null;
        foreach (dynamic ix in db.TableDefs[table].Indexes) if (!(bool)ix.Primary) index = (string)ix.Name;
        seek.Index = index;
        foreach (string asked in spec.Values.SelectMany(v => new[] { v, v.ToUpperInvariant(), v.ToLowerInvariant() }).Distinct())
        {
            seek.Seek("=", asked);
            Line("seek", asked, (bool)seek.NoMatch ? "-" : ((int)seek.Fields["Id"].Value).ToString());
        }
        seek.Close();
        db.Close();
        File.AppendAllLines(tsv, lines, new UTF8Encoding(false));
    }
}
'@

$project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <PlatformTarget>x86</PlatformTarget>
    <LangVersion>latest</LangVersion>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Microsoft.CSharp" />
  </ItemGroup>
</Project>
'@

$manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="Fixture.app"/>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <activeCodePage xmlns="http://schemas.microsoft.com/SMI/2019/WindowsSettings">LOCALE</activeCodePage>
    </windowsSettings>
  </application>
</assembly>
'@

$work = Join-Path ([IO.Path]::GetTempPath()) ('access97-code-page-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
try {
    # The program, built once for each code page: Legacy is the machine's own (1252 where this was measured).
    $exe = @{}
    foreach ($locale in 'Legacy', 'ru-RU', 'ja-JP') {
        $dir = Join-Path $work $locale
        New-Item -ItemType Directory $dir | Out-Null
        Set-Content (Join-Path $dir 'Fixture.cs') $program -Encoding utf8
        Set-Content (Join-Path $dir 'Fixture.csproj') $project -Encoding utf8
        Set-Content (Join-Path $dir 'app.manifest') $manifest.Replace('LOCALE', $locale) -Encoding utf8
        dotnet build (Join-Path $dir 'Fixture.csproj') -c Release -o (Join-Path $dir 'out') -nologo -v q | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "The program for $locale did not build." }
        $exe[$locale] = Join-Path $dir 'out\Fixture.exe'
    }

    foreach ($fixture in @(@('cp1251', 'ru-RU'), @('cp932', 'ja-JP'), @('cp1251-password', 'ru-RU'))) {
        $name, $locale = $fixture
        $mdb = Join-Path $OutDir "access97-$name.mdb"
        $tsv = Join-Path $OutDir "access97-$name.tsv"
        & $exe[$locale] make $name $mdb $EngineProgId
        if ($LASTEXITCODE -ne 0) { throw "$name was not made." }

        $header = "# What $EngineProgId (32-bit) reads of $([IO.Path]::GetFileName($mdb)), made with code page $($name -replace '^cp(\d+).*', '$1') by tools/access97-code-page-fixture.ps1, with each code page it was read with first on each line. \N is a null."
        [IO.File]::WriteAllText($tsv, $header + "`n", (New-Object Text.UTF8Encoding $false))
        foreach ($reader in $locale, 'Legacy') {
            $copy = Join-Path $work "$name-$reader.mdb"
            Copy-Item $mdb $copy
            & $exe[$reader] read $name $copy $EngineProgId $tsv
            if ($LASTEXITCODE -ne 0) { throw "$name was not read with $reader." }
        }
        # File.AppendAllLines ends lines with CRLF; the repository's text files are LF.
        [IO.File]::WriteAllText($tsv, [IO.File]::ReadAllText($tsv).Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))
        "$name -> $mdb"
    }
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
