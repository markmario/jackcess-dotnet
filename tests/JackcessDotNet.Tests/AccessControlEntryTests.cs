using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// The permissions a new table and a new relationship are given in MSysACEs (#37). Access gives a new object an
/// entry for each SID an inheritable entry of its container names, with that entry's permissions; where the
/// container's entry names the creator of whatever is made in it, the new object's entry is its owner's. Measured
/// in files DAO created and in this library's own, .mdb and .accdb. Without the owner's entry DAO, in an .mdb,
/// does not read a table this library made in a database DAO created, nor drop a relationship it made: that is
/// refereed in SchemaChangesInAccessTests.
/// </summary>
public sealed class AccessControlEntryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aces_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static string Hex(object? sid) => Convert.ToHexString((byte[])sid!);

    [Theory]
    [InlineData(FileFormat.Access2000)]
    [InlineData(FileFormat.Access2007)]
    [InlineData(FileFormat.Access2010)]
    public void A_new_table_and_a_new_relationship_have_the_entries_their_containers_pass_on_and_their_owner_s(FileFormat format)
    {
        using var db = Database.Create(_path, format);
        var parent = db.CreateTable("Parent", new[] { new ColumnDefinition("K", DataType.Int32) }, primaryKey: "K");
        db.CreateTable("Child", new[] { new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("K", DataType.Int32) }, primaryKey: "Id");
        db.CreateRelationship(new RelationshipDefinition("Parent", "K", "Child", "K") { Name = "ParentChild", HasReferentialIntegrity = true });

        var catalog = db.GetTable("MSysObjects").Rows.ToList();
        var aces    = db.GetTable("MSysACEs").Rows.ToList();
        byte[] system = (byte[])catalog.Single(r => Equals(r["Name"], "MSysObjects"))["Owner"]!;
        string creator = Convert.ToHexString(new[] { system[0], (byte)(system[1] ^ 0x07) });

        foreach (var (name, container) in new[] { ("Parent", "Tables"), ("ParentChild", "Relationships") })
        {
            var made = catalog.Single(r => Equals(r["Name"], name));
            string owner = Hex(made["Owner"]);
            int containerId = (int)catalog.Single(r => Equals(r["Name"], container) && Convert.ToInt32(r["Type"]) == 3)["Id"]!;

            // What the container passes on, by SID - the creator's entry being the owner's - each SID's permissions together.
            var expected = new Dictionary<string, int>();
            foreach (var ace in aces.Where(a => Equals(a["ObjectId"], containerId) && Equals(a["FInheritable"], true)))
            {
                string sid = Hex(ace["SID"]) == creator ? owner : Hex(ace["SID"]);
                expected[sid] = (expected.TryGetValue(sid, out int had) ? had : 0) | (int)ace["ACM"]!;
            }
            Assert.Contains(aces, a => Equals(a["ObjectId"], containerId) && Equals(a["FInheritable"], true) && Hex(a["SID"]) == creator);

            var actual = aces.Where(a => Equals(a["ObjectId"], made["Id"])).ToList();
            Assert.All(actual, a => Assert.Equal(false, a["FInheritable"]));
            Assert.Equal(expected.OrderBy(e => e.Key).Select(e => $"{e.Key}:{e.Value:X8}"),
                         actual.OrderBy(a => Hex(a["SID"])).Select(a => $"{Hex(a["SID"])}:{(int)a["ACM"]!:X8}"));
            // The owner has an entry of its own, which is what an .mdb asks for before it lets the object be changed.
            Assert.Contains(actual, a => Hex(a["SID"]) == owner);
        }
        IndexChecks.AssertEveryIndexHoldsTheRows(db, "MSysACEs");
        _ = parent.Name;
    }
}
