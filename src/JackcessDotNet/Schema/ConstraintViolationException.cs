namespace JackcessDotNet;

/// <summary>
/// A change refused because the data would break a rule of the database: a duplicate in a primary key or another
/// unique index, a null where a key or a required index takes none, a row that would refer to no parent or leave
/// rows referring to none. The change is not made, and the file is as it was.
/// <para>
/// Its own type, so a caller writing data can tell a row the database will not take from a call made at the wrong
/// time, which is what any other <see cref="InvalidOperationException"/> from this library is.
/// </para>
/// </summary>
public sealed class ConstraintViolationException : InvalidOperationException
{
    public ConstraintViolationException(string message) : base(message) { }

    public ConstraintViolationException(string message, Exception? innerException) : base(message, innerException) { }
}
