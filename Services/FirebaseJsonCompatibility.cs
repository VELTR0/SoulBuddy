using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoulBuddy.Services;

/// <summary>
/// Compatibility helpers for Firebase Realtime Database REST responses.
/// Firebase may serialize nodes whose child keys are numeric (for example an
/// events node containing only child "0") as JSON arrays. The web tracker is
/// happy with either runtime shape, but System.Text.Json cannot deserialize an
/// array directly into Dictionary&lt;string, T&gt;. Older run data may also contain
/// numeric values in fields that are strings in the current tracker model.
/// </summary>
internal static class FirebaseJsonCompatibility
{
    private static bool _configured;
    private static readonly object Gate = new();

    public static void ConfigureForVercelClient()
    {
        lock (Gate)
        {
            if (_configured)
                return;

            try
            {
                var field = typeof(VercelSoullockeClient).GetField(
                    "JsonOptions",
                    BindingFlags.NonPublic | BindingFlags.Static);

                if (field?.GetValue(null) is not JsonSerializerOptions options)
                {
                    DiagnosticLog.Warning(
                        "FirebaseJson",
                        "Could not access VercelSoullockeClient JsonOptions; compatibility converters were not installed.");
                    return;
                }

                options.NumberHandling |= JsonNumberHandling.AllowReadingFromString;

                if (!options.Converters.Any(converter => converter is FirebaseDictionaryConverterFactory))
                    options.Converters.Insert(0, new FirebaseDictionaryConverterFactory());

                if (!options.Converters.Any(converter => converter is FlexibleStringJsonConverter))
                    options.Converters.Add(new FlexibleStringJsonConverter());

                _configured = true;
                DiagnosticLog.Info(
                    "FirebaseJson",
                    "Installed Firebase RTDB compatibility converters (object/array dictionaries and flexible scalar strings).");
            }
            catch (Exception ex)
            {
                DiagnosticLog.Exception(
                    "FirebaseJson",
                    "Failed to install Firebase RTDB compatibility converters",
                    ex);
            }
        }
    }
}

internal sealed class FirebaseDictionaryConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        if (!typeToConvert.IsGenericType ||
            typeToConvert.GetGenericTypeDefinition() != typeof(Dictionary<,>))
        {
            return false;
        }

        var arguments = typeToConvert.GetGenericArguments();
        return arguments[0] == typeof(string);
    }

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var valueType = typeToConvert.GetGenericArguments()[1];
        var converterType = typeof(FirebaseStringKeyDictionaryConverter<>).MakeGenericType(valueType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

internal sealed class FirebaseStringKeyDictionaryConverter<TValue>
    : JsonConverter<Dictionary<string, TValue>>
{
    public override Dictionary<string, TValue> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var result = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);

        if (reader.TokenType == JsonTokenType.Null)
            return result;

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return result;

                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException($"Expected Firebase object property but got {reader.TokenType}.");

                var key = reader.GetString() ?? string.Empty;
                if (!reader.Read())
                    throw new JsonException("Unexpected end of Firebase object.");

                if (reader.TokenType == JsonTokenType.Null)
                    continue;

                var value = JsonSerializer.Deserialize<TValue>(ref reader, options);
                if (value is not null)
                    result[key] = value;
            }

            throw new JsonException("Unexpected end of Firebase object.");
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var index = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    return result;

                if (reader.TokenType != JsonTokenType.Null)
                {
                    var value = JsonSerializer.Deserialize<TValue>(ref reader, options);
                    if (value is not null)
                        result[index.ToString(CultureInfo.InvariantCulture)] = value;
                }

                index++;
            }

            throw new JsonException("Unexpected end of Firebase array.");
        }

        throw new JsonException(
            $"Expected Firebase object or array for dictionary, got {reader.TokenType}.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        Dictionary<string, TValue> value,
        JsonSerializerOptions options)
    {
        // Always write dictionaries as objects. This keeps SoulBuddy's writes stable
        // even when all keys happen to be numeric.
        writer.WriteStartObject();
        foreach (var pair in value)
        {
            writer.WritePropertyName(pair.Key);
            JsonSerializer.Serialize(writer, pair.Value, options);
        }
        writer.WriteEndObject();
    }
}

internal sealed class FlexibleStringJsonConverter : JsonConverter<string>
{
    public override string? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetInt64(out var integer) =>
                integer.ToString(CultureInfo.InvariantCulture),
            JsonTokenType.Number when reader.TryGetDecimal(out var number) =>
                number.ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Cannot convert {reader.TokenType} to string.")
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
