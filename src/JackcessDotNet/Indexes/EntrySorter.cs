namespace JackcessDotNet;

/// <summary>
/// Sorts the entries of an index tree being built (<see cref="TableIndexes.BuildAll"/>) by key and then by row
/// pointer, holding at most <see cref="Budget"/> bytes of them in memory: past that, what it holds is sorted and
/// written to a temporary file as a run, and the runs are merged as the entries are read back in order. So building
/// the trees of a table of millions of rows with long text keys needs the same memory as building a small one. The
/// temporary files are deleted when the sorter is disposed, and by the system if the process ends first.
/// </summary>
internal sealed class EntrySorter : IDisposable
{
    /// <summary>The bytes of entries held in memory before they go to a run on disk: keys and what holding one costs.</summary>
    internal static long Budget { get; set; } = 64L * 1024 * 1024;

    /// <summary>The folder the runs go to: the system's temporary folder unless a test names another.</summary>
    internal static string? Folder { get; set; }

    // What one entry costs held, besides its key's bytes: the array's header and the list's slot.
    private const int EntryOverhead = 48;

    internal readonly record struct Item(byte[] Key, int RowPointer, bool HasNull);

    private readonly List<Item> _held = new();
    private readonly List<FileStream> _runs = new();
    private long _heldBytes;

    /// <summary>How many runs went to disk: for tests.</summary>
    internal int RunCount => _runs.Count;

    public void Add(byte[] key, int rowPointer, bool hasNull)
    {
        _held.Add(new Item(key, rowPointer, hasNull));
        _heldBytes += key.Length + EntryOverhead;
        if (_heldBytes > Budget) Spill();
    }

    internal static int Compare(Item a, Item b)
    {
        int byKey = a.Key.AsSpan().SequenceCompareTo(b.Key);
        return byKey != 0 ? byKey : ((uint)a.RowPointer).CompareTo((uint)b.RowPointer);
    }

    private void Spill()
    {
        _held.Sort(Compare);
        string folder = Folder ?? Path.GetTempPath();
        try
        {
            var run = new FileStream(Path.Combine(folder, "jackcess-index-" + Guid.NewGuid().ToString("N") + ".run"),
                                     FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose);
            _runs.Add(run);
            using (var writer = new BinaryWriter(run, System.Text.Encoding.UTF8, leaveOpen: true))
                foreach (var item in _held)
                {
                    writer.Write(item.Key.Length);
                    writer.Write(item.Key);
                    writer.Write(item.RowPointer);
                    writer.Write(item.HasNull);
                }
            run.Flush();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No row's failure: the load is taken back and this goes to the caller as it is.
            throw new IOException(
                $"The temporary folder '{folder}' could not be used to sort an index's keys while its tree was built: {e.Message}", e);
        }
        _held.Clear();
        _heldBytes = 0;
    }

    /// <summary>Every entry added, in key order and then row-pointer order. Read once.</summary>
    public IEnumerable<Item> Sorted()
    {
        if (_runs.Count == 0)
        {
            _held.Sort(Compare);
            foreach (var item in _held) yield return item;
            yield break;
        }

        if (_held.Count > 0) Spill();
        var readers = new List<BinaryReader>();
        foreach (var run in _runs)
        {
            run.Position = 0;
            readers.Add(new BinaryReader(run, System.Text.Encoding.UTF8, leaveOpen: true));
        }

        // A heap of each run's next entry: the least at the top.
        var heap = new List<(Item Item, int Run)>();
        for (int i = 0; i < readers.Count; i++)
            if (Next(readers[i]) is { } first) Push(heap, (first, i));
        while (heap.Count > 0)
        {
            var (item, run) = heap[0];
            yield return item;
            if (Next(readers[run]) is { } next) heap[0] = (next, run);
            else
            {
                heap[0] = heap[^1];
                heap.RemoveAt(heap.Count - 1);
            }
            SiftDown(heap, 0);
        }
    }

    private static Item? Next(BinaryReader reader)
    {
        if (reader.BaseStream.Position >= reader.BaseStream.Length) return null;
        int length = reader.ReadInt32();
        var key = reader.ReadBytes(length);
        int pointer = reader.ReadInt32();
        bool hasNull = reader.ReadBoolean();
        return new Item(key, pointer, hasNull);
    }

    private static void Push(List<(Item Item, int Run)> heap, (Item, int) entry)
    {
        heap.Add(entry);
        for (int i = heap.Count - 1; i > 0;)
        {
            int parent = (i - 1) / 2;
            if (Compare(heap[parent].Item, heap[i].Item) <= 0) break;
            (heap[parent], heap[i]) = (heap[i], heap[parent]);
            i = parent;
        }
    }

    private static void SiftDown(List<(Item Item, int Run)> heap, int i)
    {
        while (true)
        {
            int left = 2 * i + 1, right = left + 1, least = i;
            if (left < heap.Count && Compare(heap[left].Item, heap[least].Item) < 0) least = left;
            if (right < heap.Count && Compare(heap[right].Item, heap[least].Item) < 0) least = right;
            if (least == i) return;
            (heap[least], heap[i]) = (heap[i], heap[least]);
            i = least;
        }
    }

    public void Dispose()
    {
        foreach (var run in _runs) run.Dispose();
        _runs.Clear();
        _held.Clear();
    }
}
