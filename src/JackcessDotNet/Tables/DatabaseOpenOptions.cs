namespace JackcessDotNet;

/// <summary>
/// What has to be known about a file to open it: pass one to
/// <see cref="Database.Open(string, DatabaseOpenOptions)"/>. What can change while a database is open - whether
/// foreign keys are enforced, whether a row may bring its own AutoNumber - is a property of the
/// <see cref="Database"/> instead.
/// <code>
///   using var db = Database.Open("Secret.accdb", new DatabaseOpenOptions { Password = "pa55word" });
/// </code>
/// </summary>
public sealed class DatabaseOpenOptions
{
    /// <summary>
    /// Creates options with nothing set: no password, no format to fall back on, and the file opened for reading and
    /// writing.
    /// </summary>
    public DatabaseOpenOptions()
    {
    }

    /// <summary>
    /// The password of a protected file: the database password of an .mdb, or the password an .accdb is encrypted
    /// with. Null or empty (the default) for none. It is compared as Access compares it, exactly, letter case and
    /// all. A file that has no password - an .mdb whose pages are encoded among them - is opened whatever the
    /// password, which is not looked at.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// The format to read the file as when its own header does not say: a file without the signature of an
    /// Access database. A file whose header names its format is opened in that format whatever is set here, so
    /// an Access 97 file is read as one, and is read-only. Null (the default) refuses a file with no such header
    /// with <see cref="System.IO.InvalidDataException"/>.
    /// </summary>
    public FileFormat? FallbackFormat { get; set; }

    /// <summary>
    /// Whether the file is opened only to be read. Default <c>false</c>: it is opened for reading and writing, and
    /// while the database is open no other program or <see cref="Database"/> can open the file for writing.
    /// <para>
    /// <c>true</c> opens it as Access opens a database read-only: nothing is ever written to the file - its handle
    /// cannot write - and every change asked of the database, its tables and their cursors is refused with
    /// <see cref="NotSupportedException"/> before anything happens (<see cref="Database.IsReadOnly"/>). A file marked
    /// read-only opens this way. The file opens while Access, or another program or <see cref="Database"/>, has it
    /// open for writing; only one that holds it exclusively keeps it out (<see cref="DatabaseInUseException"/>).
    /// </para>
    /// <para>
    /// What such a database reads is the file as it is when each page is read: rows another program has written by
    /// then are read - Access writes a change when it commits it - and a table's columns and indexes are as they were
    /// when the table was first got from the database. This library takes none of Access's locks, so nothing keeps it
    /// from reading a table part-way through another program's change of several pages: a reading made while the
    /// file is being written to can miss rows, or read one that has since gone, and can fail. For a reading that
    /// agrees with itself, open the file when no other program has it open for writing.
    /// </para>
    /// </summary>
    public bool ReadOnly { get; set; }
}
