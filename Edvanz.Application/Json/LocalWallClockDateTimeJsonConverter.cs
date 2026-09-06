using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Edvanz.Application.Json;

/// <summary>
/// Opt-out from <see cref="UtcDateTimeJsonConverter"/> for the few fields the backend
/// deliberately expresses in the teacher's LOCAL wall-clock rather than as a UTC instant —
/// today <c>PaymentTransaction.LocalCollectedAt</c> and the student tracking screen's
/// <c>PaidOnDate</c>, which exist so a receipt shows the time the tutor actually took the
/// cash (TIMEZONE_STANDARD.md §1).
///
/// <para>Stamping those with a <c>Z</c> would be a lie, and any client that then localized
/// them would move the receipt by the UTC offset. They are written with no suffix — the
/// long-standing shape — so the client keeps reading them as the wall-clock they are.</para>
///
/// <para>Apply per property: <c>[JsonConverter(typeof(LocalWallClockDateTimeJsonConverter))]</c>.
/// A property-level attribute wins over the globally registered converters.</para>
/// </summary>
public sealed class LocalWallClockDateTimeJsonConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-ddTHH:mm:ss.FFFFFFF";

    /// <inheritdoc />
    public override DateTime Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTime();

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
}

/// <summary>Nullable companion to <see cref="LocalWallClockDateTimeJsonConverter"/>.</summary>
public sealed class NullableLocalWallClockDateTimeJsonConverter : JsonConverter<DateTime?>
{
    private const string Format = "yyyy-MM-ddTHH:mm:ss.FFFFFFF";

    /// <inheritdoc />
    public override DateTime? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : reader.GetDateTime();

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value.ToString(Format, CultureInfo.InvariantCulture));
    }
}
