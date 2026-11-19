using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Tests for <see cref="DatabaseExporter"/> — turning an Access table back into
/// a <see cref="DataTable"/>, <see cref="DataSet"/>, or <c>IEnumerable&lt;T&gt;</c>.
/// </summary>
public sealed class DatabaseExporterTests : IDisposable
{
    private readonly string _path;
    public DatabaseExporterTests()
        => _path = Path.Combine(Path.GetTempPath(), $"exp_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    // ── DataTable ────────────────────────────────────────────────────────────

    [Fact]
    public void ToDataTable_BasicRoundTrip()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("Customers", new[]
            {
                new ColumnDefinition("Id",      DataType.Int32 ),
                new ColumnDefinition("Name",    DataType.Text ) { MaxLength = 50 },
                new ColumnDefinition("Joined",  DataType.DateTime),
                new ColumnDefinition("Balance", DataType.Currency),
                new ColumnDefinition("Active",  DataType.Boolean),
            }, primaryKey: "Id");
            t.Insert(new Row { ["Id"] = 1, ["Name"] = "Alice", ["Joined"] = new DateTime(2024, 1, 1), ["Balance"] = 100.50m, ["Active"] = true  });
            t.Insert(new Row { ["Id"] = 2, ["Name"] = "Bob",   ["Joined"] = new DateTime(2024, 2, 1), ["Balance"] =  -5.00m, ["Active"] = false });
        }

        using var reopen = Database.Open(_path);
        DataTable dt = reopen.GetTable("Customers").ToDataTable();

        Assert.Equal("Customers", dt.TableName);
        Assert.Equal(5, dt.Columns.Count);
        Assert.Equal(typeof(int),      dt.Columns["Id"]!.DataType);
        Assert.Equal(typeof(string),   dt.Columns["Name"]!.DataType);
        Assert.Equal(typeof(DateTime), dt.Columns["Joined"]!.DataType);
        Assert.Equal(typeof(decimal),  dt.Columns["Balance"]!.DataType);
        Assert.Equal(typeof(bool),     dt.Columns["Active"]!.DataType);
        Assert.Equal(50, dt.Columns["Name"]!.MaxLength);

        Assert.Equal(2, dt.Rows.Count);
        var byId = dt.Rows.Cast<DataRow>().OrderBy(r => (int)r["Id"]).ToList();
        Assert.Equal("Alice",  byId[0]["Name"]);
        Assert.Equal(100.50m,  byId[0]["Balance"]);
        Assert.Equal(true,     byId[0]["Active"]);
        Assert.Equal(false,    byId[1]["Active"]);

        // PK was round-tripped onto DataTable.PrimaryKey.
        Assert.Single(dt.PrimaryKey);
        Assert.Equal("Id", dt.PrimaryKey[0].ColumnName);

        // Loaded rows are not pending changes.
        foreach (DataRow r in dt.Rows)
            Assert.Equal(DataRowState.Unchanged, r.RowState);
    }

    [Fact]
    public void ToDataTable_NullValues_BecomeDBNull()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("Things", new[]
            {
                new ColumnDefinition("Id",   DataType.Int32),
                new ColumnDefinition("Note", DataType.Text) { MaxLength = 50 },
            });
            t.Insert(new Row { ["Id"] = 1, ["Note"] = null  });
            t.Insert(new Row { ["Id"] = 2, ["Note"] = "Hi"  });
        }

        using var reopen = Database.Open(_path);
        var dt = reopen.GetTable("Things").ToDataTable();
        var rows = dt.Rows.Cast<DataRow>().OrderBy(r => (int)r["Id"]).ToList();
        Assert.Equal(DBNull.Value, rows[0]["Note"]);
        Assert.Equal("Hi",         rows[1]["Note"]);
    }

    // ── DataSet ──────────────────────────────────────────────────────────────

    [Fact]
    public void ToDataSet_ReturnsAllUserTables()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            db.CreateTable("A", new[] { new ColumnDefinition("X", DataType.Int32) })
              .Insert(new Row { ["X"] = 1 });
            db.CreateTable("B", new[] { new ColumnDefinition("Y", DataType.Text) { MaxLength = 20 } })
              .Insert(new Row { ["Y"] = "hi" });
        }

        using var reopen = Database.Open(_path);
        DataSet ds = reopen.ToDataSet();

        Assert.Equal(2, ds.Tables.Count);
        Assert.NotNull(ds.Tables["A"]);
        Assert.NotNull(ds.Tables["B"]);
        Assert.Equal(1, ds.Tables["A"]!.Rows[0]["X"]);
        Assert.Equal("hi", ds.Tables["B"]!.Rows[0]["Y"]);
    }

    [Fact]
    public void ToDataSet_SystemTablesExcludedByDefault()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
            db.CreateTable("UserT", new[] { new ColumnDefinition("X", DataType.Int32) });

        using var reopen = Database.Open(_path);
        var ds = reopen.ToDataSet();
        Assert.All(ds.Tables.Cast<DataTable>(),
                   dt => Assert.DoesNotContain("MSys", dt.TableName));
    }

    // ── IEnumerable<T> ───────────────────────────────────────────────────────

    private sealed class CustomerPoco
    {
        public int      Id       { get; set; }
        public string?  Name     { get; set; }
        public DateTime Joined   { get; set; }
        public decimal  Balance  { get; set; }
        public bool     IsActive { get; set; }
    }

    [Fact]
    public void ReadRows_BasicRoundTrip()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("CustomerPoco", new[]
            {
                new ColumnDefinition("Id",       DataType.Int32),
                new ColumnDefinition("Name",     DataType.Text) { MaxLength = 50 },
                new ColumnDefinition("Joined",   DataType.DateTime),
                new ColumnDefinition("Balance",  DataType.Currency),
                new ColumnDefinition("IsActive", DataType.Boolean),
            });
            t.Insert(new Row { ["Id"] = 1, ["Name"] = "Ann", ["Joined"] = new DateTime(2024,1,1), ["Balance"] = 10m,  ["IsActive"] = true  });
            t.Insert(new Row { ["Id"] = 2, ["Name"] = "Bo",  ["Joined"] = new DateTime(2024,2,1), ["Balance"] = -5m,  ["IsActive"] = false });
        }

        using var reopen = Database.Open(_path);
        var saved = reopen.GetTable(nameof(CustomerPoco)).ReadRows<CustomerPoco>().OrderBy(c => c.Id).ToList();
        Assert.Equal(2, saved.Count);
        Assert.Equal("Ann", saved[0].Name);
        Assert.Equal(new DateTime(2024,1,1), saved[0].Joined);
        Assert.Equal(10m,  saved[0].Balance);
        Assert.True(saved[0].IsActive);
        Assert.False(saved[1].IsActive);
    }

    [Fact]
    public void ReadRows_ReadsTheTableItIsCalledOn_WhateverItsName()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
            db.CreateTable("OtherName", new[]
            {
                new ColumnDefinition("Id", DataType.Int32)
            }).Insert(new Row { ["Id"] = 7 });

        using var reopen = Database.Open(_path);
        var saved = reopen.GetTable("OtherName").ReadRows<CustomerPoco>().ToList();
        Assert.Equal(7, saved.Single().Id);
    }

    private sealed class RenamedColumnPoco
    {
        [Column("CustomerKey")]
        public int Key { get; set; }

        public string? Name { get; set; }

        [NotMapped] public string DerivedDisplay => $"{Key}:{Name}";

        public int  NotInTable { get; set; }   // extra property — should stay default
    }

    [Fact]
    public void ReadRows_RespectsColumnAndNotMapped_AndExtraPropertyStaysDefault()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("Renamed", new[]
            {
                new ColumnDefinition("CustomerKey", DataType.Int32),
                new ColumnDefinition("Name",        DataType.Text) { MaxLength = 50 },
                new ColumnDefinition("ExtraIgnored",DataType.Text) { MaxLength = 50 }, // column not on POCO
            });
            t.Insert(new Row { ["CustomerKey"] = 5, ["Name"] = "X", ["ExtraIgnored"] = "noise" });
        }

        using var reopen = Database.Open(_path);
        var saved = reopen.GetTable("Renamed").ReadRows<RenamedColumnPoco>().Single();

        Assert.Equal(5,    saved.Key);
        Assert.Equal("X",  saved.Name);
        Assert.Equal(0,    saved.NotInTable);   // never populated — extra property
        // DerivedDisplay was computed on the fly, not touched.
        Assert.Equal("5:X", saved.DerivedDisplay);
    }

    private sealed class NullableValuesPoco
    {
        public int       Id      { get; set; }
        public int?      OptInt  { get; set; }
        public DateTime? OptDate { get; set; }
        public string?   OptText { get; set; }
    }

    [Fact]
    public void ReadRows_NullableProperties_RoundTrip()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("NullableValuesPoco", new[]
            {
                new ColumnDefinition("Id",      DataType.Int32),
                new ColumnDefinition("OptInt",  DataType.Int32),
                new ColumnDefinition("OptDate", DataType.DateTime),
                new ColumnDefinition("OptText", DataType.Text) { MaxLength = 50 },
            });
            t.Insert(new Row { ["Id"] = 1, ["OptInt"] = 42, ["OptDate"] = new DateTime(2024,1,1), ["OptText"] = "hi"  });
            t.Insert(new Row { ["Id"] = 2, ["OptInt"] = null, ["OptDate"] = null, ["OptText"] = null });
        }

        using var reopen = Database.Open(_path);
        var saved = reopen.GetTable(nameof(NullableValuesPoco)).ReadRows<NullableValuesPoco>().OrderBy(c => c.Id).ToList();
        Assert.Equal(42,  saved[0].OptInt);
        Assert.Equal("hi", saved[0].OptText);
        Assert.Null(saved[1].OptInt);
        Assert.Null(saved[1].OptDate);
        Assert.Null(saved[1].OptText);
    }

    private enum Kind { Alpha = 1, Beta = 2, Gamma = 3 }
    private sealed class EnumPoco
    {
        public int  Id   { get; set; }
        public Kind Kind { get; set; }
    }

    [Fact]
    public void ReadRows_EnumProperty_ReadFromLongColumn()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("EnumPoco", new[]
            {
                new ColumnDefinition("Id",   DataType.Int32),
                new ColumnDefinition("Kind", DataType.Int32),
            });
            t.Insert(new Row { ["Id"] = 1, ["Kind"] = (int)Kind.Beta  });
            t.Insert(new Row { ["Id"] = 2, ["Kind"] = (int)Kind.Gamma });
        }

        using var reopen = Database.Open(_path);
        var saved = reopen.GetTable(nameof(EnumPoco)).ReadRows<EnumPoco>().OrderBy(c => c.Id).ToList();
        Assert.Equal(Kind.Beta,  saved[0].Kind);
        Assert.Equal(Kind.Gamma, saved[1].Kind);
    }

    private sealed class DateTimeOffsetPoco
    {
        public int            Id   { get; set; }
        public DateTimeOffset When { get; set; }
    }

    [Fact]
    public void ReadRows_DateTimeOffsetProperty_GetsUtcDateTime()
    {
        var stored = new DateTime(2024, 7, 4, 12, 0, 0, DateTimeKind.Unspecified);
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("DateTimeOffsetPoco", new[]
            {
                new ColumnDefinition("Id",   DataType.Int32),
                new ColumnDefinition("When", DataType.DateTime),
            });
            t.Insert(new Row { ["Id"] = 1, ["When"] = stored });
        }

        using var reopen = Database.Open(_path);
        var saved = reopen.GetTable(nameof(DateTimeOffsetPoco)).ReadRows<DateTimeOffsetPoco>().Single();
        Assert.Equal(stored, saved.When.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, saved.When.Offset);
    }

    [Fact]
    public void ReadRows_IsLazy_AndCanBeStoppedEarly()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("CustomerPoco", new[]
            {
                new ColumnDefinition("Id", DataType.Int32)
            });
            for (int i = 1; i <= 100; i++)
                t.Insert(new Row { ["Id"] = i });
        }

        using var reopen = Database.Open(_path);
        var firstThree = reopen.GetTable(nameof(CustomerPoco)).ReadRows<CustomerPoco>().Take(3).ToList();
        Assert.Equal(3, firstThree.Count);
        Assert.Equal(1, firstThree[0].Id);
    }

    // ── End-to-end via Importer ──────────────────────────────────────────────

    private sealed class FullRoundTripPoco
    {
        [Key] public int Id { get; set; }
        public string?   Label { get; set; }
        public double    Score { get; set; }
        public DateTime  When { get; set; }
        public bool      Flag { get; set; }
    }

    private sealed class StringEverywherePoco
    {
        public string? Id      { get; set; }
        public string? Score   { get; set; }
        public string? When    { get; set; }
        public string? Flag    { get; set; }
        public string? Marker  { get; set; }
    }

    [Fact]
    public void ReadRows_NonStringColumns_ReadIntoStringProperties()
    {
        // Schema has typed columns; the POCO has only string props. The
        // exporter must call ToString() to bridge each value.
        var ts     = new DateTime(2024, 7, 4, 12, 0, 0, DateTimeKind.Unspecified);
        var marker = new Guid("11111111-2222-3333-4444-555555555555");

        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("StringEverywherePoco", new[]
            {
                new ColumnDefinition("Id",     DataType.Int32),
                new ColumnDefinition("Score",  DataType.Double),
                new ColumnDefinition("When",   DataType.DateTime),
                new ColumnDefinition("Flag",   DataType.Boolean),
                new ColumnDefinition("Marker", DataType.Guid),
            });
            t.Insert(new Row
            {
                ["Id"]     = 7,
                ["Score"]  = 3.14,
                ["When"]   = ts,
                ["Flag"]   = true,
                ["Marker"] = marker,
            });
        }

        using var reopen = Database.Open(_path);
        var saved = reopen.GetTable(nameof(StringEverywherePoco)).ReadRows<StringEverywherePoco>().Single();

        Assert.Equal("7",          saved.Id);
        Assert.Equal((3.14).ToString(System.Globalization.CultureInfo.CurrentCulture), saved.Score);
        Assert.Equal(ts.ToString(System.Globalization.CultureInfo.CurrentCulture),      saved.When);
        Assert.Equal(true.ToString(), saved.Flag);
        Assert.Equal(marker.ToString(), saved.Marker);
    }

    [Fact]
    public void Importer_Then_Exporter_RoundTripsIntactValues()
    {
        var original = new[]
        {
            new FullRoundTripPoco { Id = 1, Label = "one", Score = 1.5,  When = new DateTime(2024,1,1), Flag = true  },
            new FullRoundTripPoco { Id = 2, Label = "two", Score = 2.25, When = new DateTime(2024,2,2), Flag = false },
            new FullRoundTripPoco { Id = 3, Label = "tre", Score = 9.99, When = new DateTime(2024,3,3), Flag = true  },
        };

        using (var db = Database.Create(_path, FileFormat.Access2000))
            db.ImportTable(original);

        using var reopen = Database.Open(_path);
        var back = reopen.GetTable(nameof(FullRoundTripPoco)).ReadRows<FullRoundTripPoco>().OrderBy(c => c.Id).ToList();
        Assert.Equal(3, back.Count);
        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(original[i].Id,    back[i].Id);
            Assert.Equal(original[i].Label, back[i].Label);
            Assert.Equal(original[i].Score, back[i].Score);
            Assert.Equal(original[i].When,  back[i].When);
            Assert.Equal(original[i].Flag,  back[i].Flag);
        }
    }

    // ── Schema-level coverage ────────────────────────────────────────────────

    [Fact]
    public void ToDataTable_TableWithoutPK_HasNoDataTablePK()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            db.CreateTable("NoPK", new[]
            {
                new ColumnDefinition("X", DataType.Int32),
            }).Insert(new Row { ["X"] = 1 });
        }

        using var reopen = Database.Open(_path);
        var dt = reopen.GetTable("NoPK").ToDataTable();
        Assert.Empty(dt.PrimaryKey);
    }

    [Fact]
    public void ToDataTable_ColumnOrderingMatchesAccessTable()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            db.CreateTable("Ordered", new[]
            {
                new ColumnDefinition("Charlie", DataType.Text) { MaxLength = 10 },
                new ColumnDefinition("Alpha",   DataType.Int32),
                new ColumnDefinition("Bravo",   DataType.Boolean),
            });
        }

        using var reopen = Database.Open(_path);
        var dt = reopen.GetTable("Ordered").ToDataTable();
        Assert.Equal(new[] { "Charlie", "Alpha", "Bravo" },
                     dt.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
    }

    [Fact]
    public void ToDataTable_ReadsASystemTable()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
            db.CreateTable("U", new[] { new ColumnDefinition("X", DataType.Int32) });

        using var reopen = Database.Open(_path);
        var dt = reopen.GetTable("MSysObjects").ToDataTable();
        Assert.Equal("MSysObjects", dt.TableName);
        Assert.Contains(dt.Rows.Cast<DataRow>(), row => "U".Equals(row["Name"]));
    }

    // ── Data-type round-trip coverage ────────────────────────────────────────

    [Fact]
    public void ToDataTable_MemoColumn_LandsAsStringWithLargeValue()
    {
        string big = new string('m', 1000);
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("Memos", new[]
            {
                new ColumnDefinition("Id",   DataType.Int32),
                new ColumnDefinition("Body", DataType.Memo),
            });
            t.Insert(new Row { ["Id"] = 1, ["Body"] = big });
        }

        using var reopen = Database.Open(_path);
        var dt = reopen.GetTable("Memos").ToDataTable();
        Assert.Equal(typeof(string), dt.Columns["Body"]!.DataType);
        Assert.Equal(big, dt.Rows[0]["Body"]);
    }

    [Fact]
    public void ToDataTable_BinaryColumn_LandsAsByteArray()
    {
        var blob = new byte[] { 1, 2, 3, 4, 5 };
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("Blobs", new[]
            {
                new ColumnDefinition("Id",  DataType.Int32),
                new ColumnDefinition("Raw", DataType.Binary) { MaxLength = 50 },
            });
            t.Insert(new Row { ["Id"] = 1, ["Raw"] = blob });
        }

        using var reopen = Database.Open(_path);
        var dt = reopen.GetTable("Blobs").ToDataTable();
        Assert.Equal(typeof(byte[]), dt.Columns["Raw"]!.DataType);
        var read = (byte[])dt.Rows[0]["Raw"];
        Assert.Equal(blob.Length, read.Length);
        for (int i = 0; i < blob.Length; i++) Assert.Equal(blob[i], read[i]);
    }

    // ── IEnumerable<T> coercion paths ────────────────────────────────────────

    private sealed class WidenedPoco
    {
        public long   BigId  { get; set; }   // column is Long (32-bit); widen to long
        public double Amount { get; set; }   // column is Money (decimal); widen to double
    }

    [Fact]
    public void ReadRows_WidensIntoLargerPropertyTypes()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("WidenedPoco", new[]
            {
                new ColumnDefinition("BigId",  DataType.Int32),
                new ColumnDefinition("Amount", DataType.Currency),
            });
            t.Insert(new Row { ["BigId"] = 42, ["Amount"] = 1234.56m });
        }

        using var reopen = Database.Open(_path);
        var saved = reopen.GetTable(nameof(WidenedPoco)).ReadRows<WidenedPoco>().Single();
        Assert.Equal(42L,        saved.BigId);
        Assert.Equal(1234.56,    saved.Amount, 4);   // tolerance for double<->decimal
    }

    private sealed class ReadOnlyPropertyPoco
    {
        public int    Id          { get; set; }
        public string Greeting    => "hi";          // get-only — exporter must skip
    }

    [Fact]
    public void ReadRows_ReadOnlyProperty_IsSkipped()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("ReadOnlyPropertyPoco", new[]
            {
                new ColumnDefinition("Id",       DataType.Int32),
                new ColumnDefinition("Greeting", DataType.Text) { MaxLength = 20 },
            });
            t.Insert(new Row { ["Id"] = 1, ["Greeting"] = "ignored" });
        }

        using var reopen = Database.Open(_path);
        var saved = reopen.GetTable(nameof(ReadOnlyPropertyPoco)).ReadRows<ReadOnlyPropertyPoco>().Single();
        Assert.Equal(1,    saved.Id);
        Assert.Equal("hi", saved.Greeting);   // computed; never set from DB
    }

    private sealed class GuidFromStringPoco
    {
        public int  Id     { get; set; }
        public Guid Marker { get; set; }
    }

    [Fact]
    public void ReadRows_GuidProperty_ParsedFromStringColumn()
    {
        // Synthetic mismatch: column is Text holding a GUID string, property is Guid.
        var g = new Guid("11111111-2222-3333-4444-555555555555");
        using (var db = Database.Create(_path, FileFormat.Access2000))
        {
            var t = db.CreateTable("GuidFromStringPoco", new[]
            {
                new ColumnDefinition("Id",     DataType.Int32),
                new ColumnDefinition("Marker", DataType.Text) { MaxLength = 40 },
            });
            t.Insert(new Row { ["Id"] = 1, ["Marker"] = g.ToString() });
        }

        using var reopen = Database.Open(_path);
        var saved = reopen.GetTable(nameof(GuidFromStringPoco)).ReadRows<GuidFromStringPoco>().Single();
        Assert.Equal(g, saved.Marker);
    }

    [Fact]
    public void ReadRows_NullTable_IsRefusedAtTheCall()
    {
        var refused = Assert.Throws<ArgumentNullException>(() => DatabaseExporter.ReadRows<CustomerPoco>(null!));
        Assert.Equal("table", refused.ParamName);
    }

    [Fact]
    public void ToDataTable_And_ToDataSet_NullArguments_AreRefusedByName()
    {
        Assert.Equal("table",    Assert.Throws<ArgumentNullException>(() => DatabaseExporter.ToDataTable(null!)).ParamName);
        Assert.Equal("database", Assert.Throws<ArgumentNullException>(() => DatabaseExporter.ToDataSet(null!)).ParamName);
    }
}
