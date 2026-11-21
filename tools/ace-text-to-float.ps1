<#
.SYNOPSIS
    Has Microsoft's own database engine store text in a Double and a Single column, and a Double and a Single in a
    Text column, and writes what it stored to a .tsv file, beside the number each text stands for.

.DESCRIPTION
    The record of what decided #124. The text is the hard kind for a conversion: the exact half between two doubles,
    or two singles, next to each other, and text just below and just above such a half - cut to 17, 20 or 25 digits,
    or with a 1 put twenty places past its last digit - in exponent form and, where it is short enough, written out.
    The halves are of numbers near 1, of whole numbers and halves near 2^52 and 2^53 (2^23 and 2^24 for a single), of
    large and small numbers, of subnormal ones, and the edges: the half past the largest number, the half between the
    largest subnormal and the least normal number, and the half between 0 and the least number.

    The script creates an .accdb with DAO and a table with a Double and a Single column, and puts each text into each
    column three ways: ACE's OLE DB provider INSERTs it as a text literal in the statement ('...'), and as a text
    parameter (VarWChar), and DAO's AddNew assigns the text to the field. It reads each value back through OLE DB, which
    returns a Double or a Single column's value as it is stored, without a conversion, and writes the bits.

    Beside them it writes the double and the single nearest each text, of two as near the one with the even
    significand, worked out exactly here; the single nearest the nearest double (the text rounded twice); and what this
    process's double.Parse and float.Parse give (Windows PowerShell 5.1 runs on .NET Framework 4.8, in 64 bits).
    Nothing of this library takes part.

    A sample of the texts - those of numbers near 1, of whole numbers and halves, the issue's own and the other
    forms - is also given to DAO's CreateProperty as the value of a Double and of a Single property.

    Then, the other way, each double and single of a list is put into a Text column three ways: an OLE DB parameter of
    its own type (Double or Single), DAO's AddNew assigning it, and ACE copying it from a Double or a Single column with
    INSERT ... SELECT; and DAO's CreateProperty takes it as a Text property's value. It writes the text stored.

    Run it in Windows PowerShell 5.1, which has System.Data.OleDb, with a 64-bit ACE:
      powershell.exe -File tools\ace-text-to-float.ps1 -Out <full path>\ace-text-to-float.tsv

    tests/JackcessDotNet.Tests/Assets/ace-text-to-float.tsv is this script run with -Picks 3 on an en-AU machine:
    4,375 lines, which runs on two machines wrote alike. NumberTextTests pins it by its SHA-256; to change it, run
    the script again and change the hash there with it.

.PARAMETER Out
    The .tsv to write. Its header lines begin with #. A line for text is "text", then: the family, the form, the text,
    the nearest double, ACE's literal, ACE's parameter, DAO, .NET's double.Parse; then the nearest single, the single
    nearest the nearest double, ACE's literal, ACE's parameter, DAO, .NET's float.Parse. A double or single is its bits
    in hex, "overflow" for text past the type's range, or "refused: " and ACE's message. A line for a property is
    "property", the family, the form, the text, the nearest double, the Double property's value, the nearest single
    and the Single property's value. A line for a number written as text is "tostring", the kind (double or single),
    its bits, its exact value, and the text ACE's parameter, ACE's INSERT ... SELECT, DAO's field and DAO's Text
    property stored, and what this process's Convert.ToString gives.

.PARAMETER Picks
    How many numbers of each family are drawn, at random from a fixed seed.

.PARAMETER Provider
    The OLE DB provider: Microsoft.ACE.OLEDB.16.0 by default.
#>
param(
    [Parameter(Mandatory = $true)] [string] $Out,
    [int] $Picks = 12,
    [string] $Provider = 'Microsoft.ACE.OLEDB.16.0'
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($Out)) { throw "Out must be a full path." }
if (-not [Environment]::Is64BitProcess) { throw "Run this in a 64-bit Windows PowerShell, for a 64-bit ACE." }
Add-Type -AssemblyName System.Data

Add-Type -ReferencedAssemblies System.Numerics -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

public sealed class TextCase
{
    public string Family;
    public string Form;
    public string Text;
    public string NearestDouble;
    public string NearestSingle;
    public string SingleViaDouble;
}

public static class TextToFloat
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // A decimal number: Unscaled x 10^Power, positive.
    struct Dec
    {
        public BigInteger Unscaled;
        public int Power;
        public Dec(BigInteger u, int p)
        {
            while (!u.IsZero && (u % 10).IsZero) { u /= 10; p++; }
            Unscaled = u; Power = p;
        }
        public string Digits { get { return Unscaled.ToString(Inv); } }
        public int Exponent { get { return Digits.Length - 1 + Power; } }
    }

    // K x 2^e, exactly, as a decimal.
    static Dec OfBinary(BigInteger k, int e)
    {
        return e >= 0 ? new Dec(k << e, 0) : new Dec(k * BigInteger.Pow(5, -e), e);
    }

    // The half between the double of these bits (positive, finite) and the next one up.
    static Dec DoubleHalf(long bits)
    {
        int field = (int)(bits >> 52);
        long fraction = bits & 0xFFFFFFFFFFFFFL;
        BigInteger m = field == 0 ? fraction : (fraction | (1L << 52));
        int e = field == 0 ? -1074 : field - 1075;
        return OfBinary(2 * m + 1, e - 1);
    }

    static Dec SingleHalf(int bits)
    {
        int field = bits >> 23;
        int fraction = bits & 0x7FFFFF;
        BigInteger m = field == 0 ? fraction : (fraction | (1 << 23));
        int e = field == 0 ? -149 : field - 150;
        return OfBinary(2 * m + 1, e - 1);
    }

    static int BitLength(BigInteger v)
    {
        int n = 0;
        while (!v.IsZero) { v >>= 1; n++; }
        return n;
    }

    // The bits of the binary number nearest num/den (positive), a tie to the even significand, with p bits after the
    // leading one; or null past the largest.
    static long? Nearest(BigInteger num, BigInteger den, int p, int least, int greatest)
    {
        if (num.IsZero) return 0;
        BigInteger first = BigInteger.One << p, limit = BigInteger.One << (p + 1);
        int exponent = Math.Max(least, BitLength(num) - BitLength(den) - (p + 1));
        BigInteger q, r, d;
        while (true)
        {
            if (exponent >= 0) { d = den << exponent; q = BigInteger.DivRem(num, d, out r); }
            else { d = den; q = BigInteger.DivRem(num << -exponent, d, out r); }
            if (q >= limit) exponent++;
            else if (q < first && exponent > least) exponent--;
            else break;
        }
        int half = (r << 1).CompareTo(d);
        if (half > 0 || (half == 0 && !q.IsEven)) q += 1;
        if (q == limit) { q >>= 1; exponent++; }
        if (exponent > greatest) return null;
        if (q < first) return (long)q;
        int bias = p == 52 ? 1075 : 150;
        return ((long)(exponent + bias) << p) | (long)(q - first);
    }

    static long? NearestDoubleBits(Dec x)
    {
        if (x.Exponent > 400) return null;
        if (x.Exponent < -400) return 0;
        return x.Power >= 0 ? Nearest(x.Unscaled * BigInteger.Pow(10, x.Power), 1, 52, -1074, 971)
                            : Nearest(x.Unscaled, BigInteger.Pow(10, -x.Power), 52, -1074, 971);
    }

    static long? NearestSingleBits(Dec x)
    {
        if (x.Exponent > 400) return null;
        if (x.Exponent < -400) return 0;
        return x.Power >= 0 ? Nearest(x.Unscaled * BigInteger.Pow(10, x.Power), 1, 23, -149, 104)
                            : Nearest(x.Unscaled, BigInteger.Pow(10, -x.Power), 23, -149, 104);
    }

    static Dec OfDoubleBits(long bits)
    {
        int field = (int)(bits >> 52);
        long fraction = bits & 0xFFFFFFFFFFFFFL;
        BigInteger m = field == 0 ? fraction : (fraction | (1L << 52));
        int e = field == 0 ? -1074 : field - 1075;
        return OfBinary(m, e);
    }

    static string Sci(Dec x, bool negative)
    {
        string digits = x.Digits;
        string s = digits.Substring(0, 1) + (digits.Length > 1 ? "." + digits.Substring(1) : "");
        int e = x.Exponent;
        return (negative ? "-" : "") + s + "E" + (e < 0 ? "-" : "+") + Math.Abs(e).ToString(Inv);
    }

    static string Positional(Dec x, bool negative)
    {
        string digits = x.Digits;
        int e = x.Exponent;
        string s;
        if (e < 0) s = "0." + new string('0', -e - 1) + digits;
        else if (digits.Length <= e + 1) s = digits + new string('0', e + 1 - digits.Length);
        else s = digits.Substring(0, e + 1) + "." + digits.Substring(e + 1);
        return (negative ? "-" : "") + s;
    }

    static Dec Cut(Dec x, int n, int add)
    {
        string digits = x.Digits;
        BigInteger u = BigInteger.Parse(digits.Substring(0, n), Inv) + add;
        return new Dec(u, x.Power + digits.Length - n);
    }

    static string Hex(long? bits, bool single)
    {
        if (bits == null) return "overflow";
        return single ? ((int)bits.Value).ToString("X8", Inv) : bits.Value.ToString("X16", Inv);
    }

    static void Add(List<TextCase> cases, string family, string form, Dec x, bool negative, bool positional)
    {
        var c = new TextCase();
        c.Family = family;
        c.Form = form;
        c.Text = positional ? Positional(x, negative) : Sci(x, negative);
        long sign = negative ? long.MinValue : 0;
        long? d = NearestDoubleBits(x);
        long? s = NearestSingleBits(x);
        long? via = d == null ? null : NearestSingleBits(OfDoubleBits(d.Value));
        c.NearestDouble = Hex(d == null ? (long?)null : d.Value | sign, false);
        c.NearestSingle = Hex(s == null ? (long?)null : s.Value | (negative ? 0x80000000L : 0), true);
        c.SingleViaDouble = Hex(via == null ? (long?)null : via.Value | (negative ? 0x80000000L : 0), true);
        cases.Add(c);
    }

    static void Forms(List<TextCase> cases, string family, Dec half, bool negative)
    {
        Add(cases, family, "half", half, negative, false);
        if (half.Exponent >= -25 && half.Exponent <= 30 && half.Digits.Length <= 60)
            Add(cases, family, "half-written-out", half, negative, true);
        bool writeOut = family.EndsWith("-written-out");
        foreach (int n in new[] { 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 25, 30 })
        {
            if (half.Digits.Length <= n) continue;
            Add(cases, family, "below-" + n, Cut(half, n, 0), negative, writeOut);
            Add(cases, family, "above-" + n, Cut(half, n, 1), negative, writeOut);
        }
        BigInteger far = BigInteger.Pow(10, 20);
        Add(cases, family, "below-far", new Dec(half.Unscaled * far - 1, half.Power - 20), negative, false);
        Add(cases, family, "above-far", new Dec(half.Unscaled * far + 1, half.Power - 20), negative, false);
    }

    public static List<TextCase> Make(int picks, int seed)
    {
        var random = new Random(seed);
        var cases = new List<TextCase>();
        Func<long> fraction52 = () => (((long)random.Next() << 31) + random.Next()) & 0xFFFFFFFFFFFFFL;
        Func<int> fraction23 = () => random.Next() & 0x7FFFFF;

        // Doubles: the family and the exponent fields drawn from.
        var doubleFamilies = new List<Tuple<string, int, int>> {
            Tuple.Create("double-near-1", 1013, 1033),
            Tuple.Create("double-whole-halves", 1075, 1076),
            Tuple.Create("double-large", 1323, 2046),
            Tuple.Create("double-small", 1, 723),
            Tuple.Create("double-subnormal", 0, 0),
            Tuple.Create("double-least-normal", 1, 64),
            Tuple.Create("double-tiny-written-out", 963, 983),
            Tuple.Create("double-whole-large-written-out", 1087, 1122),
        };
        foreach (var f in doubleFamilies)
            for (int i = 0; i < picks; i++)
            {
                long bits = ((long)random.Next(f.Item2, f.Item3 + 1) << 52) | fraction52();
                if (bits == 0) bits = 1;
                Forms(cases, f.Item1, DoubleHalf(bits), random.Next(2) == 1);
            }
        Forms(cases, "double-edge-largest", DoubleHalf(0x7FEFFFFFFFFFFFFFL), false);
        Forms(cases, "double-edge-least-normal", DoubleHalf(0x000FFFFFFFFFFFFFL), false);
        Forms(cases, "double-edge-least", OfBinary(1, -1075), false);
        Forms(cases, "double-edge-least", DoubleHalf(1), false);
        for (long k = 2; k <= 12; k++) Forms(cases, "double-least-subnormals", DoubleHalf(k), false);
        for (int k = 4; k <= 52; k += 4) Forms(cases, "double-least-subnormals", DoubleHalf(1L << k), false);
        for (int k = 4; k <= 52; k += 4) Forms(cases, "double-least-subnormals", DoubleHalf((1L << k) + 1), false);

        var singleFamilies = new List<Tuple<string, int, int>> {
            Tuple.Create("single-near-1", 117, 137),
            Tuple.Create("single-whole-halves", 150, 151),
            Tuple.Create("single-large", 187, 254),
            Tuple.Create("single-small", 1, 67),
            Tuple.Create("single-subnormal", 0, 0),
            Tuple.Create("single-tiny-written-out", 67, 87),
            Tuple.Create("single-whole-large-written-out", 191, 226),
        };
        foreach (var f in singleFamilies)
            for (int i = 0; i < picks; i++)
            {
                int bits = (random.Next(f.Item2, f.Item3 + 1) << 23) | fraction23();
                if (bits == 0) bits = 1;
                Forms(cases, f.Item1, SingleHalf(bits), random.Next(2) == 1);
            }
        Forms(cases, "single-edge-largest", SingleHalf(0x7F7FFFFF), false);
        Forms(cases, "single-edge-least-normal", SingleHalf(0x007FFFFF), false);
        Forms(cases, "single-edge-least", OfBinary(1, -150), false);
        Forms(cases, "single-edge-least", SingleHalf(1), false);
        for (int k = 2; k <= 12; k++) Forms(cases, "single-least-subnormals", SingleHalf(k), false);
        for (int k = 3; k <= 23; k += 4) Forms(cases, "single-least-subnormals", SingleHalf((1 << k) + 1), false);

        // Text of 17 random digits at each power of ten of the least and the greatest doubles: where text is read
        // with an error of its own, rather than at a half.
        for (int e = -324; e <= 308; e++)
        {
            if (e > -290 && e < 290) continue;
            for (int i = 0; i < picks; i++)
            {
                string digits = (random.Next(1, 10)).ToString(Inv);
                for (int d = 1; d < 17; d++) digits += random.Next(10).ToString(Inv);
                Add(cases, "double-random-text", "e" + e.ToString(Inv), new Dec(BigInteger.Parse(digits, Inv), e - 16), random.Next(2) == 1, false);
            }
        }

        // The issue's own case.
        var issue = new TextCase();
        issue.Family = "issue"; issue.Form = "half-17";
        issue.Text = "-8.5635493928557935E15";
        issue.NearestDouble = Hex(unchecked((long)0xC33E6C80B793E2F2UL), false);
        issue.NearestSingle = Hex(NearestSingleBits(new Dec(BigInteger.Parse("85635493928557935"), -1)).Value | 0x80000000L, true);
        issue.SingleViaDouble = Hex(NearestSingleBits(OfDoubleBits(0x433E6C80B793E2F2L)).Value | 0x80000000L, true);
        cases.Add(issue);

        // Text of other forms: signs, zeros, leading zeros, and what .NET reads with NumberStyles.Any.
        foreach (string text in new[] {
            "0", "-0", "-0.0", "1E-400", "-1E-400", "1E+400", "+1.5", " 1.5 ", "1e5", "1E+05",
            "1.79769313486232E+308", "1.7976931348623158E+308", "1.797693134862315807937289714053E+308",
            "3.4028235E+38", "3.40282356779733661637539395458142568448E+38", "3.402823567797336616375393954582E+38",
            "000000000000000000001234567890123456789012345", "0.00000000000000000000001234567890123456789012345",
            "1234567890123456789012345678901234567890", "12345678901234567890.5", "1234567890123456789.05",
            "(1.5)", "$1.5", "1,234.5", "1.5-", "-$1.5", "1.5E", "1.5e+3" })
            cases.Add(Plain(text));
        return cases;
    }

    // A text case whose number is worked out when it is plainly written: a sign, digits with a point, an exponent.
    static TextCase Plain(string text)
    {
        var c = new TextCase();
        c.Family = "forms"; c.Form = "plain"; c.Text = text;
        c.NearestDouble = c.NearestSingle = c.SingleViaDouble = "-";
        var m = System.Text.RegularExpressions.Regex.Match(text.Trim(), @"^([+-]?)(\d*)(?:\.(\d*))?(?:[eE]([+-]?\d+))?$");
        if (!m.Success) return c;
        bool negative = m.Groups[1].Value == "-";
        string digits = m.Groups[2].Value + m.Groups[3].Value;
        int power = -m.Groups[3].Value.Length + (m.Groups[4].Success ? int.Parse(m.Groups[4].Value, Inv) : 0);
        var x = new Dec(BigInteger.Parse("0" + digits, Inv), power);
        if (x.Unscaled.IsZero)
        {
            c.NearestDouble = negative ? "8000000000000000" : "0000000000000000";
            c.NearestSingle = c.SingleViaDouble = negative ? "80000000" : "00000000";
            return c;
        }
        long? d = NearestDoubleBits(x);
        long? s = NearestSingleBits(x);
        long? via = d == null ? null : NearestSingleBits(OfDoubleBits(d.Value));
        c.NearestDouble = Hex(d == null ? (long?)null : d.Value | (negative ? long.MinValue : 0), false);
        c.NearestSingle = Hex(s == null ? (long?)null : s.Value | (negative ? 0x80000000L : 0), true);
        c.SingleViaDouble = Hex(via == null ? (long?)null : via.Value | (negative ? 0x80000000L : 0), true);
        return c;
    }

    // The round-trip text of a double or a single's exact value, for the record.
    public static string Exact(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        if (double.IsNaN(value) || double.IsInfinity(value)) return value.ToString(Inv);
        if (value == 0) return bits < 0 ? "-0" : "0";
        return Sci(OfDoubleBits(bits & long.MaxValue), bits < 0);
    }
}
'@

$inv = [Globalization.CultureInfo]::InvariantCulture
$culture = [Globalization.CultureInfo]::CurrentCulture
$cases = [TextToFloat]::Make($Picks, 124)

$dir = Join-Path ([IO.Path]::GetTempPath()) ('ace-text-float-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir | Out-Null
$path = Join-Path $dir 'text-float.accdb'
$dbe = New-Object -ComObject DAO.DBEngine.120
$db = $dbe.CreateDatabase($path, ';LANGID=0x0409;CP=1252;COUNTRY=0', 128)
$db.Close()

function Bits-Of($v, [bool] $single) {
    if ($v -is [DBNull] -or $v -eq $null) { return 'null' }
    if ($single) { return [BitConverter]::ToInt32([BitConverter]::GetBytes([float]$v), 0).ToString('X8', $inv) }
    return [BitConverter]::DoubleToInt64Bits([double]$v).ToString('X16', $inv)
}
function Message($e) {
    $m = $e.Exception.InnerException.Message; if (-not $m) { $m = $e.Exception.Message }
    return 'refused: ' + ($m -replace '\s+', ' ')
}

$connection = New-Object System.Data.OleDb.OleDbConnection("Provider=$Provider;OLE DB Services=-4;Data Source=$path")
$connection.Open()
function Run([string] $sql) { $c = $connection.CreateCommand(); $c.CommandText = $sql; [void]$c.ExecuteNonQuery() }
function Scalar([string] $sql) { $c = $connection.CreateCommand(); $c.CommandText = $sql; return $c.ExecuteScalar() }
Run 'CREATE TABLE Nums (Id LONG CONSTRAINT pkNums PRIMARY KEY, D FLOAT, S REAL)'
Run 'CREATE TABLE Texts (Id LONG CONSTRAINT pkTexts PRIMARY KEY, T TEXT(255))'
Run 'CREATE TABLE Source (Id LONG CONSTRAINT pkSource PRIMARY KEY, D FLOAT, S REAL)'

# Each text into each column, by literal and by parameter.
$results = @{}
$id = 0
try {
    foreach ($case in $cases) {
        foreach ($column in 'D', 'S') {
            $single = $column -eq 'S'
            $id++
            $literal = $null
            try {
                Run "INSERT INTO Nums (Id, $column) VALUES ($id, '$($case.Text)')"
                $literal = Bits-Of (Scalar "SELECT $column FROM Nums WHERE Id = $id") $single
            } catch { $literal = Message $_ }
            $id++
            $parameter = $null
            try {
                $c = $connection.CreateCommand()
                $c.CommandText = "INSERT INTO Nums (Id, $column) VALUES (?, ?)"
                $ip = New-Object System.Data.OleDb.OleDbParameter('id', [System.Data.OleDb.OleDbType]::Integer); $ip.Value = $id
                $vp = New-Object System.Data.OleDb.OleDbParameter('v', [System.Data.OleDb.OleDbType]::VarWChar); $vp.Value = $case.Text
                [void]$c.Parameters.Add($ip); [void]$c.Parameters.Add($vp)
                [void]$c.ExecuteNonQuery()
                $parameter = Bits-Of (Scalar "SELECT $column FROM Nums WHERE Id = $id") $single
            } catch { $parameter = Message $_ }
            $results["$($case.Text)|$column"] = @($literal, $parameter, $null)
        }
    }
} finally { $connection.Close() }

# Each text into each column by DAO.
$db = $dbe.OpenDatabase($path)
try {
    foreach ($case in $cases) {
        foreach ($column in 'D', 'S') {
            $single = $column -eq 'S'
            $id++
            $rs = $db.OpenRecordset('Nums', 2)
            $dao = $null
            try {
                $rs.AddNew()
                $rs.Fields.Item('Id').Value = [int]$id
                $field = $rs.Fields.Item($column)
                [void]$field.GetType().InvokeMember('Value', [Reflection.BindingFlags]::SetProperty, $null, $field, @([string]$case.Text))
                $rs.Update()
            } catch {
                $dao = Message $_
                try { $rs.CancelUpdate() } catch {}
            }
            $rs.Close()
            if ($dao -eq $null) { $results["$($case.Text)|$column"][2] = $id } else { $results["$($case.Text)|$column"][2] = $dao }
        }
    }

    # A sample of the texts as a property's value: DAO's CreateProperty with the text, as a Double (7) and a Single (6).
    $properties = New-Object 'System.Collections.Generic.List[string]'
    # A table of their own for each 200 of them: DAO refuses a property past some thousands on one table.
    $p = 0
    foreach ($case in $cases) {
        if ($case.Family -notin 'double-near-1', 'single-near-1', 'double-whole-halves', 'issue', 'forms') { continue }
        if ($p % 200 -eq 0) {
            $db.Execute("CREATE TABLE Props$p (Id LONG)")
            $db.TableDefs.Refresh()
            $table = $db.TableDefs.Item("Props$p")
        }
        $got = @()
        foreach ($type in 7, 6) {
            $p++
            try {
                $property = $table.CreateProperty("P$p", $type, [string]$case.Text)
                $table.Properties.Append($property)
                $got += Bits-Of $table.Properties.Item("P$p").Value ($type -eq 6)
            } catch { $got += Message $_ }
        }
        $properties.Add("property`t$($case.Family)`t$($case.Form)`t$($case.Text)`t$($case.NearestDouble)`t$($got[0])`t$($case.NearestSingle)`t$($got[1])")
    }
} finally { $db.Close() }

$connection.Open()
$lines = New-Object 'System.Collections.Generic.List[string]'
$lines.Add("# culture $($culture.Name); $Provider; .NET Framework $([Environment]::Version) in a 64-bit process; picks $Picks")
$lines.Add("# text`tfamily`tform`ttext`tnearest double`tace literal`tace parameter`tdao`tdouble.Parse`tnearest single`tsingle via double`tace literal`tace parameter`tdao`tfloat.Parse")
try {
    foreach ($case in $cases) {
        $row = @('text', $case.Family, $case.Form, $case.Text)
        foreach ($column in 'D', 'S') {
            $single = $column -eq 'S'
            $r = $results["$($case.Text)|$column"]
            $dao = $r[2]
            if ($dao -is [int]) { $dao = Bits-Of (Scalar "SELECT $column FROM Nums WHERE Id = $dao") $single }
            $net = $null
            try {
                if ($single) { $net = Bits-Of ([float]::Parse($case.Text, [Globalization.NumberStyles]::Any, $culture)) $true }
                else { $net = Bits-Of ([double]::Parse($case.Text, [Globalization.NumberStyles]::Any, $culture)) $false }
            } catch { $net = 'refused: ' + $_.Exception.InnerException.GetType().Name }
            if ($single) { $row += @($case.NearestSingle, $case.SingleViaDouble, $r[0], $r[1], $dao, $net) }
            else { $row += @($case.NearestDouble, $r[0], $r[1], $dao, $net) }
        }
        $lines.Add($row -join "`t")
    }
    $lines.Add("# property`tfamily`tform`ttext`tnearest double`tdao double property`tnearest single`tdao single property")
    $lines.AddRange($properties)

    # ── The other way: a double or a single into a Text column ──
    # Commas bind before arithmetic in PowerShell: each sum and quotient is in parentheses.
    $doubles = @((0.1 + 0.2), (1.0 / 3), (2.0 / 3), 0.1, 100.0, 1e15, 1e16, 1e17, 123456789012345.0, 1234567890123456.0,
        1000000000000005.0, 1000000000000015.0, 0.5, 1e-4, 1e-5, 1e-6, 1e-7, 1.5e-10, 12345.678901234567,
        [Math]::PI, (-[Math]::E), [double]::MaxValue, [double]::Epsilon, 2.2250738585072014E-308, 1152921504606846976.0,
        (5e-324 * 3), [BitConverter]::Int64BitsToDouble([long]::MinValue), 0.0, 9007199254740993.0, (0.30000000000000004 * 3),
        1.7976931348623157E+300, -8563549392855794.0, 4.35, 2.675, 1.0000000000000002, 0.9999999999999999,
        1.23456789012345E-5, 1.2345E-10, 1E-14, 1E-15, 1E-16, 1E-20, 1.5E-20, 1.23456789012345E-15,
        1.23456789012345E-20, 0.000123456789012345, 999999999999999.0, 999999999999999.5, 99999999999999.95,
        9.9999999999999995E-5, -1.5E-300, 123.456, 0.1234567890123455, 1.25, 0.125, 2.5, 1000000000000025.0,
        -1000000000000005.0, 1.00000000000005, 1.00000000000015, 12345678901234.5, 123456789012345.5,
        [BitConverter]::Int64BitsToDouble([BitConverter]::DoubleToInt64Bits(1e-15) - 1),
        [BitConverter]::Int64BitsToDouble([BitConverter]::DoubleToInt64Bits(1e-5) - 1),
        [BitConverter]::Int64BitsToDouble([BitConverter]::DoubleToInt64Bits(1e15) - 1),
        9.999999999999995E+15, 1.234567890123456E-5, [double]::NaN, [double]::PositiveInfinity, [double]::NegativeInfinity,
        [BitConverter]::Int64BitsToDouble(0x7FF8000000000000))
    $singles = @([float]0.1, [float](1.0 / 3), [float]16777216, [float]16777217, [float]3.4028235E+38, [float]1.401298E-45,
        [float]1e-7, [float]1e-5, [float]123456.7, [float]1234567.8, [float]0.3, [float]2.675, [float]-1.5, [float]1e10,
        [float]9999999, [float]12345678, [float]1.5e-10, [float]1.234567e-5, [float]1e-20, [float]123456.78,
        [float]0.0001, [float]1e-6, [float]2.5, [float]1234567.5, [float]12345665, [float]12345675, [float]-12345665,
        [BitConverter]::ToSingle([BitConverter]::GetBytes([int]0x80000000), 0), [float]999999.94, [float]9.9999994E-8,
        [float]::NaN, [float]::PositiveInfinity)
    $tid = 0
    foreach ($kind in 'double', 'single') {
        $values = if ($kind -eq 'double') { $doubles } else { $singles }
        $column = if ($kind -eq 'double') { 'D' } else { 'S' }
        foreach ($v in $values) {
            $tid++
            $p = $null
            try {
                $c = $connection.CreateCommand()
                $c.CommandText = 'INSERT INTO Texts (Id, T) VALUES (?, ?)'
                $ip = New-Object System.Data.OleDb.OleDbParameter('id', [System.Data.OleDb.OleDbType]::Integer); $ip.Value = $tid
                $type = if ($kind -eq 'double') { [System.Data.OleDb.OleDbType]::Double } else { [System.Data.OleDb.OleDbType]::Single }
                $vp = New-Object System.Data.OleDb.OleDbParameter('v', $type); $vp.Value = $v
                [void]$c.Parameters.Add($ip); [void]$c.Parameters.Add($vp)
                [void]$c.ExecuteNonQuery()
                $p = [string](Scalar "SELECT T FROM Texts WHERE Id = $tid")
            } catch { $p = Message $_ }

            $copied = $null
            try {
                $c = $connection.CreateCommand()
                $c.CommandText = "INSERT INTO Source (Id, $column) VALUES (?, ?)"
                $ip = New-Object System.Data.OleDb.OleDbParameter('id', [System.Data.OleDb.OleDbType]::Integer); $ip.Value = $tid
                $vp = New-Object System.Data.OleDb.OleDbParameter('v', $type); $vp.Value = $v
                [void]$c.Parameters.Add($ip); [void]$c.Parameters.Add($vp)
                [void]$c.ExecuteNonQuery()
                Run "INSERT INTO Texts (Id, T) SELECT Id + 100000, $column FROM Source WHERE Id = $tid"
                $copied = [string](Scalar "SELECT T FROM Texts WHERE Id = $($tid + 100000)")
            } catch { $copied = Message $_ }

            $bits = if ($kind -eq 'double') { Bits-Of $v $false } else { Bits-Of $v $true }
            $exact = if ($kind -eq 'double') { [TextToFloat]::Exact([double]$v) } else { [TextToFloat]::Exact([double][float]$v) }
            $lines.Add("tostring`t$kind`t$bits`t$exact`t$p`t$copied`t{dao $tid}`t$([Convert]::ToString($v))")
        }
    }
} finally { $connection.Close() }

# DAO assigns each double and single to the Text column.
$db = $dbe.OpenDatabase($path)
try {
    $tid = 0
    foreach ($kind in 'double', 'single') {
        $values = if ($kind -eq 'double') { $doubles } else { $singles }
        foreach ($v in $values) {
            $tid++
            $text = $null
            $rs = $db.OpenRecordset('Texts', 2)
            try {
                $rs.AddNew()
                $rs.Fields.Item('Id').Value = [int]($tid + 200000)
                $field = $rs.Fields.Item('T')
                [void]$field.GetType().InvokeMember('Value', [Reflection.BindingFlags]::SetProperty, $null, $field, @($v))
                $rs.Update()
                $r2 = $db.OpenRecordset("SELECT T FROM Texts WHERE Id = $($tid + 200000)", 4)
                $text = [string]$r2.Fields.Item('T').Value
                $r2.Close()
            } catch {
                $text = Message $_
                try { $rs.CancelUpdate() } catch {}
            }
            $rs.Close()
            # And as a Text property's value (dbText, 10).
            $property = $null
            try {
                $created = $db.TableDefs.Item('Texts').CreateProperty("T$tid", 10, $v)
                $db.TableDefs.Item('Texts').Properties.Append($created)
                $property = [string]$db.TableDefs.Item('Texts').Properties.Item("T$tid").Value
            } catch { $property = Message $_ }
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i].Contains("`t{dao $tid}`t")) { $lines[$i] = $lines[$i].Replace("`t{dao $tid}`t", "`t$text`t$property`t"); break }
            }
        }
    }
} finally { $db.Close() }
$lines.Insert(2, "# tostring`tkind`tbits`texact`tace parameter`tace insert-select`tdao`tdao text property`tConvert.ToString")

[IO.File]::WriteAllText($Out, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
try { Remove-Item -Recurse -Force $dir } catch { Write-Warning "The database is left at $($dir): $($_.Exception.Message)" }
Write-Host "written $($lines.Count) lines to $Out"
