using System.IO;
using System.Security.Authentication;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// What a caller can tell apart by the type of a refusal: data that breaks a rule of the database
/// (<see cref="ConstraintViolationException"/>) from a call the database cannot do as it stands, a table that is
/// not there from one that is (<see cref="Database.TryGetTable"/>), and a wrong password from a file the system
/// will not open.
/// </summary>
public sealed class RefusalTypeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"refusal_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private Table People(Database db)
        => db.CreateTable("People", new[]
        {
            new ColumnBuilder("Id",   DataType.Long).Build(),
            new ColumnBuilder("Name", DataType.Text).MaxLength(20).Build(),
        }, primaryKey: "Id");

    [Fact]
    public void A_row_the_database_will_not_take_is_a_constraint_violation_and_still_an_invalid_operation()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var people = People(db);
        people.Insert(new Row { ["Id"] = 1, ["Name"] = "Ada" });

        // A duplicate in the primary key, and a null in it.
        Assert.Throws<ConstraintViolationException>(() => people.Insert(new Row { ["Id"] = 1, ["Name"] = "Bea" }));
        Assert.Throws<ConstraintViolationException>(() => people.Validate(new Row { ["Name"] = "Cy" }));
        Assert.Throws<ConstraintViolationException>(() => people.UpdateByPrimaryKey(1, new Row { ["Id"] = null }));

        // A handler written for the type these were before still catches one.
        bool caught = false;
        try { people.Insert(new Row { ["Id"] = 1, ["Name"] = "Bea" }); }
        catch (InvalidOperationException) { caught = true; }
        Assert.True(caught);

        Assert.Equal("Ada", Assert.Single(people.Rows)["Name"]);
    }

    [Fact]
    public void A_call_the_database_cannot_do_is_not_a_constraint_violation()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var people = People(db);
        people.Insert(new Row { ["Id"] = 1, ["Name"] = "Ada" });

        // A table that is not there is the caller's argument.
        Assert.Throws<ArgumentException>(() => db.GetTable("Nobody"));
        Assert.False(db.TryGetTable("Nobody", out _));

        // Assert.Throws takes the exact type: none of these is the derived one.
        Assert.Throws<InvalidOperationException>(() => db.CreateTable("PEOPLE", new[] { new ColumnBuilder("Id", DataType.Long).Build() }));
        Assert.Throws<InvalidOperationException>(() => people.UpdateByPrimaryKey(2, new Row { ["Name"] = "Bea" }));
        Assert.Throws<InvalidOperationException>(() => people.DeleteRow("Id", 2));
    }

    [Fact]
    public void A_row_that_breaks_a_relationship_is_a_constraint_violation()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var people = People(db);
        var pets = db.CreateTable("Pets", new[]
        {
            new ColumnBuilder("Id",    DataType.Long).Build(),
            new ColumnBuilder("Owner", DataType.Long).Build(),
        }, primaryKey: "Id");
        people.Insert(new Row { ["Id"] = 1, ["Name"] = "Ada" });
        pets.Insert(new Row { ["Id"] = 10, ["Owner"] = 7 });

        // Rows already there that refer to no parent.
        var definition = new RelationshipDefinition("People", "Id", "Pets", "Owner") { HasReferentialIntegrity = true };
        Assert.Throws<ConstraintViolationException>(() => db.CreateRelationship(definition));

        pets.UpdateByPrimaryKey(10, new Row { ["Owner"] = 1 });
        db.CreateRelationship(definition);
        db.EnforceForeignKeys = true;

        // A row that would refer to no parent, and a parent that would leave a row referring to none.
        Assert.Throws<ConstraintViolationException>(() => db.GetTable("Pets").Insert(new Row { ["Id"] = 11, ["Owner"] = 7 }));
        Assert.Throws<ConstraintViolationException>(() => db.GetTable("People").DeleteRow("Id", 1));
    }

    [Fact]
    public void An_index_the_rows_do_not_allow_is_a_constraint_violation()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        var people = People(db);
        people.Insert(new Row { ["Id"] = 1, ["Name"] = "Ada" });
        people.Insert(new Row { ["Id"] = 2, ["Name"] = "Ada" });
        people.Insert(new Row { ["Id"] = 3 });

        Assert.Throws<ConstraintViolationException>(() => people.AddIndex(new IndexDefinition("UniqueName", "Name") { IsUnique = true }));
        Assert.Throws<ConstraintViolationException>(() => people.AddIndex(new IndexDefinition("RequiredName", "Name") { IsRequired = true }));
        // The index is at fault here, not the rows.
        Assert.Throws<InvalidOperationException>(() => people.AddIndex(new IndexDefinition("ByNothing", "Nowhere")));
    }

    [Fact]
    public void A_table_is_asked_for_without_an_exception_for_one_that_is_not_there()
    {
        using var db = Database.Create(_path, FileFormat.Access2000);
        People(db).Insert(new Row { ["Id"] = 1, ["Name"] = "Ada" });

        Assert.True(db.TryGetTable("people", out var found));   // by name, without regard to case, as GetTable finds it
        Assert.Equal("Ada", Assert.Single(found!.Rows.ToList())["Name"]);

        Assert.False(db.TryGetTable("Nobody", out var none));
        Assert.Null(none);

        // A name that is no name is the caller's mistake, as it is to GetTable.
        Assert.Throws<ArgumentException>(() => db.TryGetTable(" ", out _));
        Assert.Throws<ArgumentException>(() => db.GetTable(" "));
    }

    [Fact]
    public void A_file_the_password_does_not_open_is_refused_as_a_wrong_credential()
    {
        using (var db = Database.Create(_path, FileFormat.Access2000))
            People(db);

        // The catalog's page no longer reads as a table definition, which is how a file encrypted with another
        // password looks to one that has the wrong one.
        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 2 * 4096;
            stream.Write(new byte[] { 0xA7, 0x5C, 0x13, 0xE9 }, 0, 4);
        }

        Assert.Throws<InvalidCredentialException>(() => Database.Open(_path, new DatabaseOpenOptions { Password = "not the password" }));
        // Nothing is left holding the file.
        File.Delete(_path);
    }
}
