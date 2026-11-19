using System.Reflection;

namespace JackcessDotNet;

/// <summary>
/// What a new database starts as: a copy of an empty one Access's own engine made, as in Jackcess, with the
/// format's version in its header.
/// <para>
/// The Access 2000-2003 file is Jackcess's <c>empty2003.mdb</c>. The two .accdb files were made by DAO's
/// <c>CreateDatabase</c> (<c>tools/empty-databases.ps1</c>): the Access 2007 format, whose tables sort in
/// General - Legacy, and the Access 2010 format, whose tables sort in General.
/// </para>
/// <para>
/// Access makes no empty file in the format of Access 2016 or of Access 2019. It makes one in the 2010 format, and
/// sets the version in the header when a table first has a column the older format lacks - 0x05 for a Large
/// Number, 0x06 for a Date/Time Extended - changing nothing else in the header. <see cref="FileFormat.Access2016"/> and
/// <see cref="FileFormat.Access2019"/> are therefore the Access 2010 file with that version, which is what Jackcess's
/// own empty files for them are.
/// </para>
/// </summary>
internal sealed class DatabaseHeader
{
    /// <summary>Where the header keeps the format's version: a byte no codec hides.</summary>
    internal const int OffsetVersion = 0x14;

    /// <summary>The version of the format Access 2016 writes: a file with a Large Number column.</summary>
    internal const byte Version2016 = 0x05;

    /// <summary>The version of the format Access 2019 writes: a file with a Date/Time Extended column.</summary>
    internal const byte Version2019 = 0x06;

    private readonly JetFormat _format;

    private DatabaseHeader(JetFormat format)
    {
        _format = format ?? throw new ArgumentNullException(nameof(format));
    }

    public static DatabaseHeader Create(JetFormat format)
        => new(format);

    public void WriteTo(PageFile file)
    {
        if (file is null)
            throw new ArgumentNullException(nameof(file));

        file.EnsureWritable();

        string resourceName = _format.Version switch
        {
            FileFormat.Access2000  => "JackcessDotNet.Resources.empty2003.mdb",
            FileFormat.Access2007 => "JackcessDotNet.Resources.empty2007.accdb",
            FileFormat.Access2010 or FileFormat.Access2016 or FileFormat.Access2019 => "JackcessDotNet.Resources.empty2010.accdb",
            _ => throw new InvalidOperationException($"Unsupported format {_format.Version}.")
        };

        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new InvalidOperationException($"Missing embedded resource '{resourceName}'.");

        file.ReplaceWith(stream);

        if (_format.Version == FileFormat.Access2016) RaiseVersion(file, Version2016);
        if (_format.Version == FileFormat.Access2019) RaiseVersion(file, Version2019);
    }

    /// <summary>
    /// Whether the file is one of Access 2016's format or a later one, as its header says now: one that has the Large
    /// Number, which a file of an older format is given only by a table with such a column.
    /// </summary>
    internal static bool HasLargeNumber(PageFile file) => file.ReadKeptPage(0)[OffsetVersion] >= Version2016;

    /// <summary>
    /// Makes the file one of at least <paramref name="version"/>, as Access does when a table is given a column
    /// an older format lacks: the header's version byte becomes <paramref name="version"/> and the byte after it
    /// zero. A file already of that version or a later one is left as it is.
    /// </summary>
    internal static void RaiseVersion(PageFile file, byte version)
    {
        byte[] header = file.ReadKeptPage(0);
        if (header[OffsetVersion] >= version) return;

        header[OffsetVersion]     = version;
        header[OffsetVersion + 1] = 0x00;
        file.WritePage(0, header);
    }
}
