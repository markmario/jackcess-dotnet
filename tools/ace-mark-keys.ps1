<#
.SYNOPSIS
    Has Microsoft's own database engine index text values that hold the marks Access keys by the character before
    them - ー, 々, ゝ, the shadda and their like - and writes the keys it made to a .tsv file.

.DESCRIPTION
    In General - Legacy twelve characters have no key of their own (#156): U+0651 and its two presentation forms
    U+FE7C and U+FE7D (the shadda), U+3005 (々), U+3031 and U+3032 (〱 〲), U+309D and U+309E (ゝ ゞ), U+30FD and
    U+30FE (ヽ ヾ), and U+30FC and U+FF70 (ー ｰ). Access keys each by the character before it. This script writes the
    values that pin that rule down, and has tools/ace-text-keys.ps1 index them in a Jet4 .mdb ACE makes (General -
    Legacy, 1033 version 0) and read the keys back. The golden file
    tests/JackcessDotNet.Tests/IndexKeys/general-legacy-marks-ace16.tsv is made by it, and TextIndexKeyTests holds
    this library's keys to it byte for byte.

    The values:
      - each mark alone and after a character of each kind - hiragana, katakana full and half width, voiced, small,
        ン, kanji, a Latin letter with and without an accent, Æ and ß, a digit, a space, a hyphen, an apostrophe, a
        control character, Arabic letters with and without a vowel mark, Thai, Cyrillic, Greek, Hangul, a surrogate
        pair, and another mark - each alone, and followed by é, b and カ;
      - two marks together, after nothing, a kana, a letter and a kanji;
      - runs of one to nine kana ending in ー or 々, with and without a small kana before them;
      - control characters, hyphens and voiced sound marks after the marks, and keys cut at 510 bytes: 253 and 254
        characters before ー, 々, ゝ, ｰ and the shadda, and the mark among them;
      - each of ー and 々 after a sample of the BMP: every 61st character, every kana, and the characters Access
        sorts as several letters;
      - words: データ, メール, コンピューター, 佐々木, 人々, ｺｰﾋｰ, an Arabic name with a shadda.

    -Sweep writes, in place of the golden file, one value file and one .tsv per sweep into the folder -Out names:
    each of the twelve marks after every character of the BMP but the surrogates and U+0000, and every such character
    after 々, after a々 and after カー. About 1,100,000 values; ACE 16 keys them in a few minutes. The rule this
    library keeps (TextIndexCodes.MarkCodes) was held to all of them.

.PARAMETER Out
    The .tsv to write, or with -Sweep the folder to write the sweeps to.

.EXAMPLE
    pwsh tools/ace-mark-keys.ps1 -Out tests/JackcessDotNet.Tests/IndexKeys/general-legacy-marks-ace16.tsv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Out,
    [switch]$Sweep
)

$ErrorActionPreference = 'Stop'
$textKeys = Join-Path $PSScriptRoot 'ace-text-keys.ps1'
$marks = '0651', '3005', '3031', '3032', '309D', '309E', '30FC', '30FD', '30FE', 'FE7C', 'FE7D', 'FF70'

function Join-Values($lines) {
    $seen = New-Object 'System.Collections.Generic.HashSet[string]'
    $unique = New-Object System.Collections.Generic.List[string]
    foreach ($l in $lines) { if ($l -and $seen.Add($l)) { $unique.Add($l) } }
    ($unique -join "`n") + "`n"
}

function Get-Bmp {
    for ($c = 1; $c -le 0xFFFF; $c++) { if ($c -lt 0xD800 -or $c -gt 0xDFFF) { '{0:X4}' -f $c } }
}

function Join-CodePoints { (@($args) | Where-Object { $_ }) -join ' ' }

if ($Sweep) {
    $folder = [IO.Path]::GetFullPath($Out)
    New-Item -ItemType Directory -Force $folder | Out-Null
    $sweeps = [ordered]@{}
    foreach ($m in $marks) { $sweeps["before-$m"] = foreach ($c in Get-Bmp) { "$c $m" } }
    $sweeps['after-3005'] = foreach ($c in Get-Bmp) { "3005 $c" }
    $sweeps['after-0061-3005'] = foreach ($c in Get-Bmp) { "0061 3005 $c" }
    $sweeps['after-30AB-30FC'] = foreach ($c in Get-Bmp) { "30AB 30FC $c" }
    foreach ($name in $sweeps.Keys) {
        $values = Join-Path $folder "$name.txt"
        [IO.File]::WriteAllText($values, (Join-Values $sweeps[$name]))
        & $textKeys -Order GeneralLegacy -Values $values -Out (Join-Path $folder "$name.tsv")
    }
    return
}

$lines = New-Object System.Collections.Generic.List[string]

# Each mark after a character of each kind, alone and followed by others.
$before = @(
    '',
    '3042', '304B', '304C', '3063', '3093', '3041',                                                  # あ か が っ ん ぁ
    '30A2', '30AB', '30AC', '30C3', '30E5', '30F3', '30F4', '30A1', '30F5', '30EE', '30F7',          # ア カ ガ ッ ュ ン ヴ ァ ヵ ヮ ヷ
    '30D1', '30D0', '30CF',                                                                          # パ バ ハ
    'FF71', 'FF76', 'FF76 FF9E', 'FF8A FF9F', 'FF6F', 'FF9D',                                        # ｱ ｶ ｶﾞ ﾊﾟ ｯ ﾝ
    '65E5', '4E00',                                                                                  # 日 一
    '0061', '0041', '00E9', '00C6', '00DF', '0031', '0020 0061', '002D', '0027', '0001', '0061 0001', '0061 002D',
    '0628', '0645', '0645 064E', '0627',
    '0061 D83D DE00', '30AB D83D DE00',
    '0E01', '0430', '03B1', 'AC00',
    '30FC', '3005', '30AB 30FC', '65E5 3005', '0061 30FC', '30AB 3005', '3042 309D'
)
foreach ($m in $marks) {
    foreach ($b in $before) {
        foreach ($a in '', '00E9', '0062', '30AB') { $lines.Add((Join-CodePoints $b $m $a)) }
    }
}

# Two marks together.
foreach ($m in $marks) { foreach ($b in '', '30AB', '0061', '65E5') { $lines.Add((Join-CodePoints $b $m $m)) } }

# Runs of kana: the run after the kana's codes, and the suffix that repeats for every seven kana.
$kana = '30AB', '30AD', '30AF', '30B1', '30B3', '30B5', '30B7', '30B9', '30BB', '30BD'
for ($n = 1; $n -le 9; $n++) {
    $run = $kana[0..($n - 1)] -join ' '
    $lines.Add("$run 30FC"); $lines.Add("$run 3005"); $lines.Add("$run 30FC 30AB"); $lines.Add("30E5 $run 30FC")
}
foreach ($v in '30AB 0061 30FC', '30AB 65E5 3005', '0061 30AB 30FC', '65E5 30AB 30FC', '30AB 0020 30FC', '30AB 0001 30FC',
               '30AB 002D 30FC', '30E5 3005', '30C3 30FC 30C3', '30E5 30FC 30E5 30FC', '30AB 30FC 0001', '0001 30FC', '0001 3005') {
    $lines.Add($v)
}

# What follows a mark: control characters (whose place is told), hyphens, the half-width voiced sound mark.
foreach ($m in $marks) {
    foreach ($b in '', '0061', '00C6', '30AB', '0001', '00E9', '30E5', '65E5') {
        foreach ($a in '0001', '0001 00E9', '002D 0062', '00E9 0001', '30AB 0001', 'FF9E', '30AB FF9E') { $lines.Add((Join-CodePoints $b $m $a)) }
    }
}

# Keys cut at 510 bytes, with the mark at the end of 255 characters or just before it.
foreach ($m in '30FC', '3005', '309D', 'FF70', '0651') {
    foreach ($n in 254, 255) {
        foreach ($c in '00E9', '30AB', '65E5') {
            $lines.Add(((@($c) * ($n - 1)) + $m) -join ' ')
            $lines.Add(((@($c) * ($n - 2)) + $m + $c) -join ' ')
        }
    }
}

# ー and 々 after a sample of the BMP.
$several = '00C6', '00DE', '00DF', '0132', '0152', '01C4', '01C7', '01CA', '01E2', '01F1', '01FC', '05F0', '05F1', '05F2', 'FB00', 'FB03', 'FB05', 'FB06'
$sample = @(Get-Bmp | Where-Object { [Convert]::ToInt32($_, 16) % 61 -eq 0 }) +
          @(@(0x3041..0x30FF) + @(0xFF66..0xFF9F) | ForEach-Object { '{0:X4}' -f $_ }) + $several
foreach ($c in $sample) { $lines.Add("$c 30FC"); $lines.Add("$c 3005") }

# Words.
foreach ($v in '30C7 30FC 30BF', '30E1 30FC 30EB', '30B3 30F3 30D4 30E5 30FC 30BF 30FC', '4F50 3005 6728', '4EBA 3005', '6642 3005',
               'FF7A FF70 FF8B FF70', '0645 062D 0645 0651 062F', '3044 3059 309E', '3053 3053 308D 309D', '30CF 30FC 30C9 30FB 30C7 30A3 30B9 30AF') {
    $lines.Add($v)
}

$values = Join-Path ([IO.Path]::GetTempPath()) ("ace-mark-keys-{0}.txt" -f [Guid]::NewGuid().ToString('N'))
[IO.File]::WriteAllText($values, (Join-Values $lines))
try { & $textKeys -Order GeneralLegacy -Values $values -Out $Out }
finally { Remove-Item $values }
