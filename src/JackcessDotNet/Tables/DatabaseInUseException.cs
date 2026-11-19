using System.IO;

namespace JackcessDotNet;

/// <summary>
/// A database file refused because another program, or another <see cref="Database"/>, has it open in a way that
/// does not let this one in: open for writing, when this one would write too, or open exclusively. Nothing of the
/// file is read or changed.
/// <para>
/// Its own type, so a caller can tell a file that is in use - which may be free later - from any other
/// <see cref="IOException"/>, such as a path that does not exist. Its <see cref="Exception.HResult"/> is that of
/// a sharing violation, 0x80070020, as the <see cref="IOException"/> Windows gives for a file in use has, so code
/// that looks for that finds it here too.
/// </para>
/// </summary>
public sealed class DatabaseInUseException : IOException
{
    // ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION as HRESULTs.
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation    = unchecked((int)0x80070021);

    /// <summary>Creates the exception with a message that says which file is in use, and what was not done.</summary>
    /// <param name="message">What was refused, and why.</param>
    public DatabaseInUseException(string message) : base(message) => HResult = SharingViolation;

    /// <summary>
    /// Creates the exception with a message that says which file is in use, and what was not done, and the exception
    /// that showed it.
    /// </summary>
    /// <param name="message">What was refused, and why.</param>
    /// <param name="innerException">The exception this one is raised for, or null when there is none.</param>
    public DatabaseInUseException(string message, Exception? innerException) : base(message, innerException)
        => HResult = SharingViolation;

    /// <summary>
    /// Whether <paramref name="e"/> is Windows refusing to open a file because of how another handle has it open: a
    /// sharing violation (ERROR_SHARING_VIOLATION) or a lock violation (ERROR_LOCK_VIOLATION).
    /// </summary>
    internal static bool IsSharingViolation(IOException e) => e.HResult is SharingViolation or LockViolation;
}
