using System.Text.Json;
using System.Text.Json.Serialization;

#pragma warning disable IDE0130
namespace Veil.Shared;
#pragma warning restore IDE0130

public sealed class ObfuscatedIdJsonConverter : JsonConverter<ObfuscatedId> {
    public override ObfuscatedId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        string? value = reader.GetString();
        if(string.IsNullOrWhiteSpace(value)) return default;
        return new ObfuscatedId(value);
    }

    public override void Write(Utf8JsonWriter writer, ObfuscatedId value, JsonSerializerOptions options) {
        if(value.Value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value);
    }
}