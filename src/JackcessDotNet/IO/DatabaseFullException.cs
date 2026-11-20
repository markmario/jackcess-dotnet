namespace JackcessDotNet;

/// <summary>
/// A change that would make the database's file longer than the 2 GB (2,147,483,648 bytes) an Access file can be.
/// Access reads a longer file this library wrote, and cannot write to it: ACE refuses an insert into one, "Cannot open
/// database ''. It may not be a database that your application recognizes, or the file may be corrupt." (measured,
/// ACE 16, on a file of 2,160,779,264 bytes). So the page that would pass the limit is not written, and the change it
/// was part of is taken back, as any change that fails part-way is: an insert, a batch, a schema change, the building
/// of an index.
/// </summary>
public sealed class DatabaseFullException : IOException
{
    internal DatabaseFullException(string path, long maxLength)
        : base($"'{path}' cannot grow past {maxLength:N0} bytes, the most an Access file can be; the change that would " +
               "have made it longer was not made.")
    {
        MaxLength = maxLength;
    }

    /// <summary>The longest the file can be, in bytes: 2 GB.</summary>
    public long MaxLength { get; }
}
