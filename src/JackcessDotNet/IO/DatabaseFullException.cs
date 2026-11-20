namespace JackcessDotNet;

/// <summary>
/// A change that would make the database's file longer than the 2 GB (2,147,483,648 bytes) an Access file can be, or
/// a change to a file that is longer already. Access reads a longer file this library wrote, and cannot write to it:
/// ACE refuses an insert into one, "Cannot open database ''. It may not be a database that your application
/// recognizes, or the file may be corrupt." (measured, ACE 16, on a file of 2,160,779,264 bytes). So the page that
/// would pass the limit is not written, and the change it was part of is taken back, as any change that fails part-way
/// is: an insert, a batch, a long value, a schema change, the building of an index. A file already longer - one a
/// version of this library before the limit wrote - is read whole, and every change to it is refused so, with the file
/// left as it is.
/// </summary>
public sealed class DatabaseFullException : IOException
{
    internal DatabaseFullException(string path, long maxLength, long length)
        : base(length > maxLength
            ? $"'{path}' is {length:N0} bytes, longer than the {maxLength:N0} an Access file can be: it is read, and the change " +
              "was not made."
            : $"'{path}' cannot grow past {maxLength:N0} bytes, the most an Access file can be; the change that would " +
              "have made it longer was not made.")
    {
        MaxLength = maxLength;
    }

    /// <summary>The longest the file can be, in bytes: 2 GB.</summary>
    public long MaxLength { get; }
}
