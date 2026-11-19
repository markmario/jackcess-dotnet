using System.Globalization;
using System.Text;

namespace JackcessDotNet;

/// <summary>
/// A Date/Time Extended value as a row stores it: its 42 bytes (<see cref="ExtendedDateTimeCodec"/>), in whichever
/// form they are. Access keys such a value by these bytes and not by the date they say (#51): its <c>INSERT</c>
/// writes a date in one form, its SQL <c>UPDATE</c> in another and DAO's table-type recordset in a third, with bytes
/// after it that are no part of the value (#64), and it holds them under keys of their own - a unique index takes a
/// row in each, and a relationship's parent is the row with the child's very bytes. So a row's index
/// keys are made of these bytes (<see cref="RowDecoder.DecodeForKey"/>), keys are compared by them, and a row
/// rewritten without a new value for the column keeps them. A caller's <see cref="DateTime"/> is the form Access's
/// <c>INSERT</c> writes (<see cref="Of"/>), which is what this library writes too.
/// </summary>
internal sealed class StoredExtendedDate : IEquatable<StoredExtendedDate>
{
    private readonly byte[] _bytes;

    /// <param name="bytes">The 42 bytes a row holds, which are not changed afterwards.</param>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not 42 bytes long.</exception>
    internal StoredExtendedDate(byte[] bytes)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        if (bytes.Length != ExtendedDateTimeCodec.Size)
            throw new ArgumentException($"A Date/Time Extended value is {ExtendedDateTimeCodec.Size} bytes; {bytes.Length} were given.", nameof(bytes));
        _bytes = bytes;
    }

    /// <summary><paramref name="value"/> as Access's <c>INSERT</c> writes it, and this library.</summary>
    internal static StoredExtendedDate Of(DateTime value) => new(ExtendedDateTimeCodec.Encode(value));

    /// <summary>The 42 bytes, in the row's order.</summary>
    internal byte[] Bytes => _bytes;

    /// <summary>
    /// The date the bytes say, as a row is read with it (<see cref="ExtendedDateTimeCodec.Decode"/>); null for bytes
    /// that say none.
    /// </summary>
    internal DateTime? Date => ExtendedDateTimeCodec.Decode(_bytes, 0);

    /// <summary>
    /// Whether the bytes are the form Access's <c>INSERT</c>, and this library, write the date they say in - the
    /// form whose key is the key of that date as a caller gives it.
    /// </summary>
    internal bool IsFirstForm => Date is { } date && _bytes.AsSpan().SequenceEqual(ExtendedDateTimeCodec.Encode(date));

    /// <summary>
    /// The date the bytes say as versions of this library that keyed the date they read read it, the key of which,
    /// in the first form, is the key such a version made for the row (#51): from 2930d3f a count of digits other
    /// than 7 - Access's second form among them - was read as no value, and from db6f93b, with
    /// <paramref name="secondFormAsDate"/>, a count of 0 as Access's second form
    /// (<see cref="ExtendedDateTimeCodec.DecodeAsEarlierVersions"/>). Null for no value: so both read the layout DAO's
    /// table-type recordset writes (#64).
    /// </summary>
    internal DateTime? DateAsEarlierVersionsRead(bool secondFormAsDate)
        => secondFormAsDate || _bytes[ExtendedDateTimeCodec.CountAt] == (byte)'7' ? ExtendedDateTimeCodec.DecodeAsEarlierVersions(_bytes, 0) : null;

    /// <summary>
    /// <paramref name="values"/> - a row's, for an index's columns - as the versions of this library that keyed the
    /// date they read read them (<see cref="DateAsEarlierVersionsRead"/>): each value in another form than the first
    /// as its date, as from db6f93b, and then, with <paramref name="asFrom2930d3f"/>, as from 2930d3f, which read
    /// Access's second form as no value. None when every value is in the first form, which those versions keyed as
    /// this library does. Such a version entered a row under the key of one of them.
    /// </summary>
    internal static IEnumerable<object?[]> AsEarlierVersionsRead(IReadOnlyList<object?> values, bool asFrom2930d3f)
    {
        if (!values.Any(value => value is StoredExtendedDate stored && !stored.IsFirstForm)) yield break;
        foreach (bool secondFormAsDate in asFrom2930d3f ? new[] { true, false } : new[] { true })
            yield return values
                .Select(value => value is StoredExtendedDate stored && !stored.IsFirstForm ? stored.DateAsEarlierVersionsRead(secondFormAsDate) : value)
                .ToArray();
    }

    /// <inheritdoc/>
    public bool Equals(StoredExtendedDate? other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is StoredExtendedDate other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        unchecked
        {
            uint hash = 2166136261;   // FNV-1a
            foreach (byte b in _bytes) hash = (hash ^ b) * 16777619;
            return (int)hash;
        }
    }

    /// <summary>The date the bytes say, as a refusal names a value; the bytes themselves when they say none.</summary>
    public override string ToString()
        => Date is { } date
            ? date.ToString(CultureInfo.InvariantCulture)
            : Encoding.ASCII.GetString(_bytes).Replace("\0", "\\0");
}
