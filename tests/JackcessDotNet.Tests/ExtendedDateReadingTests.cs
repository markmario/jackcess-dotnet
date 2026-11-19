using System.Globalization;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// How Access reads the 42 bytes of a Date/Time Extended value, and so how this library reads them (#64): as three
/// numbers - the day from the start, the time of day after the first colon, the count of the fraction's digits after
/// the last - not as fields at fixed places. Every case is bytes changed in a file Access made, then read by DAO 16
/// and asked of ACE 16's <c>=</c> (through a snapshot and a table-type recordset alike, which read every case the
/// same): what DAO read is beside each. Where Access has an error - DAO gives Empty, and ACE's <c>=</c> fails, "Internal
/// error" or, for a count of 10 or more, "Attempted to divide by zero" - this library reads null; so it does where
/// Access reads a value no <see cref="DateTime"/> is. ExportMdb.Interop's referee tests patch such bytes into a file
/// and have DAO and ACE read them beside this library.
/// </summary>
public sealed class ExtendedDateReadingTests
{
    // 2 January 2020, 03:04:05.1234567: day 737425, and 110451234567 hundreds of nanoseconds.
    private const string D = "2020-01-02 03:04:05.1234567";

    private static DateTime? Read(string stored)
    {
        Assert.True(stored.Length <= ExtendedDateTimeCodec.Size, $"'{stored}' is {stored.Length} bytes.");
        // One byte a character, the rest of the 42 zero bytes, as a value shorter than its field is filled out.
        var bytes = new byte[ExtendedDateTimeCodec.Size];
        for (int i = 0; i < stored.Length; i++) bytes[i] = checked((byte)stored[i]);
        return ExtendedDateTimeCodec.Decode(bytes, 0);
    }

    private static DateTime? Expected(string? date)
        => date is null ? null : DateTime.ParseExact(date, "yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    // ── The layouts Access writes ───────────────────────────────────────────

    [Theory]
    [InlineData("0000000000000737425:0000000110451234567:7\0", D)]                              // INSERT
    [InlineData("0000000000000737425:0000000000000011045:0\0", "2020-01-02 03:04:05.0000000")]   // UPDATE, a dynaset
    [InlineData("000000000737425:0000000110451234567:7\0\0\0\0\0", D)]                          // a table-type recordset
    [InlineData("000000000737425:0000000110451234567:7\0\0\0\u001F\u0002", D)]                  // its last bytes as DAO left them
    [InlineData("000000000737425:0000000110451234567:7\0\0\0ta", D)]
    [InlineData("000000000737425:0000000110451234567:7\0\0\0®\u0002", D)]
    [InlineData("000000000737425:0000000110451234567:7\0ÿÿÿÿ", D)]
    [InlineData("000000000737425:0000000110451234567:7\01234", D)]
    [InlineData("000000000000000:0000000000000000000:7\0\0\0" + "7\0", "0001-01-01 00:00:00.0000000")]
    [InlineData("000000003652058:0000000863999999999:7\0\0\0" + "5\0", "9999-12-31 23:59:59.9999999")]
    public void Each_layout_Access_writes_is_read_as_its_date(string stored, string expected)
        => Assert.Equal(Expected(expected), Read(stored));

    // ── 1. The value ends at its first zero byte ────────────────────────────

    [Theory]
    [InlineData("000000000737425:0000000110451234567:7\0:5", D)]           // the colon after it is not read: the count is 7
    [InlineData("737425:110451234567\0:7", null)]                          // one colon before it: an error
    [InlineData("000000000000737\0425:0000000110451234567:7\0", null)]     // no colon before it
    [InlineData("\0000000000000737425:0000000110451234567:7\0", null)]     // nothing before it
    [InlineData("737425:110451234567:\07", null)]                          // no count before it
    [InlineData("0000000000000737425:0000000110451234567:7 ", D)]          // none at all: the 42 bytes
    public void The_value_ends_at_its_first_zero_byte(string stored, string? expected)
        => Assert.Equal(Expected(expected), Read(stored));

    // ── 2. The colons: the day before the first, the count after the last ───

    [Theory]
    [InlineData("737425:110451234567:7", D)]                               // as few digits as the numbers have
    [InlineData("0:0000000110451234567:7", "0001-01-01 03:04:05.1234567")]
    [InlineData("737425:0:7", "2020-01-02 00:00:00.0000000")]
    [InlineData("0000000000000000000737425:0110451234567:7", D)]           // a day of 25 digits
    [InlineData("737425:000000000000000000110451234567:7", D)]             // a time of 30
    [InlineData("737425:110451234567:7:7", D)]                             // the count after the last colon
    [InlineData("737425:110451234567:0:7", D)]
    [InlineData("737425:110451234567::7", D)]
    [InlineData("737425:110451234567:junk:7", D)]
    [InlineData("73:7425:110451234567:7", "0001-03-15 00:00:00.0007425")] // the time after the first
    [InlineData("737425:110451234567:7:5", null)]                          // a count of 5: 306:48:32, a time past a day
    [InlineData("0000000000000737425 0000000110451234567 7\0", null)]      // no colon: an error
    [InlineData("0000000000000737425:0000000110451234567 7\0", null)]      // one
    [InlineData("0000000000000737425 0000000110451234567:7\0", null)]
    [InlineData("0000000000000737425;0000000110451234567:7\0", null)]
    [InlineData("000000000000073742500000000110451234567:7\0", null)]
    [InlineData(":0000000110451234567:7\0", null)]                         // a field with nothing in it: an error
    [InlineData("::737425:110451234567:7", null)]
    [InlineData("0000000000000737425::7\0", null)]
    [InlineData("737425::110451234567:7", null)]
    [InlineData("0000000000000737425:0000000110451234567:7:", null)]
    [InlineData("000000000737425:0000000110451234567:", null)]
    [InlineData("::", null)]
    [InlineData(":", null)]
    [InlineData("7", null)]
    [InlineData("", null)]
    public void The_day_is_before_the_first_colon_the_time_after_it_and_the_count_after_the_last(string stored, string? expected)
        => Assert.Equal(Expected(expected), Read(stored));

    // ── 3. A number ─────────────────────────────────────────────────────────

    // What may begin a number: a digit, white space as C has it, a plus sign. White space is skipped, then one plus
    // sign, then the digits are read up to the first byte that is not one.
    [Theory]
    [InlineData("             737425:0000000110451234567:7\0", D)]
    [InlineData("737425             :0000000110451234567:7\0", D)]
    [InlineData("      737425       :0000000110451234567:7\0", D)]
    [InlineData("+000000000000737425:0000000110451234567:7\0", D)]
    [InlineData(" +00000000000737425:0000000110451234567:7\0", D)]
    [InlineData("\t000000000000737425:0000000110451234567:7\0", D)]
    [InlineData("\n000000000000737425:0000000110451234567:7\0", D)]
    [InlineData("\v000000000000737425:0000000110451234567:7\0", D)]
    [InlineData("\f000000000000737425:0000000110451234567:7\0", D)]
    [InlineData("\r000000000000737425:0000000110451234567:7\0", D)]
    [InlineData("\t+00000000000737425:0000000110451234567:7\0", D)]
    [InlineData(" \n00000000000737425:0000000110451234567:7\0", D)]
    [InlineData("0000000000000737425:       110451234567:7\0", D)]
    [InlineData("0000000000000737425:110451234567       :7\0", D)]
    [InlineData("0000000000000737425:+000000110451234567:7\0", D)]
    [InlineData("0000000000000737425:\t000000110451234567:7\0", D)]
    [InlineData("000000000737425:0000000110451234567: 7", D)]
    [InlineData("000000000737425:0000000110451234567:+7", D)]
    [InlineData("000000000737425:0000000110451234567:07", D)]
    [InlineData("000000000737425:0000000110451234567:007", D)]
    [InlineData("000000000737425:0000000110451234567:  7", D)]
    [InlineData("000000000737425:0000000110451234567:\t7", D)]
    [InlineData("000000000737425:0000000110451234567: +7", D)]
    [InlineData("737425:110451234567:0000000007", D)]
    [InlineData("737425:110451234567:00000000000000000007", D)]
    public void A_number_may_have_white_space_and_a_plus_sign_before_its_digits(string stored, string expected)
        => Assert.Equal(Expected(expected), Read(stored));

    // Anything else first is an error: a minus sign, a letter, punctuation, a byte C does not take for white space.
    [Theory]
    [InlineData("-000000000000737425:0000000110451234567:7\0")]
    [InlineData("-000000000000000000:0000000110451234567:7\0")]           // even of 0
    [InlineData("x000000000000737425:0000000110451234567:7\0")]
    [InlineData(".000000000000737425:0000000110451234567:7\0")]
    [InlineData(",000000000000737425:0000000110451234567:7\0")]
    [InlineData("#000000000000737425:0000000110451234567:7\0")]
    [InlineData(" 000000000000737425:0000000110451234567:7\0")]      // a no-break space
    [InlineData("\u0085000000000000737425:0000000110451234567:7\0")]
    [InlineData("\u001C000000000000737425:0000000110451234567:7\0")]      // the separators 0x1C to 0x1F
    [InlineData("\u001F000000000000737425:0000000110451234567:7\0")]
    [InlineData("\u0008000000000000737425:0000000110451234567:7\0")]
    [InlineData("\u007F000000000000737425:0000000110451234567:7\0")]
    [InlineData("\u0001000000000000737425:0000000110451234567:7\0")]
    [InlineData("ÿ000000000000737425:0000000110451234567:7\0")]
    [InlineData("²000000000000737425:0000000110451234567:7\0")]      // a superscript two
    [InlineData("0000000000000737425:-000000110451234567:7\0")]
    [InlineData("0000000000000737425:x000000110451234567:7\0")]
    [InlineData("000000000737425:0000000110451234567:-7")]
    [InlineData("000000000737425:0000000110451234567:x")]
    [InlineData("000000000737425:0000000110451234567:\u00857")]
    public void A_number_that_begins_otherwise_is_an_error(string stored)
        => Assert.Null(Read(stored));

    // The digits end at the first byte that is not one, and no digit at all is 0.
    [Theory]
    [InlineData("000000000000073742X:0000000110451234567:7\0", "0202-11-26 03:04:05.1234567")]
    [InlineData("000000000000737X425:0000000110451234567:7\0", "0003-01-08 03:04:05.1234567")]
    [InlineData("000000000000737 425:0000000110451234567:7\0", "0003-01-08 03:04:05.1234567")]
    [InlineData("000000000000737²425:0000000110451234567:7\0", "0003-01-08 03:04:05.1234567")]
    [InlineData("                   :0000000110451234567:7\0", "0001-01-01 03:04:05.1234567")]
    [InlineData("+:0000000110451234567:7", "0001-01-01 03:04:05.1234567")]
    [InlineData("+ :0000000110451234567:7", "0001-01-01 03:04:05.1234567")]
    [InlineData("+ 00000000000737425:0000000110451234567:7\0", "0001-01-01 03:04:05.1234567")]   // a space after the sign
    [InlineData("++00000000000737425:0000000110451234567:7\0", "0001-01-01 03:04:05.1234567")]
    [InlineData("+-00000000000737425:0000000110451234567:7\0", "0001-01-01 03:04:05.1234567")]
    [InlineData("+x00000000000737425:0000000110451234567:7\0", "0001-01-01 03:04:05.1234567")]
    [InlineData("  -0000000000737425:0000000110451234567:7\0", "0001-01-01 03:04:05.1234567")]   // a minus sign after white space
    [InlineData("  x0000000000737425:0000000110451234567:7\0", "0001-01-01 03:04:05.1234567")]
    [InlineData(" \u001C00000000000737425:0000000110451234567:7\0", "0001-01-01 03:04:05.1234567")]
    [InlineData("0x00000000000737425:0000000110451234567:7\0", "0001-01-01 03:04:05.1234567")]
    [InlineData("0000000000000737425:00000001104512345X7:7\0", "2020-01-02 00:01:50.4512345")]
    [InlineData("0000000000000737425:00000001104512345 7:7\0", "2020-01-02 00:01:50.4512345")]
    [InlineData("0000000000000737425:                   :7\0", "2020-01-02 00:00:00.0000000")]
    [InlineData("737425: :7", "2020-01-02 00:00:00.0000000")]
    [InlineData("0000000000000737425:  -0000110451234567:7\0", "2020-01-02 00:00:00.0000000")]
    [InlineData("000000000737425:0000000110451234567:7x", D)]
    [InlineData("000000000737425:0000000110451234567:7 5", D)]
    [InlineData("000000000737425:0000000110451234567:7.5", D)]
    [InlineData("0000000000000737425:0000000110451234567:7x", D)]
    [InlineData("0000000000000737425:0000000110451234567:7ÿ", D)]
    [InlineData("0000000000000737425:0000000110451234567:7\u0001", D)]
    public void A_number_s_digits_end_at_the_first_byte_that_is_not_one(string stored, string expected)
        => Assert.Equal(Expected(expected), Read(stored));

    // A number of more than 2^64 - 1 is an error; the day and the count are taken to their low 32 bits.
    [Theory]
    [InlineData("4295704721:110451234567:7", D)]                                       // 2^32 + 737425
    [InlineData("9223372036855513233:110451234567:7", D)]                              // 2^63 + 737425
    [InlineData("18446744069415321745:110451234567:7", D)]                             // 2^64 - 2^32 + 737425
    [InlineData("4294967296:0:7", "0001-01-01 00:00:00.0000000")]                      // 2^32: day 0
    [InlineData("737425:110451234567:4294967303", D)]                                  // a count of 2^32 + 7
    [InlineData("737425:4294967296:7", "2020-01-02 00:07:09.4967296")]                 // the time is not cut
    [InlineData("2147483648:0:7", null)]                                               // 2^31: a day past the last
    [InlineData("18446744073709551615:0:7", null)]                                     // 2^64 - 1: day 2^32 - 1
    [InlineData("18446744073709551616:0:7", null)]                                     // 2^64: an error
    [InlineData("99999999999999999999:000000000000000000:7\0", null)]
    [InlineData("737425:18446744073709551616:7", null)]
    [InlineData("737425:99999999999999999999:7", null)]
    [InlineData("737425:110451234567:18446744073709551623", null)]
    [InlineData("737425:110451234567:4294967296", null)]                               // a count of 2^32: 0, whole seconds
    public void A_number_is_read_to_64_bits_and_the_day_and_the_count_are_taken_to_32(string stored, string? expected)
        => Assert.Equal(Expected(expected), Read(stored));

    // ── 4. The count: the time divided by 10 to it is in seconds, the remainder in units of 100 ns ─────────

    [Theory]
    [InlineData("000000000737425:0000000000000011045:0", "2020-01-02 03:04:05.0000000")]
    [InlineData("000000000737425:0000000000000110451:1", "2020-01-02 03:04:05.0000001")]
    [InlineData("000000000737425:0000000000001104512:2", "2020-01-02 03:04:05.0000012")]
    [InlineData("000000000737425:0000000000011045123:3", "2020-01-02 03:04:05.0000123")]
    [InlineData("000000000737425:0000000000110451234:4", "2020-01-02 03:04:05.0001234")]
    [InlineData("000000000737425:0000000001104512345:5", "2020-01-02 03:04:05.0012345")]
    [InlineData("000000000737425:0000000011045123456:6", "2020-01-02 03:04:05.0123456")]
    [InlineData("000000000737425:0000000110451234567:7", D)]
    [InlineData("000000000737425:0000001104500000001:8", "2020-01-02 03:04:05.0000001")]
    [InlineData("000000000737425:0000001104500000010:8", "2020-01-02 03:04:05.0000010")]
    [InlineData("000000000737425:0000011045000000001:9", "2020-01-02 03:04:05.0000001")]
    [InlineData("000000000737425:0000000086399999999:6", "2020-01-02 23:59:59.0999999")]
    public void The_count_divides_the_time_into_seconds_and_a_remainder_in_ticks(string stored, string expected)
        => Assert.Equal(Expected(expected), Read(stored));

    [Theory]
    [InlineData("000000000737425:0000000110451234567:10")]   // "Attempted to divide by zero"
    [InlineData("000000000737425:0000000110451234567:70")]
    [InlineData("000000000737425:0000000110451234567:71")]
    [InlineData("0000000000000737425:0000000110451234567:75")]
    [InlineData("0000000000000737425:0000000110451234567:70")]
    public void A_count_of_10_or_more_is_an_error(string stored)
        => Assert.Null(Read(stored));

    // ── What Access shows that is no DateTime ────────────────────────────────

    [Theory]
    [InlineData("0000000000003652059:0000000000000000000:7\0")]   // DAO: 12/30/1899; ACE puts it after 31 December 9999
    [InlineData("9999999999999999999:0000000000000000000:7\0")]   // the same
    [InlineData("0000000000003652058:0000000864000000000:7\0")]   // DAO: 12/31/9999 24:00:00
    [InlineData("0000000000000737425:0000000864000000000:7\0")]   // DAO: 24:00:00; ACE puts it after 23:59:59.9999999, before the next day
    [InlineData("0000000000000737425:0000000864000000001:7\0")]   // DAO: 24:00:00.0000001
    [InlineData("0000000000000737425:0000000000000086400:0\0")]   // DAO: 24:00:00
    [InlineData("0000000000000737425:0000000000000172800:0\0")]   // DAO: 48:00:00
    [InlineData("0000000000000737425:0000000110451234567:0\0")]   // DAO: 10050:29:27, the time in seconds
    [InlineData("000000000737425:0000000110451234567:3")]         // DAO: 30680:53:54.0000567
    [InlineData("000000000737425:0000000110451234567:1")]         // DAO: -12103:50:56.0000007
    [InlineData("000000000737425:0000000110451234567: ")]         // DAO: 10050:29:27, a count of no digits
    [InlineData("000000000737425:0000000110451234567:+ 7")]       // the same
    [InlineData("000000000737425:0000001104512345678:8")]         // DAO: 03:04:05.12345678; ACE's = finds it for no date
    [InlineData("000000000737425:0000011045123456789:9")]         // DAO: 03:04:05.123456789
    [InlineData("000000000737425:0000000110451234567:9")]         // DAO: 00:01:50.451234567
    public void A_value_no_DateTime_holds_is_read_as_null(string stored)
        => Assert.Null(Read(stored));

    [Theory]
    [InlineData("0:0:7", "0001-01-01 00:00:00.0000000")]
    [InlineData("0:0:0", "0001-01-01 00:00:00.0000000")]
    [InlineData("00:00:07", "0001-01-01 00:00:00.0000000")]
    [InlineData("0000000000000000000:0000000000000000000:7\0", "0001-01-01 00:00:00.0000000")]
    [InlineData("0000000000003652058:0000000863999999999:7\0", "9999-12-31 23:59:59.9999999")]
    [InlineData("0000000000000737425:0000000000000086399:0\0", "2020-01-02 23:59:59.0000000")]
    public void The_ends_of_the_range_are_read(string stored, string expected)
        => Assert.Equal(Expected(expected), Read(stored));

    [Fact]
    public void Bytes_with_no_colon_are_no_date()
    {
        Assert.Null(Read(new string(' ', 42)));
        Assert.Null(Read(new string('0', 42)));
    }

    // ── What earlier versions read ──────────────────────────────────────────

    // Versions of this library that keyed the date they read (#51) read the colons at their places in the first form,
    // and so the third layout as no value; the entries they made for such a row are under the key of no value.
    [Theory]
    [InlineData("0000000000000737425:0000000110451234567:7\0", D)]
    [InlineData("0000000000000737425:0000000000000011045:0\0", "2020-01-02 03:04:05.0000000")]
    [InlineData(" 000000000000737425:0000000110451234567:7\0", D)]
    [InlineData("000000000737425:0000000110451234567:7\0\0\0\0\0", null)]
    [InlineData("000000000737425:0000000110451234567:7\0\0\0" + "7\0", null)]
    [InlineData("000000000000073742X:0000000110451234567:7\0", null)]
    [InlineData("0000000000000737425:0000000000011045123:3\0", null)]
    public void Versions_that_keyed_the_date_they_read_read_it_at_the_first_form_s_places(string stored, string? expected)
    {
        var bytes = new byte[ExtendedDateTimeCodec.Size];
        for (int i = 0; i < stored.Length; i++) bytes[i] = (byte)stored[i];
        Assert.Equal(Expected(expected), ExtendedDateTimeCodec.DecodeAsEarlierVersions(bytes, 0));
        Assert.Equal(Expected(expected), new StoredExtendedDate(bytes).DateAsEarlierVersionsRead(secondFormAsDate: true));
    }
}
