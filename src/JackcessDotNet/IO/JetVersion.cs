namespace JackcessDotNet;

/// <summary>
/// Identifies a Jet/ACE format generation, used to select the right
/// <see cref="JetFormat"/> at file-open time.
/// </summary>
public enum JetVersion
{
    /// <summary>Jet 3 — Access 97 (.mdb, page size 2048). Read only: such a file is neither created nor written.</summary>
    Jet3,
    /// <summary>Jet 4 — Access 2000/2002/2003 (.mdb, page size 4096).</summary>
    Jet4,
    /// <summary>ACE 12 — Access 2007 (.accdb, page size 4096).</summary>
    Jet12,
    /// <summary>ACE 14 — Access 2010 (.accdb), which Access 2013 and later also write until a file needs more.</summary>
    Jet14,
    /// <summary>ACE 16 — Access 2016 (.accdb): what a file becomes when a table of it has a Large Number column.</summary>
    Jet16,
    /// <summary>ACE 17 — Access 2019 and later (.accdb): what a file becomes when a table of it has a Date/Time Extended column.</summary>
    Jet17,
}
