using System.Text.Json;
using System.Text.Json.Serialization;

namespace Edvanz.Application.Json;

/// <summary>
/// Serializes every <see cref="DateTime"/> on the wire as an explicit UTC instant
/// (<c>2026-08-31T13:15:45.1234567Z</c>).
///
/// <para>WHY: every moment-in-time this system stores is UTC (TIMEZONE_STANDARD.md §1), but
/// EF Core materializes SQL Server <c>datetime2</c> with <see cref="DateTimeKind.Unspecified"/>,
/// which <c>System.Text.Json</c> writes WITHOUT a suffix. A value computed in memory
/// (<c>DateTime.UtcNow</c> echoed straight back in a create response) is
/// <see cref="DateTimeKind.Utc"/> and DOES carry a <c>Z</c>. So the same logical field
/// travelled in two different shapes depending on whether it had been round-tripped through
/// the database — and a suffix-less instant is read as LOCAL by every standards-compliant
/// client parser (Dart, JS), landing 2–3h off for Egypt. This converter removes the
/// ambiguity at its source: the payload now states the zone instead of relying on each
/// client to remember which fields are UTC.</para>
///
/// <para>Reading is deliberately left at the framework's own behaviour, so nothing about
/// what callers may send, or about what gets persisted, changes.</para>
///
/// <para>A value the backend deliberately expresses in the teacher's LOCAL wall-clock
/// (<c>LocalCollectedAt</c>, <c>PaidOnDate</c>) must opt out with
/// <see cref="LocalWallClockDateTimeJsonConverter"/>; a calendar day belongs in a
/// <c>DateOnly</c>, which this converter never sees.</para>
/// </summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    /// <summary>Framework default — a suffix-less value keeps arriving exactly as before.</summary>
    public override DateTime Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTime();

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToUtcInstant(value));

    /// <summary>
    /// Unspecified is stamped, never shifted: it is already the UTC value read back out of
    /// the database, so converting it would subtract the server offset a second time.
    /// </summary>
    internal static DateTime ToUtcInstant(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}

/// <summary>Nullable companion to <see cref="UtcDateTimeJsonConverter"/>.</summary>
public sealed class NullableUtcDateTimeJsonConverter : JsonConverter<DateTime?>
{
    /// <inheritdoc />
    public override DateTime? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : reader.GetDateTime();

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(UtcDateTimeJsonConverter.ToUtcInstant(value.Value));
    }
}
