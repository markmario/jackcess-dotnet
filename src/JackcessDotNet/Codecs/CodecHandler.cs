namespace JackcessDotNet;

/// <summary>
/// Per-page encode/decode transformation applied by <see cref="PageFile"/> on
/// every read and write. The default <see cref="NullCodecHandler"/> is the
/// identity. <see cref="Database.Open(string, DatabaseOpenOptions)"/> gives a file
/// the codec its header asks for: <see cref="JetCryptCodecHandler"/> for an .mdb
/// whose pages Access encoded, <see cref="OfficeCryptCodecHandler"/> for an
/// encrypted .accdb. A codec leaves page 0, the header, as it is.
/// </summary>
internal interface ICodecHandler
{
    /// <summary>
    /// Returns the plaintext page bytes given the raw bytes read from disk.
    /// <paramref name="pageNumber"/> is supplied because real codecs derive the
    /// page-specific keystream from the page index.
    /// </summary>
    byte[] DecodePage(byte[] rawPage, int pageNumber);

    /// <summary>Inverse of <see cref="DecodePage"/>: returns the bytes to write to disk.</summary>
    byte[] EncodePage(byte[] page, int pageNumber);
}

/// <summary>Identity codec used for unencrypted .mdb / .accdb files.</summary>
internal sealed class NullCodecHandler : ICodecHandler
{
    public static readonly NullCodecHandler Instance = new();
    public byte[] DecodePage(byte[] rawPage, int pageNumber) => rawPage;
    public byte[] EncodePage(byte[] page,    int pageNumber) => page;
}
