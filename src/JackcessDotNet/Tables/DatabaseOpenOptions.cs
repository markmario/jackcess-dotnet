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
    /// What such a database reads is the file as it is at each move of a cursor, and at each row of
    /// <see cref="Table.Rows"/>: the pages a move needs are read from the file again - a page or more at every row -
    /// and nothing read at one move is taken for what the file holds at the next, as another program may have written
    /// to it in between. An Access 97 file, which is only ever read, is read so whether or not this is set. (With
    /// <see cref="KeepWritersOut"/>, on Windows, no other program can write to it, and pages are kept as over a
    /// database opened to be written.) A table's columns and indexes are as they were when the table was first got from the database.
    /// Access writes a change it has committed once it has been idle for half a second, and can keep the last until it
    /// next changes something or closes the database, so the last rows Access committed may not be read while Access
    /// has the file open; once it has closed it, every one is.
    /// </para>
    /// <para>
    /// This library takes none of Access's locks, so nothing keeps a reading from meeting another program's change
    /// part-way, or from seeing at one move what that program changed after the last. A reading made while the file is
    /// being written to can miss rows - ones the writer adds or moves behind a cursor, and ones it has not yet written
    /// to the file - and can give a row twice: one the writer moves ahead of a cursor after the cursor has given it, or
    /// one on a page read while the writer was part-way through writing it. It can give a Memo or OLE value that is not
    /// the row's own - another row's, or part of one - where the writer gives the place of the row's value to another
    /// between the reading of the row and the reading of its value. And it can fail: a value that is not where its row
    /// says - its place deleted, or holding fewer bytes than the value has - is refused with
    /// <see cref="System.IO.InvalidDataException"/>, and the reading can be made again. What a reading cannot do is
    /// change the file: its handle cannot write. Nor does it give a row as an earlier move read it, or a value read
    /// from a place that has been deleted or is too short for it. A writer - Access with the database open shared, or
    /// a <see cref="Database"/> - neither waits for it nor is kept out by it; what needs the file to itself is: Access
    /// opening the database exclusively, and so Access's Compact and Repair, and <see cref="Database.Create(string)"/>
    /// replacing the file (<see cref="DatabaseInUseException"/>). A reading that agrees with itself - every row once,
    /// each with its own values - needs no writer while it is made: read the file when no other program has it open
    /// for writing. A database opened to be written keeps every writer out while it is open, and so, on Windows, does
    /// one opened only to be read with <see cref="KeepWritersOut"/>.
    /// </para>
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Whether a file opened only to be read (<see cref="ReadOnly"/>) keeps every writer out while it is open. Default
    /// <c>false</c>: a file opened only to be read is shared for reading and writing, and opens beside a writer. Set
    /// without <see cref="ReadOnly"/>, <see cref="Database.Open(string, DatabaseOpenOptions)"/> throws
    /// <see cref="ArgumentException"/> before the file is opened, whatever its format.
    /// <para>
    /// <c>true</c> shares the file for reading only, as Access holds a database it opens exclusively and read-only
    /// (DAO's <c>OpenDatabase</c> with both set, ACE's <c>Mode=Share Deny Write</c>). On Windows, while the database is
    /// open no other program or <see cref="Database"/> opens the file for writing - Access opening it shared, or
    /// exclusively, is told the file is "already in use", and ACE's default mode opens it read-only instead - and
    /// readers - Access read-only, a <see cref="Database"/> opened only to be read - open it beside this one. So every
    /// reading is of one state of the file, as it was when it was opened, and a cursor keeps the page it is on, and an
    /// index cursor its leaf, from one move to the next, as over a database opened to be written, rather than reading
    /// them again at every move; <see cref="Table.Rows"/> reads each page once.
    /// </para>
    /// <para>
    /// Elsewhere .NET keeps to a file's sharing only by advisory locks, which let another program, or another
    /// <see cref="Database"/>, open the file to write beside this one. There the file is still opened sharing reading
    /// only, but a writer is not kept out, and the database reads as one opened with <see cref="ReadOnly"/> alone does
    /// - its pages again at every move - so that a reading beside a writer gives what <see cref="ReadOnly"/> says it
    /// can.
    /// </para>
    /// <para>
    /// A file another program or <see cref="Database"/> has open for writing - Access, while the database is open in
    /// it - is not opened: <see cref="DatabaseInUseException"/>, and nothing of the file is changed. To read it beside
    /// the writer instead, open it again without this, as <see cref="ReadOnly"/> describes:
    /// <code>
    ///   Database db;
    ///   try { db = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true, KeepWritersOut = true }); }
    ///   catch (DatabaseInUseException) { db = Database.Open(path, new DatabaseOpenOptions { ReadOnly = true }); }
    /// </code>
    /// </para>
    /// <para>
    /// An Access 97 file, which is only ever read, keeps writers out when it is opened with <see cref="ReadOnly"/> and
    /// this; opened without either, it is shared with writers, as one opened with <see cref="ReadOnly"/> alone is.
    /// </para>
    /// </summary>
    public bool KeepWritersOut { get; set; }
}
