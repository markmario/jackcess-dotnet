namespace JackcessDotNet;

/// <summary>
/// The format of an Access database file, named for the version of Access that introduced it: what
/// <see cref="Database.Create(string, FileFormat)"/> makes and <see cref="Database.FileFormat"/> reports. Jackcess
/// calls it <c>Database.FileFormat</c> (<c>V1997</c>, <c>V2000</c>...).
/// </summary>
public enum FileFormat
{
    /// <summary>Access 97 (.mdb, Jet 3, page size 2048; Jackcess's <c>V1997</c>). Read only: such a file is neither created nor written.</summary>
    Access97,
    /// <summary>Access 2000, 2002 and 2003 (.mdb, Jet 4, page size 4096; Jackcess's <c>V2000</c> and <c>V2003</c>).</summary>
    Access2000,
    /// <summary>Access 2007 (.accdb, ACE 12, page size 4096; Jackcess's <c>V2007</c>).</summary>
    Access2007,
    /// <summary>Access 2010 (.accdb, ACE 14; Jackcess's <c>V2010</c>), which Access 2013 and later also write until a file needs more.</summary>
    Access2010,
    /// <summary>Access 2016 (.accdb, ACE 16; Jackcess's <c>V2016</c>): what a file becomes when a table of it has a Large Number column.</summary>
    Access2016,
    /// <summary>Access 2019 and later (.accdb, ACE 17; Jackcess's <c>V2019</c>): what a file becomes when a table of it has a Date/Time Extended column.</summary>
    Access2019,
}
