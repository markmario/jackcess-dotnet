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
    /// <summary>Creates options with nothing set: no password, and no format to fall back on.</summary>
    public DatabaseOpenOptions()
    {
    }

    /// <summary>
    /// The password of an encrypted file. Null or empty (the default) for none. A file that is not encrypted is
    /// opened whatever the password, which is not looked at.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// The format to read the file as when its own header does not say: a file without the signature of an
    /// Access database. A file whose header names its format is opened in that format whatever is set here, so
    /// an Access 97 file is read as one, and is read-only. Null (the default) refuses a file with no such header
    /// with <see cref="System.IO.InvalidDataException"/>.
    /// </summary>
    public FileFormat? FallbackFormat { get; set; }
}
