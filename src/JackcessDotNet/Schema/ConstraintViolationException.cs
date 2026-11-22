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
    /// <summary>Creates the exception with a message that says which rule the data would break.</summary>
    /// <param name="message">What was refused, and why.</param>
    internal ConstraintViolationException(string message) : base(message) { }

    /// <summary>
    /// Creates the exception with a message that says which rule the data would break, and the exception that
    /// showed it.
    /// </summary>
    /// <param name="message">What was refused, and why.</param>
    /// <param name="innerException">The exception this one is raised for, or null when there is none.</param>
    internal ConstraintViolationException(string message, Exception? innerException) : base(message, innerException) { }
}
