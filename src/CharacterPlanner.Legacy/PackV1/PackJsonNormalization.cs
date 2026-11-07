using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.PackV1;

internal static class PackJsonNormalization
{
    // encoding/json merges repeated struct/map fields, but replaces arrays and
    // individual map values. Normalize that contract before typed deserialization.
    public static JsonNode? Normalize(JsonElement value, Type type, JsonNode? previous = null)
    {
        if (value.ValueKind == JsonValueKind.Null && previous is not null &&
            (type == typeof(string) || type.IsValueType && Nullable.GetUnderlyingType(type) is null && !type.IsGenericType || type == typeof(ProgressionDefinition)))
        { return previous; }
        if (value.ValueKind == JsonValueKind.String) { return JsonValue.Create(PackJsonStrings.Decode(value)); }
        if (value.ValueKind == JsonValueKind.Array && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
        {
            var array = new JsonArray();
            foreach (var element in value.EnumerateArray()) { array.Add(Normalize(element, type.GenericTypeArguments[0])); }
            return array;
        }
        if (value.ValueKind != JsonValueKind.Object) { return JsonNode.Parse(value.GetRawText(), documentOptions: PackJson.DocumentOptions); }
        var result = previous as JsonObject ?? new JsonObject();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableDictionary<,>))
        {
            foreach (var field in value.EnumerateObject()) { result[PackJsonStrings.Name(field)] = Normalize(field.Value, type.GenericTypeArguments[1]); }
            return result;
        }
        var properties = type.GetProperties().Where(property => property.GetCustomAttribute<JsonPropertyNameAttribute>() is not null).ToArray();
        foreach (var field in value.EnumerateObject())
        {
            var manualDecoder = type == typeof(NumericContextInputDefinition) || type == typeof(ActiveSkillPreviewOutputDefinition);
            var property = properties.FirstOrDefault(property => string.Equals(property.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name, PackJsonStrings.Name(field), StringComparison.Ordinal));
            if (property is null && !manualDecoder)
            { property = properties.FirstOrDefault(property => string.Equals(property.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name, PackJsonStrings.Name(field), StringComparison.OrdinalIgnoreCase)); }
            if (property is null) { continue; }
            var key = property.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name;
            // Detach before reusing a node already parented by this object.
            result.Remove(key, out var old);
            if (old is null && field.Value.ValueKind == JsonValueKind.Null &&
                (property.PropertyType == typeof(string) || property.PropertyType.IsValueType && !property.PropertyType.IsGenericType))
            { continue; } // A null scalar leaves the decoder's initializer intact.
            result[key] = Normalize(field.Value, property.PropertyType, old);
        }
        return result;
    }
}
