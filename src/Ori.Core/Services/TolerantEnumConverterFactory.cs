using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ori.Core.Services;

/// <summary>
/// Reads enum values from strings or numbers and maps unrecognised values to a
/// configured fallback instead of throwing. One enum value written by a newer build
/// must not quarantine an entire settings file when an older build reads it.
/// </summary>
public sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    private readonly Dictionary<Type, object> _fallbacks;

    /// <param name="fallbacks">Type/fallback pairs; unlisted types fall back to the enum's zero value.</param>
    public TolerantEnumConverterFactory()
        : this(Array.Empty<(Type Type, object Fallback)>())
    {
    }

    public TolerantEnumConverterFactory(params (Type Type, object Fallback)[] fallbacks)
    {
        _fallbacks = fallbacks.ToDictionary(pair => pair.Type, pair => pair.Fallback);
    }

    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        _fallbacks.TryGetValue(typeToConvert, out var fallback);
        return (JsonConverter)Activator.CreateInstance(
            typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert), fallback)!;
    }

    public sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        private readonly T _fallback;

        public TolerantEnumConverter(object? fallback = null) =>
            _fallback = fallback is T typed ? typed : default;

        private static readonly bool IsFlags =
            typeof(T).IsDefined(typeof(FlagsAttribute), inherit: false);

        private static readonly long AllowedBits = Enum.GetValues<T>()
            .Aggregate(0L, (bits, value) => bits | Convert.ToInt64(value));

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                var text = reader.GetString();
                if (!string.IsNullOrWhiteSpace(text)
                    && Enum.TryParse<T>(text, ignoreCase: true, out var parsed)
                    && IsAllowed(parsed))
                    return parsed;
                return _fallback;
            }

            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var number))
            {
                var parsed = (T)Enum.ToObject(typeof(T), number);
                return IsAllowed(parsed) ? parsed : _fallback;
            }

            if (reader.TokenType == JsonTokenType.Null)
                return _fallback;

            reader.Skip();
            return _fallback;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());

        private static bool IsAllowed(T value)
        {
            if (IsFlags)
                return (Convert.ToInt64(value) & ~AllowedBits) == 0;
            return Enum.IsDefined(value);
        }
    }
}
