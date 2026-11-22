using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Index cursors against a model of what each gives: every move and lookup compared, with savepoints taken and
/// restored, while updates, inserts and deletes move rows into, out of and across the cursors' ranges. The model
/// holds, for each pass, the rows it has given, one by one: going one way a cursor gives a row once, and a row it
/// went past without coming to it - outside its range, left out by a date, looked past by a lookup - is given when
/// it comes to it. The cursors are over an integer key, with and without a range, and over a date read as Access
/// reads it followed by another column, with ranges whose ends are dates or dates and values, in or out; their
/// rows' dates are stored as the different numbers Access stores for one date.
/// </summary>
public sealed class CursorPassModelTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cursor_model_{Guid.NewGuid():N}.mdb");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Theory]
    [InlineData(1, false)] [InlineData(2, true)] [InlineData(3, false)] [InlineData(4, true)]
    [InlineData(5, false)] [InlineData(6, true)] [InlineData(7, false)] [InlineData(8, true)]
    [InlineData(9, false)] [InlineData(10, true)] [InlineData(11, false)] [InlineData(12, true)]
    public void A_cursor_gives_what_the_model_gives(int seed, bool ignoreNulls)
    {
        var log = new List<string>();
        try
        {
            Run(seed, ignoreNulls, steps: 1500, log);
        }
        catch (Exception e)
        {
            throw new Exception($"seed {seed}: {e.Message}\n" + string.Join("\n", log.Skip(Math.Max(0, log.Count - 40))), e);
        }
    }

    // ── The model ─────────────────────────────────────────────────────────────

    private static readonly DateTime[] Days =
    {
        new(2020, 3, 4, 10, 20, 30), new(2020, 3, 4, 10, 20, 31), new(2021, 1, 1),
        new(1899, 12, 30, 6, 0, 0), new(1899, 12, 29, 12, 0, 0), new(1899, 12, 29, 18, 0, 0),
    };

    private sealed class RowState
    {
        public int? K;
        public double? D;
        public int? L;
        public int Pointer;
    }

    // A place in an index's order: its columns' values as numbers, a null first, then the row pointer.
    private readonly struct Place : IComparable<Place>
    {
        public Place(double a, double b, int pointer) => (A, B, Pointer) = (a, b, pointer);

        public double A { get; }
        public double B { get; }
        public int Pointer { get; }

        public int CompareTo(Place other)
            => A != other.A ? A.CompareTo(other.A) : B != other.B ? B.CompareTo(other.B) : ((uint)Pointer).CompareTo((uint)other.Pointer);
    }

    private enum Where { Before, At, After }

    private sealed class Model
    {
        public Where Where = Where.Before;
        public Place Place;
        public bool? Forward;
        public HashSet<int> Given = new();

        public Model Copy() => new() { Where = Where, Place = Place, Forward = Forward, Given = new HashSet<int>(Given) };
    }

    // A cursor's index and range: by K, or by (D, L) with dates read as Access reads them.
    private sealed class Spec
    {
        public bool ByDL;
        public object?[]? Start;
        public object?[]? End;
        public bool StartIn = true;
        public bool EndIn = true;

        public IndexRange? Range => Start is null && End is null
            ? null
            : new IndexRange { StartKey = Start, EndKey = End, IsStartInclusive = StartIn, IsEndInclusive = EndIn };

        public override string ToString()
            => (ByDL ? "ByDL " : "ByK ") + (StartIn ? "[" : "(") + Show(Start) + " .. " + Show(End) + (EndIn ? "]" : ")");
    }

    private static string Show(object?[]? values)
        => values is null ? "-" : string.Join(",", values.Select(v => v is DateTime d ? d.ToString("yyyy-MM-dd HH:mm:ss") : v?.ToString() ?? "null"));

    // Where a row's entry is against an end of a range, or the values a closest row is looked for at: as the cursor
    // puts it, a date by what the stored number reads as, a null before every value.
    private static int Against(Spec spec, RowState row, object?[] values)
    {
        if (!spec.ByDL) return row.K is { } k ? Math.Sign(k.CompareTo((int)values[0]!)) : -1;
        if (row.D is not { } stored) return -1;
        var date = (DateTime)values[0]!;
        int byDate = ValueMatch.ReadsAs(stored) is { } read ? read.CompareTo(ValueMatch.AsStored(date)) : stored.CompareTo(date.ToOADate());
        if (byDate != 0 || values.Length == 1) return Math.Sign(byDate);
        return row.L is { } l ? Math.Sign(l.CompareTo((int)values[1]!)) : -1;
    }

    private static bool InRange(Spec spec, RowState row)
    {
        if (spec.Start is { } start && Against(spec, row, start) is var fromStart && (spec.StartIn ? fromStart < 0 : fromStart <= 0)) return false;
        if (spec.End is { } end && Against(spec, row, end) is var toEnd && (spec.EndIn ? toEnd > 0 : toEnd >= 0)) return false;
        return true;
    }

    private static Place PlaceOf(Spec spec, RowState row)
        => spec.ByDL
            ? new Place(row.D ?? double.NegativeInfinity, row.L ?? double.NegativeInfinity, row.Pointer)
            : new Place(row.K ?? double.NegativeInfinity, 0, row.Pointer);

    // ── A run ─────────────────────────────────────────────────────────────────

    private void Run(int seed, bool ignoreNulls, int steps, List<string> log)
    {
        var random = new Random(seed);
        if (File.Exists(_path)) File.Delete(_path);
        using var db = Database.Create(_path);
        var table = db.CreateTable("T", new[]
        {
            new ColumnDefinition("Id", DataType.Int32), new ColumnDefinition("K", DataType.Int32),
            new ColumnDefinition("D", DataType.DateTime), new ColumnDefinition("L", DataType.Int32),
        }, new[] { "Id" }, new[] { new IndexDefinition("ByK", "K") { IgnoreNulls = ignoreNulls }, new IndexDefinition("ByDL", "D", "L") });

        var rows = new Dictionary<int, RowState>();
        int nextId = 1;
        int? RandomK() => random.Next(8) == 0 ? null : random.Next(0, 30);
        int? RandomL() => random.Next(6) == 0 ? null : random.Next(0, 6);
        double? RandomD()
        {
            if (random.Next(8) == 0) return null;
            var day = Days[random.Next(Days.Length)];
            var (least, greatest) = ValueMatch.StoredRange(day);
            double number = random.Next(3) switch { 0 => least, 1 => day.ToOADate(), _ => greatest };
            // A time of 30 December 1899 is stored as a positive number or as its negative, both read as it.
            return day.Date == new DateTime(1899, 12, 30) && random.Next(2) == 0 ? -number : number;
        }
        void SetD(int id, double? number)
        {
            table.RewriteRowAt(rows[id].Pointer, new Row { ["D"] = number });
            rows[id].D = number;
        }
        void Insert()
        {
            int id = nextId++;
            var row = new Row { ["Id"] = id, ["K"] = RandomK(), ["D"] = null, ["L"] = RandomL() };
            table.Insert(row);
            var at = row.Id!.Value;
            rows[id] = new RowState { K = (int?)row["K"], L = (int?)row["L"], Pointer = RowPointer.Pack(at.PageNumber, at.RowNumber) };
            if (RandomD() is { } number) SetD(id, number);
        }
        for (int i = 0; i < 40; i++) Insert();

        object?[] DateEnd() => random.Next(2) == 0
            ? new object?[] { Days[random.Next(Days.Length)].AddSeconds(random.Next(3) - 1) }
            : new object?[] { Days[random.Next(Days.Length)].AddSeconds(random.Next(3) - 1), random.Next(0, 6) };
        var specs = new[]
        {
            new Spec(),
            new Spec
            {
                Start = random.Next(4) == 0 ? null : new object?[] { random.Next(3, 15) }, StartIn = random.Next(2) == 0,
                End = random.Next(4) == 0 ? null : new object?[] { random.Next(15, 27) }, EndIn = random.Next(2) == 0,
            },
            new Spec { ByDL = true, Start = DateEnd(), StartIn = random.Next(2) == 0, End = random.Next(3) == 0 ? null : DateEnd(), EndIn = random.Next(2) == 0 },
            new Spec { ByDL = true, Start = random.Next(2) == 0 ? null : DateEnd(), StartIn = random.Next(2) == 0, End = DateEnd(), EndIn = random.Next(2) == 0 },
        };
        var cursors = specs.Select(s => s.Range is { } range
            ? table.CreateIndexCursor(s.ByDL ? "ByDL" : "ByK", range)
            : table.CreateIndexCursor(s.ByDL ? "ByDL" : "ByK")).ToArray();
        var models  = specs.Select(_ => new Model()).ToArray();
        var saves   = new List<(int Cursor, CursorSavepoint Savepoint, Model Model)>();
        log.Add("cursors: " + string.Join("; ", specs.Select(s => s.ToString())));

        // The entries a cursor goes through, in its index's order: the rows the index holds, within its range.
        List<(Place Place, int Id, RowState Row)> Entries(Spec spec)
            => rows.Where(r => spec.ByDL || !(ignoreNulls && r.Value.K is null))
                   .Where(r => InRange(spec, r.Value))
                   .Select(r => (PlaceOf(spec, r.Value), r.Key, r.Value))
                   .OrderBy(e => e.Item1)
                   .ToList();

        int? ModelMove(int c, bool forward)
        {
            var m = models[c];
            if (m.Forward is { } going && going != forward) m.Given = m.Where == Where.At ? new HashSet<int> { m.Place.Pointer } : new HashSet<int>();
            m.Forward = forward;
            if (forward && m.Where == Where.After || !forward && m.Where == Where.Before) return null;
            var entries = Entries(specs[c]);
            if (!forward) entries.Reverse();
            foreach (var e in entries)
            {
                if (m.Where == Where.At && (forward ? e.Place.CompareTo(m.Place) <= 0 : e.Place.CompareTo(m.Place) >= 0)) continue;
                if (m.Given.Contains(e.Row.Pointer)) continue;
                (m.Where, m.Place) = (Where.At, e.Place);
                m.Given.Add(e.Row.Pointer);
                return e.Id;
            }
            m.Where = forward ? Where.After : Where.Before;
            return null;
        }

        // FindNextRow("K", v), through the index: on within the key's part, or a jump to it; nothing changes when
        // there is no further row with the key.
        int? ModelFindNext(int c, int v)
        {
            var m = models[c];
            var kept = m.Copy();
            if (m.Where == Where.After) return null;
            if (m.Where == Where.At && m.Place.A > v) return null;
            Place from;
            if (m.Where == Where.Before || m.Place.A < v)
            {
                if (m.Forward == false) m.Given = m.Where == Where.At ? new HashSet<int> { m.Place.Pointer } : new HashSet<int>();
                from = new Place(v, 0, 0);
            }
            else
            {
                if (m.Forward == false) m.Given = new HashSet<int> { m.Place.Pointer };
                from = m.Place;
            }
            m.Forward = true;
            foreach (var e in Entries(specs[c]).Where(e => e.Place.CompareTo(from) > 0))
            {
                if (m.Given.Contains(e.Row.Pointer)) continue;
                if (e.Row.K != v) break;
                (m.Where, m.Place) = (Where.At, e.Place);
                m.Given.Add(e.Row.Pointer);
                return e.Id;
            }
            models[c] = kept;
            return null;
        }

        // A lookup from the start: FindRowByKey, the first row with the values; FindClosestRowByKey, that or
        // the first after them. A pass begins with the row found.
        int? ModelLookup(int c, object?[] values, bool closest)
        {
            var entries = Entries(specs[c]);
            var found = entries.Where(e => Against(specs[c], e.Row, values) == 0).Select(e => ((Place, int, RowState)?)e).FirstOrDefault();
            if (found is null && closest)
                found = entries.Where(e => Against(specs[c], e.Row, values) > 0).Select(e => ((Place, int, RowState)?)e).FirstOrDefault();
            if (found is { } f)
            {
                models[c] = new Model { Where = Where.At, Place = f.Item1, Forward = true, Given = new HashSet<int> { f.Item3.Pointer } };
                return f.Item2;
            }
            if (closest) models[c] = new Model { Where = Where.After, Forward = true };
            return null;
        }

        int IdOf(Row row) => (int)row["Id"]!;
        void Check(string what, int? got, int? expected)
        {
            log.Add($"{what}: got {got?.ToString() ?? "-"} expected {expected?.ToString() ?? "-"}");
            Assert.True(got == expected, $"{what}: the cursor gave {got?.ToString() ?? "nothing"}, the model {expected?.ToString() ?? "nothing"}");
        }

        for (int step = 0; step < steps; step++)
        {
            int c = random.Next(cursors.Length);
            var cursor = cursors[c];
            var spec = specs[c];
            int op = random.Next(100);
            if (op < 38)
            {
                bool forward = random.Next(10) < 7;
                int? expected = ModelMove(c, forward);
                bool moved = forward ? cursor.MoveNext() : cursor.MovePrevious();
                Check($"c{c} {(forward ? "next" : "previous")}", moved ? IdOf(cursor.Current!) : null, expected);
            }
            else if (op < 62 && rows.Count > 0)
            {
                // An update: by the table, by another cursor on its row, or by the cursor on its own.
                int id = rows.Keys.ElementAt(random.Next(rows.Count));
                var other = cursors[(c + 1) % cursors.Length];
                int how = random.Next(4);
                if (how == 1 && other.Current is { } theirs && rows.ContainsKey(IdOf(theirs))) id = IdOf(theirs);
                else if (how == 2 && cursor.Current is { } own && rows.ContainsKey(IdOf(own))) id = IdOf(own);
                else how = 0;
                var by = how == 1 ? other : how == 2 ? cursor : null;
                switch (random.Next(3))
                {
                    case 0:
                    {
                        int? k = RandomK();
                        if (by is not null) by.UpdateCurrentRow(new Row { ["K"] = k });
                        else { var row = table.FindRowByPrimaryKey(id)!; row["K"] = k; table.Update(row); }
                        rows[id].K = k;
                        log.Add($"update {id} K={k?.ToString() ?? "null"}");
                        break;
                    }
                    case 1:
                    {
                        int? l = RandomL();
                        if (by is not null) by.UpdateCurrentRow(new Row { ["L"] = l });
                        else { var row = table.FindRowByPrimaryKey(id)!; row["L"] = l; table.Update(row); }
                        rows[id].L = l;
                        log.Add($"update {id} L={l?.ToString() ?? "null"}");
                        break;
                    }
                    default:
                    {
                        var number = RandomD();
                        SetD(id, number);
                        log.Add($"update {id} D={number?.ToString("R") ?? "null"}");
                        break;
                    }
                }
            }
            else if (op < 69)
            {
                Insert();
                var row = rows[nextId - 1];
                log.Add($"insert {nextId - 1} K={row.K?.ToString() ?? "null"} D={row.D?.ToString("R") ?? "null"} L={row.L?.ToString() ?? "null"}");
            }
            else if (op < 74 && rows.Count > 10)
            {
                int id = rows.Keys.ElementAt(random.Next(rows.Count));
                table.Delete(table.FindRowByPrimaryKey(id)!);
                int pointer = rows[id].Pointer;
                rows.Remove(id);
                foreach (var m in models.Concat(saves.Select(s => s.Model))) m.Given.Remove(pointer);
                log.Add($"delete {id}");
            }
            else if (op < 78)
            {
                if (random.Next(2) == 0) { cursor.MoveBeforeFirst(); models[c] = new Model(); }
                else { cursor.MoveAfterLast(); models[c] = new Model { Where = Where.After }; }
                log.Add($"c{c} reset");
            }
            else if (op < 84)
            {
                saves.Add((c, cursor.GetSavepoint(), models[c].Copy()));
                log.Add($"c{c} savepoint #{saves.Count - 1}");
            }
            else if (op < 90 && saves.Count > 0)
            {
                int s = random.Next(saves.Count);
                var (sc, savepoint, model) = saves[s];
                cursors[sc].RestoreSavepoint(savepoint);
                models[sc] = model.Copy();
                log.Add($"c{sc} restore #{s}");
            }
            else if (!spec.ByDL)
            {
                int v = random.Next(0, 30);
                switch (random.Next(3))
                {
                    case 0:
                    {
                        int? expected = ModelFindNext(c, v);
                        Check($"c{c} FindNextRow K={v}", cursor.FindNextRow("K", v) is { } row ? IdOf(row) : null, expected);
                        break;
                    }
                    case 1:
                    {
                        int? expected = ModelLookup(c, new object?[] { v }, closest: false);
                        Check($"c{c} FindRowByKey {v}", cursor.FindRowByKey(v) is { } row ? IdOf(row) : null, expected);
                        break;
                    }
                    default:
                    {
                        int? expected = ModelLookup(c, new object?[] { v }, closest: true);
                        Check($"c{c} FindClosestRowByKey {v}", cursor.FindClosestRowByKey(v) is { } row ? IdOf(row) : null, expected);
                        break;
                    }
                }
            }
            else
            {
                var values = DateEnd();
                int? expected = ModelLookup(c, values, closest: true);
                Check($"c{c} FindClosestRowByKey {Show(values)}", cursor.FindClosestRowByKey(values) is { } row ? IdOf(row) : null, expected);
            }
        }
    }
}
