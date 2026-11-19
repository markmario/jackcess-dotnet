using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// An import holds every value of its source exactly, or refuses before anything is written (#44): the column a
/// CLR type is imported into, the values given to a table that is there, what the source says of nulls and keys,
/// and that a refused import - for a value, or for a row the database refuses part-way - leaves the file byte for
/// byte as it was.
/// </summary>
public sealed class ImportExactnessTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"exact_{Guid.NewGuid():N}.mdb");
    private readonly string _accdb = Path.Combine(Path.GetTempPath(), $"exact_{Guid.NewGuid():N}.accdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        if (File.Exists(_accdb)) File.Delete(_accdb);
    }

    private static DataTable TableOf(string name, Type type, params object?[] values)
    {
        var table = new DataTable("T");
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add(name, type);
        for (int i = 0; i < values.Length; i++) table.Rows.Add(i + 1, values[i] ?? DBNull.Value);
        return table;
    }

    private static Column ColumnNamed(Table table, string name) => table.Columns.Single(c => c.Name == name);

    private static List<object?> ValuesOf(Database db, string table, string column)
        => db.GetTable(table).Rows.OrderBy(r => (int)r["Id"]!).Select(r => r[column]).ToList();

    /// <summary>Runs <paramref name="refused"/> on the file at <paramref name="path"/> and asserts the file is byte for byte as it was.</summary>
    private static TException RefusedAndUnchanged<TException>(string path, Action<Database> refused) where TException : Exception
    {
        byte[] before = File.ReadAllBytes(path);
        TException exception;
        using (var db = Database.Open(path))
            exception = Assert.ThrowsAny<TException>(() => refused(db));
        Assert.Equal(before, File.ReadAllBytes(path));
        return exception;
    }

    // ── The column a CLR type is imported into ───────────────────────────────

    [Fact]
    public void Signed_bytes_and_unsigned_shorts_go_to_columns_that_hold_their_whole_range()
    {
        var table = TableOf("S", typeof(sbyte), sbyte.MinValue, (sbyte)-1, sbyte.MaxValue);
        table.Columns.Add("U", typeof(ushort));
        table.Rows[0]["U"] = ushort.MaxValue; table.Rows[1]["U"] = (ushort)32768; table.Rows[2]["U"] = (ushort)0;

        using (var db = Database.Create(_path))
            db.ImportTable(table);

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("T");
        Assert.Equal(DataType.Int16, ColumnNamed(t, "S").DataType);
        Assert.Equal(DataType.Int32, ColumnNamed(t, "U").DataType);
        Assert.Equal(new object?[] { (short)-128, (short)-1, (short)127 }, ValuesOf(reopen, "T", "S"));
        Assert.Equal(new object?[] { 65535, 32768, 0 }, ValuesOf(reopen, "T", "U"));
        Assert.Equal(new sbyte[] { -128, -1, 127 }, t.ReadRows<SignedPoco>().OrderBy(p => p.Id).Select(p => p.S));
    }

    private sealed class SignedPoco
    {
        public int Id { get; set; }
        public sbyte S { get; set; }
    }

    [Fact]
    public void A_uint_is_a_Large_Number_in_an_accdb_and_a_Decimal_of_10_digits_in_an_mdb()
    {
        var table = TableOf("V", typeof(uint), uint.MaxValue, 2_147_483_648u, 0u);
        using (var db = Database.Create(_accdb, FileFormat.Access2010)) db.ImportTable(table);
        using (var db = Database.Create(_path)) db.ImportTable(table);

        using (var accdb = Database.Open(_accdb))
        {
            Assert.Equal(DataType.Int64, ColumnNamed(accdb.GetTable("T"), "V").DataType);
            Assert.Equal(new object?[] { 4_294_967_295L, 2_147_483_648L, 0L }, ValuesOf(accdb, "T", "V"));
        }
        using var mdb = Database.Open(_path);
        var column = ColumnNamed(mdb.GetTable("T"), "V");
        Assert.Equal((DataType.Decimal, (byte)10, (byte)0), (column.DataType, column.Precision, column.Scale));
        Assert.Equal(new object?[] { 4_294_967_295m, 2_147_483_648m, 0m }, ValuesOf(mdb, "T", "V"));
    }

    [Fact]
    public void A_ulong_is_a_Decimal_of_20_digits()
    {
        var table = TableOf("V", typeof(ulong), ulong.MaxValue, 9_223_372_036_854_775_808ul, 0ul);
        using (var db = Database.Create(_accdb, FileFormat.Access2010)) db.ImportTable(table);

        using var reopen = Database.Open(_accdb);
        var column = ColumnNamed(reopen.GetTable("T"), "V");
        Assert.Equal((DataType.Decimal, (byte)20, (byte)0), (column.DataType, column.Precision, column.Scale));
        Assert.Equal(new object?[] { 18_446_744_073_709_551_615m, 9_223_372_036_854_775_808m, 0m }, ValuesOf(reopen, "T", "V"));
    }

    [Fact]
    public void Floats_and_doubles_keep_every_bit_NaN_and_infinities_among_them()
    {
        // Objects, not a DataTable: a DataTable keeps -0 as 0.
        double[] doubles = { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.0, double.Epsilon, double.MaxValue, 0.1 };
        float[] floats = { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -0.0f, float.Epsilon, float.MaxValue, 0.1f };
        var objects = doubles.Select((d, i) => new FloatingPoco { Id = i + 1, D = d, F = floats[i] }).ToArray();

        using (var db = Database.Create(_path)) db.ImportTable(objects, new ImportOptions { TableName = "T" });

        using var reopen = Database.Open(_path);
        Assert.Equal(doubles.Select(BitConverter.DoubleToInt64Bits), ValuesOf(reopen, "T", "D").Select(v => BitConverter.DoubleToInt64Bits((double)v!)));
        Assert.Equal(floats.Select(f => BitConverter.ToInt32(BitConverter.GetBytes(f), 0)),
                     ValuesOf(reopen, "T", "F").Select(v => BitConverter.ToInt32(BitConverter.GetBytes((float)v!), 0)));
    }

    private sealed class FloatingPoco
    {
        public int Id { get; set; }
        public double D { get; set; }
        public float F { get; set; }
    }

    [Fact]
    public void A_decimal_is_a_Decimal_whose_scale_and_precision_the_values_give()
    {
        // 1.23456 has five places: a Currency column kept four and stored 1.2346 (#44).
        var table = TableOf("Price", typeof(decimal), 1.23456m, -3.10m, 12345678901234.5m, null);
        using (var db = Database.Create(_path)) db.ImportTable(table);

        using var reopen = Database.Open(_path);
        var column = ColumnNamed(reopen.GetTable("T"), "Price");
        Assert.Equal((DataType.Decimal, (byte)19, (byte)5), (column.DataType, column.Precision, column.Scale));
        var read = ValuesOf(reopen, "T", "Price");
        Assert.Equal(new object?[] { 1.23456m, -3.1m, 12345678901234.5m, null }, read);
        // Read at the column's scale: 1.23456 to the last digit.
        Assert.Equal("1.23456", ((decimal)read[0]!).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_decimal_column_is_18_digits_at_least_and_28_at_most()
    {
        var small = TableOf("V", typeof(decimal), 1.5m, 2.25m);
        var wide = TableOf("V", typeof(decimal), 1234567890123456789012345678m, -1m);
        var fine = TableOf("V", typeof(decimal), 0.0000000000000000000000000001m);
        using (var db = Database.Create(_path))
        {
            db.ImportTable(small, new ImportOptions { TableName = "Small" });
            db.ImportTable(wide, new ImportOptions { TableName = "Wide" });
            db.ImportTable(fine, new ImportOptions { TableName = "Fine" });
        }

        using var reopen = Database.Open(_path);
        Column Of(string t) => ColumnNamed(reopen.GetTable(t), "V");
        Assert.Equal(((byte)18, (byte)2), (Of("Small").Precision, Of("Small").Scale));
        Assert.Equal(((byte)28, (byte)0), (Of("Wide").Precision, Of("Wide").Scale));
        Assert.Equal(((byte)28, (byte)28), (Of("Fine").Precision, Of("Fine").Scale));
        Assert.Equal(new object?[] { 1234567890123456789012345678m, -1m }, ValuesOf(reopen, "Wide", "V"));
        Assert.Equal(0.0000000000000000000000000001m, ValuesOf(reopen, "Fine", "V").Single());
    }

    [Fact]
    public void A_decimal_s_trailing_zeros_are_given_up_only_where_keeping_them_needs_more_than_28_digits()
    {
        // 28 digits before the point and 1.0's one place after it would be 29: the zero is given up, as no value
        // needs it.
        var table = TableOf("V", typeof(decimal), 1234567890123456789012345678m, 1.0m);
        using (var db = Database.Create(_path)) db.ImportTable(table);

        using var reopen = Database.Open(_path);
        var column = ColumnNamed(reopen.GetTable("T"), "V");
        Assert.Equal(((byte)28, (byte)0), (column.Precision, column.Scale));
        Assert.Equal(new object?[] { 1234567890123456789012345678m, 1m }, ValuesOf(reopen, "T", "V"));
    }

    [Fact]
    public void Decimals_that_need_more_than_28_digits_are_refused_before_anything_is_written()
    {
        Database.Create(_path).Dispose();
        var table = TableOf("V", typeof(decimal), 1234567890123456789012345678m, 0.5m);
        var refusal = RefusedAndUnchanged<ArgumentException>(_path, db => db.ImportTable(table));
        Assert.Equal("table", refusal.ParamName);
        Assert.Contains("29 digits", refusal.Message);

        // A value of 29 significant digits is past any Decimal column.
        var single = TableOf("V", typeof(decimal), 7.9228162514264337593543950335m);
        RefusedAndUnchanged<ArgumentException>(_path, db => db.ImportTable(single));
    }

    [Fact]
    public void Bytes_are_Binary_only_where_a_length_is_given_and_every_value_fits_it()
    {
        var rows = new[]
        {
            new BytesPoco { Id = 1, Short = new byte[] { 1, 2, 3 }, Unbounded = new byte[] { 4 }, Overflowing = new byte[10] },
            new BytesPoco { Id = 2, Short = Array.Empty<byte>(), Unbounded = new byte[5000], Overflowing = new byte[11] },
            new BytesPoco { Id = 3, Short = null, Unbounded = null, Overflowing = null },
        };
        rows[1].Unbounded![4999] = 0xAB;
        using (var db = Database.Create(_path)) db.ImportTable(rows, new ImportOptions { TableName = "T" });

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("T");
        Assert.Equal((DataType.Binary, 10), (ColumnNamed(t, "Short").DataType, ColumnNamed(t, "Short").MaxLength));
        Assert.Equal(DataType.OleObject, ColumnNamed(t, "Unbounded").DataType);
        Assert.Equal(DataType.OleObject, ColumnNamed(t, "Overflowing").DataType);
        Assert.Equal(new object?[] { new byte[] { 1, 2, 3 }, Array.Empty<byte>(), null }, ValuesOf(reopen, "T", "Short"));
        Assert.Equal(rows.Select(r => (object?)r.Unbounded), ValuesOf(reopen, "T", "Unbounded"));
        Assert.Equal(rows.Select(r => (object?)r.Overflowing), ValuesOf(reopen, "T", "Overflowing"));
    }

    private sealed class BytesPoco
    {
        public int Id { get; set; }
        [MaxLength(10)] public byte[]? Short { get; set; }
        public byte[]? Unbounded { get; set; }
        [MaxLength(10)] public byte[]? Overflowing { get; set; }
    }

    [Fact]
    public void Text_is_Text_only_where_every_value_fits_its_length()
    {
        var rows = new[]
        {
            new TextPoco { Id = 1, NoLength = new string('a', 255), Short = "abcde", Overflowing = "abcdef" },
            new TextPoco { Id = 2, NoLength = "b", Short = "", Overflowing = null },
        };
        var longer = TableOf("NoLength", typeof(string), new string('x', 256), "y");
        using (var db = Database.Create(_path))
        {
            db.ImportTable(rows, new ImportOptions { TableName = "Objects" });
            db.ImportTable(longer, new ImportOptions { TableName = "Longer" });
        }

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("Objects");
        Assert.Equal((DataType.Text, 255), (ColumnNamed(t, "NoLength").DataType, ColumnNamed(t, "NoLength").MaxLength));
        Assert.Equal((DataType.Text, 5), (ColumnNamed(t, "Short").DataType, ColumnNamed(t, "Short").MaxLength));
        Assert.Equal(DataType.Memo, ColumnNamed(t, "Overflowing").DataType);
        Assert.Equal(DataType.Memo, ColumnNamed(reopen.GetTable("Longer"), "NoLength").DataType);
        Assert.Equal(new object?[] { new string('x', 256), "y" }, ValuesOf(reopen, "Longer", "NoLength"));
        Assert.Equal(new object?[] { "abcdef", null }, ValuesOf(reopen, "Objects", "Overflowing"));
    }

    private sealed class TextPoco
    {
        public int Id { get; set; }
        public string? NoLength { get; set; }
        [MaxLength(5)] public string? Short { get; set; }
        [MaxLength(5)] public string? Overflowing { get; set; }
    }

    [Fact]
    public void A_null_for_a_Yes_No_column_is_refused_before_anything_is_written()
    {
        Database.Create(_path).Dispose();
        var table = TableOf("Flag", typeof(bool), true, null);
        var refusal = RefusedAndUnchanged<ArgumentException>(_path, db => db.ImportTable(table));
        Assert.Contains("Flag", refusal.Message);
        Assert.Contains("DataTable.Rows[1]", refusal.Message);

        var objects = new[] { new NullableFlagPoco { Id = 1, Flag = null } };
        RefusedAndUnchanged<ArgumentException>(_path, db => db.ImportTable(objects));
    }

    private sealed class NullableFlagPoco
    {
        public int Id { get; set; }
        public bool? Flag { get; set; }
    }

    [Fact]
    public void A_date_before_the_year_100_is_refused_and_one_after_is_kept_to_the_millisecond()
    {
        Database.Create(_path).Dispose();
        foreach (var early in new[] { DateTime.MinValue, new DateTime(99, 12, 31) })
            RefusedAndUnchanged<ArgumentException>(_path, db => db.ImportTable(TableOf("When", typeof(DateTime), early)));

        var kept = new[] { new DateTime(100, 1, 1), new DateTime(2026, 11, 19, 7, 8, 9, 123), new DateTime(9999, 12, 31, 23, 59, 59, 999) };
        using (var db = Database.Open(_path)) db.ImportTable(TableOf("When", typeof(DateTime), kept.Cast<object?>().ToArray()));
        using var reopen = Database.Open(_path);
        Assert.Equal(kept.Cast<object?>(), ValuesOf(reopen, "T", "When"));
    }

    // ── Nullability and keys ─────────────────────────────────────────────────

    [Fact]
    public void A_DataColumn_that_allows_no_null_is_a_required_column()
    {
        var table = new DataTable("T");
        table.Columns.Add(new DataColumn("Id", typeof(int)) { AutoIncrement = true, AllowDBNull = false });
        table.Columns.Add(new DataColumn("Name", typeof(string)) { AllowDBNull = false });
        table.Columns.Add(new DataColumn("Note", typeof(string)));
        table.Columns.Add(new DataColumn("Flag", typeof(bool)) { AllowDBNull = false });
        table.Rows.Add(1, "Ada", null, true);

        using (var db = Database.Create(_path)) db.ImportTable(table);

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("T");
        Assert.True(ColumnNamed(t, "Name").IsRequired);
        Assert.False(ColumnNamed(t, "Note").IsRequired);
        // An AutoNumber and a Yes/No have a value in every row: Access gives neither the property.
        Assert.False(ColumnNamed(t, "Id").IsRequired);
        Assert.False(ColumnNamed(t, "Flag").IsRequired);
        Assert.Throws<ConstraintViolationException>(() => t.Insert(new Row { ["Name"] = null, ["Flag"] = false }));
    }

    [Fact]
    public void A_property_with_Required_or_of_a_value_type_that_is_not_nullable_is_a_required_column()
    {
        using (var db = Database.Create(_path))
            db.ImportTable(new[] { new RequiredPoco { Id = 1, Name = "Ada", Count = 3, Optional = null, When = new DateTime(2026, 1, 1) } });

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("RequiredPoco");
        Assert.True(ColumnNamed(t, "Name").IsRequired);
        Assert.True(ColumnNamed(t, "Count").IsRequired);
        Assert.True(ColumnNamed(t, "When").IsRequired);
        Assert.False(ColumnNamed(t, "Optional").IsRequired);
        Assert.False(ColumnNamed(t, "Free").IsRequired);
        Assert.False(ColumnNamed(t, "Done").IsRequired);
    }

    private sealed class RequiredPoco
    {
        public int Id { get; set; }
        [Required] public string? Name { get; set; }
        public int Count { get; set; }
        public int? Optional { get; set; }
        public string? Free { get; set; }
        public bool Done { get; set; }
        public DateTime When { get; set; }
    }

    [Fact]
    public void A_null_in_a_DataColumn_that_allows_none_takes_back_the_whole_import()
    {
        // A DataSet that does not enforce its constraints can hold one.
        var set = new DataSet { EnforceConstraints = false };
        var table = set.Tables.Add("T");
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add(new DataColumn("Name", typeof(string)) { AllowDBNull = false });
        table.Rows.Add(1, "Ada");
        table.Rows.Add(2, DBNull.Value);
        Database.Create(_path).Dispose();

        RefusedAndUnchanged<ConstraintViolationException>(_path, db => db.ImportTable(table));
    }

    [Fact]
    public void Several_Key_properties_are_a_key_of_several_columns_in_their_column_order_then_declared()
    {
        using (var db = Database.Create(_path))
        {
            db.ImportTable(new[] { new DeclaredKeys { Region = "N", Number = 7 } });
            db.ImportTable(new[] { new OrderedKeys { Region = "N", Number = 7, Line = 1 } });
        }

        using var reopen = Database.Open(_path);
        string[] KeyOf(string table) => reopen.GetTable(table).Indexes.Single(i => i.IsPrimaryKey).Columns.Select(c => c.Column.Name).ToArray();
        Assert.Equal(new[] { "Region", "Number" }, KeyOf("DeclaredKeys"));
        Assert.Equal(new[] { "Number", "Region", "Line" }, KeyOf("OrderedKeys"));
        Assert.NotNull(reopen.GetTable("DeclaredKeys").FindRowByPrimaryKey("N", 7));
    }

    private sealed class DeclaredKeys
    {
        [Key] public string? Region { get; set; }
        [Key] public int Number { get; set; }
    }

    private sealed class OrderedKeys
    {
        [Key, Column(Order = 1)] public string? Region { get; set; }
        [Key, Column(Order = 0)] public int Number { get; set; }
        [Key] public int Line { get; set; }
    }

    [Fact]
    public void A_key_on_a_Decimal_column_is_refused_before_anything_is_written()
    {
        // A decimal is a Decimal column, and so is a uint in an .mdb: this library writes no index keys for one.
        Database.Create(_path).Dispose();
        RefusedAndUnchanged<NotSupportedException>(_path, db => db.ImportTable(new[] { new DecimalKeyed { Price = 1.5m } }));
        RefusedAndUnchanged<NotSupportedException>(_path, db => db.ImportTable(new[] { new UIntKeyed { Id = 1 } }));

        // In an .accdb a uint is a Large Number, whose keys are written.
        using (var db = Database.Create(_accdb, FileFormat.Access2010))
            db.ImportTable(new[] { new UIntKeyed { Id = uint.MaxValue } });
        using var reopen = Database.Open(_accdb);
        Assert.NotNull(reopen.GetTable("UIntKeyed").FindRowByPrimaryKey((long)uint.MaxValue));
    }

    private sealed class DecimalKeyed
    {
        [Key] public decimal Price { get; set; }
    }

    private sealed class UIntKeyed
    {
        [Key] public uint Id { get; set; }
    }

    // ── Whole or not at all ──────────────────────────────────────────────────

    [Fact]
    public void A_row_the_table_refuses_takes_back_the_table_and_the_rows_before_it()
    {
        Database.Create(_path).Dispose();
        var table = TableOf("Name", typeof(string), "a", "b", "c");
        table.Rows[2]["Id"] = 1;   // a duplicate in the primary key, in the last row

        RefusedAndUnchanged<ConstraintViolationException>(_path, db => db.ImportTable(table, new ImportOptions { PrimaryKey = new[] { "Id" } }));

        // The session that refused it can import the table afterwards: nothing of the first try is held.
        using (var db = Database.Open(_path))
        {
            Assert.Throws<ConstraintViolationException>(() => db.ImportTable(table, new ImportOptions { PrimaryKey = new[] { "Id" } }));
            table.Rows[2]["Id"] = 3;
            db.ImportTable(table, new ImportOptions { PrimaryKey = new[] { "Id" } });
            Assert.Equal(3, db.GetTable("T").Rows.Count());
        }
    }

    [Fact]
    public void A_DataSet_is_imported_whole_or_not_at_all()
    {
        Database.Create(_path).Dispose();
        var set = new DataSet();
        var first = set.Tables.Add("First");
        first.Columns.Add("Id", typeof(int));
        first.Rows.Add(1);
        var second = set.Tables.Add("Second");
        second.Columns.Add("Flag", typeof(bool));
        second.Rows.Add(DBNull.Value);

        // The second table's value is refused before the first is made.
        RefusedAndUnchanged<ArgumentException>(_path, db => db.ImportTables(set));

        // A row the second table refuses, once both are made, takes back the first: a DataSet that does not enforce
        // its constraints holds a duplicate in a key.
        var unchecked_ = new DataSet { EnforceConstraints = false };
        var one = unchecked_.Tables.Add("First");
        one.Columns.Add("Id", typeof(int));
        one.Rows.Add(1);
        var two = unchecked_.Tables.Add("Second");
        two.Columns.Add("Id", typeof(int));
        two.PrimaryKey = new[] { two.Columns["Id"]! };
        two.Rows.Add(1);
        two.Rows.Add(1);
        RefusedAndUnchanged<ConstraintViolationException>(_path, db => db.ImportTables(unchecked_, new ImportOptions()));
    }

    // ── Adding to a table that is there ──────────────────────────────────────

    private void TableWithEveryType()
    {
        using var db = Database.Create(_path);
        db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32),
            new ColumnDefinition("B", DataType.Byte),
            new ColumnDefinition("I", DataType.Int16),
            new ColumnDefinition("L", DataType.Int32),
            new ColumnDefinition("S", DataType.Single),
            new ColumnDefinition("D", DataType.Double),
            new ColumnDefinition("C", DataType.Currency),
            new ColumnDefinition("N", DataType.Decimal) { Precision = 6, Scale = 2 },
            new ColumnDefinition("Y", DataType.Boolean),
            new ColumnDefinition("X", DataType.Text) { MaxLength = 5 },
            new ColumnDefinition("Bin", DataType.Binary) { MaxLength = 4 },
            new ColumnDefinition("When", DataType.DateTime),
            new ColumnDefinition("G", DataType.Guid),
        }, "Id").Insert(new Row { ["Id"] = 1, ["Y"] = true });
    }

    // A column, the type of the source's column, a value the table's column holds and one it does not hold exactly.
    public static IEnumerable<object[]> ValuesNotHeld() => new[]
    {
        new object[] { "B", typeof(int), 255, 256 },
        new object[] { "B", typeof(int), 0, -1 },
        new object[] { "I", typeof(int), -32768, 32768 },
        new object[] { "L", typeof(long), 2_147_483_647L, 2_147_483_648L },
        new object[] { "L", typeof(double), 2.0, 1.5 },
        new object[] { "L", typeof(string), "42", "4.2" },
        new object[] { "L", typeof(string), "-7", "forty" },
        new object[] { "L", typeof(object), 7, true },
        new object[] { "S", typeof(double), 0.5, 0.1 },
        new object[] { "S", typeof(int), 16_777_216, 16_777_217 },
        // 0.1 is the shortest text of the double it reads as; no double's is 19 digits.
        new object[] { "D", typeof(decimal), 0.1m, 0.1234567890123456789m },
        new object[] { "C", typeof(decimal), 1.2345m, 1.23456m },
        new object[] { "C", typeof(double), 0.25, 1.0 / 3 },
        new object[] { "N", typeof(decimal), 1234.56m, 1.234m },
        new object[] { "N", typeof(decimal), 9999.99m, 10000m },
        new object[] { "Y", typeof(object), true, 1 },
        new object[] { "Bin", typeof(byte[]), new byte[4], new byte[5] },
        new object[] { "When", typeof(DateTime), new DateTime(2000, 1, 1), new DateTime(50, 1, 1) },
        new object[] { "G", typeof(string), Guid.Empty.ToString(), "not a guid" },
    };

    [Theory]
    [MemberData(nameof(ValuesNotHeld))]
    public void A_value_the_column_does_not_hold_exactly_is_refused_and_nothing_is_added(string column, Type type, object held, object value)
    {
        TableWithEveryType();
        var source = new DataTable("T");
        source.Columns.Add("Id", typeof(int));
        source.Columns.Add(column, type);
        source.Rows.Add(2, held);
        source.Rows.Add(3, value);

        var refusal = RefusedAndUnchanged<ArgumentException>(_path, db => db.ImportTable(source, new ImportOptions { AppendIfExists = true }));
        Assert.Equal("table", refusal.ParamName);
        Assert.Contains($"value of {column}", refusal.Message);
        Assert.Contains("DataTable.Rows[1]", refusal.Message);
    }

    [Fact]
    public void Text_longer_than_its_column_is_refused_as_too_long_and_nothing_is_added()
    {
        TableWithEveryType();
        var source = TableOf("X", typeof(string), "fits", "sixsix");
        var refusal = RefusedAndUnchanged<ColumnValueTooLongException>(_path, db => db.ImportTable(source, new ImportOptions { AppendIfExists = true }));
        Assert.Equal(("X", 5, 6), (refusal.ColumnName, refusal.MaxLength, refusal.Length));
    }

    [Fact]
    public void A_null_for_a_Yes_No_column_that_is_there_is_refused()
    {
        TableWithEveryType();
        var source = TableOf("Y", typeof(bool), true, null);
        RefusedAndUnchanged<ArgumentException>(_path, db => db.ImportTable(source, new ImportOptions { AppendIfExists = true }));
    }

    [Fact]
    public void Values_a_column_holds_exactly_are_added_as_it_holds_them()
    {
        TableWithEveryType();
        var source = new DataTable("T");
        foreach (var (name, type) in new[] { ("Id", typeof(long)), ("B", typeof(int)), ("I", typeof(double)), ("L", typeof(string)),
                                             ("S", typeof(decimal)), ("D", typeof(float)), ("C", typeof(double)), ("N", typeof(string)),
                                             ("Y", typeof(string)), ("X", typeof(int)), ("Bin", typeof(byte[])), ("When", typeof(DateTimeOffset)),
                                             ("G", typeof(string)) })
            source.Columns.Add(name, type);
        var guid = Guid.NewGuid();
        var when = new DateTimeOffset(2026, 11, 19, 10, 0, 0, TimeSpan.FromHours(11));
        source.Rows.Add(2L, 255, -32768.0, "-2147483648", 0.1m, 0.1f, 0.1, "1234.50", "True", 42, new byte[] { 1, 2, 3, 4 }, when, guid.ToString("B"));

        using (var db = Database.Open(_path))
            db.ImportTable(source, new ImportOptions { AppendIfExists = true });

        using var reopen = Database.Open(_path);
        var row = reopen.GetTable("T").FindRowByPrimaryKey(2)!;
        Assert.Equal((byte)255, row["B"]);
        Assert.Equal((short)-32768, row["I"]);
        Assert.Equal(int.MinValue, row["L"]);
        Assert.Equal(0.1f, row["S"]);
        Assert.Equal((double)0.1f, row["D"]);
        Assert.Equal(0.1m, row["C"]);
        Assert.Equal(1234.50m, row["N"]);
        Assert.Equal(true, row["Y"]);
        Assert.Equal("42", row["X"]);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, row["Bin"]);
        Assert.Equal(when.UtcDateTime, row["When"]);
        Assert.Equal(guid, row["G"]);
    }

    [Fact]
    public void A_row_an_appended_table_refuses_takes_back_every_row_before_it()
    {
        TableWithEveryType();
        var source = TableOf("X", typeof(string), "a", "b", "c");
        source.Rows[0]["Id"] = 4;
        source.Rows[1]["Id"] = 5;
        source.Rows[2]["Id"] = 1;   // the row the table already has

        RefusedAndUnchanged<ConstraintViolationException>(_path, db => db.ImportTable(source, new ImportOptions { AppendIfExists = true }));
        using (var reopen = Database.Open(_path))
            Assert.Single(reopen.GetTable("T").Rows);

        // The session that refused it appends the rows afterwards and finds each through the key: nothing of the
        // first try is held, in the table's rows or in its index.
        using (var db = Database.Open(_path))
        {
            Assert.Throws<ConstraintViolationException>(() => db.ImportTable(source, new ImportOptions { AppendIfExists = true }));
            source.Rows[2]["Id"] = 6;
            db.ImportTable(source, new ImportOptions { AppendIfExists = true });
            var table = db.GetTable("T");
            Assert.Equal(4, table.RowCount);
            foreach (int id in new[] { 1, 4, 5, 6 })
                Assert.NotNull(table.FindRowByPrimaryKey(id));
        }
        using var after = Database.Open(_path);
        Assert.Equal(new[] { 1, 4, 5, 6 }, after.GetTable("T").Rows.Select(r => (int)r["Id"]!).OrderBy(id => id));
        Assert.Equal("c", after.GetTable("T").FindRowByPrimaryKey(6)!["X"]);
    }

    [Fact]
    public void An_AutoNumber_value_an_append_does_not_keep_is_not_looked_at()
    {
        using (var db = Database.Create(_path))
            db.CreateTable("T", new[] { new ColumnDefinition("Id", DataType.Int32) { IsAutoNumber = true }, new ColumnDefinition("Name", DataType.Text) }, "Id");

        // A number past a Long Integer, which the table would number afresh anyway.
        var source = TableOf("Name", typeof(string), "a");
        source.Columns.Remove("Id");
        source.Columns.Add("Id", typeof(long));
        source.Rows[0]["Id"] = 5_000_000_000L;
        using (var db = Database.Open(_path))
            db.ImportTable(source, new ImportOptions { AppendIfExists = true });

        using var reopen = Database.Open(_path);
        Assert.Equal(1, reopen.GetTable("T").Rows.Single()["Id"]);
    }
}
