using System.Globalization;
using SNChat.Core.Models;

namespace SNChat.Tests;

/// <summary>
/// Message timestamps are held as UTC and stored without a timezone marker, so
/// how they are parsed and converted decides whether the UI shows local time.
/// Written so they hold whatever timezone the machine running them is in.
/// </summary>
public class TimestampTests
{
    private const string Stored = "2026-08-31 17:19:36";

    /// <summary>
    /// The bug was simply that nothing converted: the card bound Timestamp and
    /// formatted it, so the UTC value went straight to the screen.
    /// </summary>
    [Fact]
    public void LocalTimestamp_converts_from_utc()
    {
        var utc = new DateTime(2026, 8, 31, 17, 19, 36, DateTimeKind.Utc);
        var message = new Message { Timestamp = utc };

        // Compared against the machine's own offset rather than a fixed one, so
        // this still holds on a machine running UTC.
        Assert.Equal(DateTimeKind.Local, message.LocalTimestamp.Kind);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(utc), message.LocalTimestamp - utc);
    }

    /// <summary>
    /// Why conversations saved before the storage fix still display correctly:
    /// ToLocalTime treats an Unspecified value as UTC, which is what those
    /// files hold. Pinned because the display would silently regress by an
    /// entire timezone offset if this ever stopped being true.
    /// </summary>
    [Fact]
    public void Unspecified_is_treated_as_utc_when_converting()
    {
        var unspecified = DateTime.Parse(Stored, CultureInfo.InvariantCulture);
        var utc = DateTime.SpecifyKind(unspecified, DateTimeKind.Utc);

        Assert.Equal(DateTimeKind.Unspecified, unspecified.Kind);
        Assert.Equal(utc.ToLocalTime(), unspecified.ToLocalTime());
    }

    /// <summary>
    /// Parsing now states the kind rather than leaving it to be inferred.
    /// </summary>
    [Fact]
    public void Assuming_universal_produces_a_utc_value()
    {
        var parsed = DateTime.Parse(
            Stored,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal(new DateTime(2026, 8, 31, 17, 19, 36, DateTimeKind.Utc), parsed);
    }

    [Fact]
    public void Round_tripping_a_stored_timestamp_preserves_the_instant()
    {
        var original = new DateTime(2026, 8, 31, 17, 19, 36, DateTimeKind.Utc);

        var written = original.ToUniversalTime().ToString(
            "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var read = DateTime.Parse(
            written,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        Assert.Equal(original, read);
    }
}
