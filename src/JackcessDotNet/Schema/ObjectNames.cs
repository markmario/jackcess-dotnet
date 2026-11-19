namespace JackcessDotNet;

/// <summary>
/// Whether two names of a database's objects of one kind - its tables, a table's columns or indexes, its
/// relationships - are one name: when they are equal without regard to case as .NET compares them
/// (<see cref="StringComparison.OrdinalIgnoreCase"/>), the comparison this library looks names up with - a row's
/// column, a table by its name. A new name equal to one its kind has, or to another new one, is refused, so that no
/// two objects are taken for each other. The comparison throws for no string: a name already in a file - one with a
/// noncharacter in it, say - stands in the way of nothing but a new name equal to it.
/// <para>
/// This is not yet Access's own rule. Measured with DAO, Access also takes as one names that differ in width, in kana,
/// or in an accent written as one character or as two, which this library keeps apart. And it keeps apart names .NET
/// takes as one without regard to case - the micro sign and the Greek mu, and letters of other scripts that .NET's
/// newer case tables pair - which this library refuses on .NET; .NET Framework does not pair them, and keeps them
/// apart, as Access does.
/// </para>
/// </summary>
internal static class ObjectNames
{
    /// <summary>Whether <paramref name="first"/> and <paramref name="second"/> are one name to this library.</summary>
    internal static bool Same(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="taken"/>, a name an object has, quoted for a message that refuses <paramref name="given"/>
    /// for it: with what makes the two one, when they are not the same text.
    /// </summary>
    internal static string Taken(string taken, string given)
        => taken == given ? $"'{taken}'" : $"'{taken}', which is '{given}' without regard to case";
}
