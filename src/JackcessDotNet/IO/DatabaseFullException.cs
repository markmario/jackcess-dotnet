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
/// <para>
/// A database opened with a lower limit (<see cref="DatabaseOpenOptions.MaxFileLength"/>) is held to that limit the
/// same way.
/// </para>
/// </summary>
public sealed class DatabaseFullException : IOException
{
    internal DatabaseFullException(string path, long maxLength, long length)
        : base(Describe(path, maxLength, length))
    {
        MaxLength = maxLength;
    }

    private static string Describe(string path, long maxLength, long length)
    {
        string limit = maxLength >= PageFile.FormatMaxLength
            ? "an Access file can be"
            : $"the database was opened to grow to ({nameof(DatabaseOpenOptions)}.{nameof(DatabaseOpenOptions.MaxFileLength)})";
        return length > maxLength
            ? $"'{path}' is {length:N0} bytes, longer than the {maxLength:N0} {limit}: it is read, and the change was not made."
            : $"'{path}' cannot grow past {maxLength:N0} bytes, the most {limit}; the change that would have made it longer " +
              "was not made.";
    }

    /// <summary>
    /// The longest the file can be, in bytes: 2 GB, or the lower limit the database was opened with
    /// (<see cref="DatabaseOpenOptions.MaxFileLength"/>).
    /// </summary>
    public long MaxLength { get; }
}
