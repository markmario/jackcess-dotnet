namespace JackcessDotNet;

/// <summary>
/// The rows of a table that one of its indexes was seen not to hold (<see cref="TableIndexes.HoldsEveryRow"/>,
/// under <see cref="Database.VerifyIndexes"/>),
/// each with the key the index should have it under: none, for an index Access alone has written. They are what
/// the index cannot answer for - a unique index says nothing of a duplicate of one of them
/// (qcatco/exportmdb.interop#73) - and they are kept while the database is open. A row leaves when this library
/// deletes or rewrites it, since a row it rewrites gains its entries; none is ever added, since a row it inserts
/// has them.
/// </summary>
internal sealed class RowsNotHeld
{
    /// <summary>No rows: what an index that is taken at its word lacks (<see cref="Database.VerifyIndexes"/> off).</summary>
    internal static readonly RowsNotHeld None = new(new Dictionary<int, byte[]>());

    private readonly Dictionary<int, byte[]> _keys;
    // How many of the rows have each key: what a duplicate is looked for by.
    private readonly Dictionary<byte[], int> _rowsWith = new(ForeignKeyEnforcer.KeyComparer.Instance);

    /// <param name="keys">The key each row should be under, by the row's pointer.</param>
    internal RowsNotHeld(Dictionary<int, byte[]> keys)
    {
        _keys = keys;
        foreach (byte[] key in keys.Values)
            _rowsWith[key] = _rowsWith.TryGetValue(key, out int rows) ? rows + 1 : 1;
    }

    internal int Count => _keys.Count;

    /// <summary>Whether one of the rows, other than the one at <paramref name="rowPointer"/>, has <paramref name="key"/>.</summary>
    internal bool AnotherHas(byte[] key, int? rowPointer)
    {
        if (!_rowsWith.TryGetValue(key, out int rows)) return false;
        if (rowPointer is { } own && _keys.TryGetValue(own, out byte[]? ownKey) && ownKey.AsSpan().SequenceEqual(key)) rows--;
        return rows > 0;
    }

    /// <summary>
    /// The row at <paramref name="rowPointer"/> has been deleted or rewritten: it is no longer a row the index lacks.
    /// Returns the key it had, to be put back if the change is taken back (<see cref="PutBack"/>); null when the
    /// row was not one of them.
    /// </summary>
    internal byte[]? Forget(int rowPointer)
    {
        if (!_keys.TryGetValue(rowPointer, out byte[]? key)) return null;
        _keys.Remove(rowPointer);
        if (--_rowsWith[key] == 0) _rowsWith.Remove(key);
        return key;
    }

    /// <summary>The row at <paramref name="rowPointer"/>, forgotten by a change that was taken back, is one the index lacks again.</summary>
    internal void PutBack(int rowPointer, byte[] key)
    {
        _keys[rowPointer] = key;
        _rowsWith[key] = _rowsWith.TryGetValue(key, out int rows) ? rows + 1 : 1;
    }
}
