using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CharacterPlanner.Core.Expressions;

using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Legacy.PackV1;

public static class PackJson
{
    internal static JsonDocumentOptions DocumentOptions { get; } = new() { MaxDepth = 10000 };
    private static readonly JsonSerializerOptions Options = CreateOptions();

    // Core accepts bytes; opening files belongs to Infrastructure.
    // Unknown fields are ignored. Repeated scalars/arrays replace values; repeated
    // structs/maps merge. Portable build JSON has a separate contract.
    public static GamePack Deserialize(ReadOnlyMemory<byte> json)
    {
        using var document = ParseDocument(json);
        CheckShape(document.RootElement, typeof(GamePack));
        var normalized = PackJsonNormalization.Normalize(document.RootElement, typeof(GamePack));
        var pack = normalized?.Deserialize<GamePack>(Options) ?? new GamePack();
        return pack with
        {
            Progression = pack.Progression ?? new(),
            Stats = pack.Stats.Select(stat => stat.Format.Length == 0 ? stat with { Format = "integer" } : stat).ToImmutableArray(),
            Attributes = pack.Attributes.Select(attribute => attribute.Format.Length == 0 ? attribute with { Format = "decimal" } : attribute).ToImmutableArray()
        };
    }

    private static JsonDocument ParseDocument(ReadOnlyMemory<byte> json)
    {
        try { return JsonDocument.Parse(json, DocumentOptions); }
        catch (JsonException exception)
        { throw new JsonException(PackJsonSyntax.Describe(json.Span) ?? exception.Message); }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 10000 };
        options.Converters.Add(new CurvePointConverter());
        options.Converters.Add(new ArrayConverterFactory());
        options.Converters.Add(new DictionaryConverterFactory());
        options.Converters.Add(new FiniteDoubleConverter());
        options.Converters.Add(new LegacyLongConverter());
        options.Converters.Add(new LegacyBooleanConverter());
        options.Converters.Add(new LegacyStringConverter());
        options.Converters.Add(new ModifierValueConverter());
        return options;
    }

    private static void CheckShape(JsonElement element, Type type, string fieldPath = "", string? wireType = null, bool structField = true)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(CurvePoint)) { _ = element.Deserialize<CurvePoint>(Options); return; }
        if (HasCustomDecoder(type)) { fieldPath = ""; }
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (type == typeof(AttributeDefinition)) { throw new JsonException("schema: attribute \"\" must specify min and max"); }
            if (type == typeof(NumericContextInputDefinition)) { throw new JsonException("schema: numeric context input requires \"id\""); }
            if (type == typeof(ActiveSkillPreviewOutputDefinition)) { throw new JsonException("schema: active skill preview output requires \"id\""); }
            if (type == typeof(ActiveSkillModDefinition)) { throw new JsonException("schema: skill mod requires id"); }
            if (type == typeof(ActiveSkillModSlotDefinition)) { throw new JsonException("schema: invalid skill mod slot \"\""); }
            return;
        }
        if (type == typeof(double) || type == typeof(long) || type == typeof(string) || type == typeof(bool))
        {
            var valid = type == typeof(string) ? element.ValueKind == JsonValueKind.String : type == typeof(bool) ? element.ValueKind is JsonValueKind.True or JsonValueKind.False :
                element.ValueKind == JsonValueKind.Number && (type == typeof(long) ? element.TryGetInt64(out _) : element.TryGetDouble(out var number) && double.IsFinite(number));
            if (!valid) { throw TypeError(element, type, fieldPath, structField, wireType); }
            return;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
        {
            if (element.ValueKind != JsonValueKind.Array) { throw TypeError(element, type, fieldPath, structField); }
            var index = 0;
            foreach (var child in element.EnumerateArray())
            { CheckShape(child, type.GenericTypeArguments[0], fieldPath + "." + (index++).ToString(System.Globalization.CultureInfo.InvariantCulture), structField: false); }
            return;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableDictionary<,>))
        {
            if (element.ValueKind != JsonValueKind.Object) { throw TypeError(element, type, fieldPath, structField); }
            foreach (var child in element.EnumerateObject()) { CheckShape(child.Value, type.GenericTypeArguments[1], fieldPath + "." + PackJsonStrings.Name(child), structField: structField); }
            return;
        }
        if (type.Namespace != typeof(GamePack).Namespace || type == typeof(ModifierValue)) { return; }
        if (element.ValueKind != JsonValueKind.Object) { throw TypeError(element, type, fieldPath, structField); }
        var manualDecoder = type == typeof(NumericContextInputDefinition) || type == typeof(ActiveSkillPreviewOutputDefinition);
        void Properties()
        {
            var properties = type.GetProperties().Where(property => property.GetCustomAttribute<JsonPropertyNameAttribute>() is not null).ToArray();
            foreach (var field in element.EnumerateObject())
            {
                var property = properties.FirstOrDefault(property => string.Equals(property.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name, PackJsonStrings.Name(field),
                    manualDecoder ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
                if (property is null) { continue; }
                var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name;
                var prefix = type == typeof(ActiveSkillModDefinition) || type == typeof(ActiveSkillModSlotDefinition) ? "alias." :
                    fieldPath.Length > 0 ? fieldPath + "." : "";
                if (property.PropertyType == typeof(ModifierValue)) { _ = field.Value.Deserialize<ModifierValue>(Options); }
                else
                {
                    var wire = name == "format" ? "schema.StatFormat" : name == "op" ? "schema.ModifierOp" : name == "kind" && type == typeof(StatDefinition) ? "schema.StatKind" : null;
                    CheckShape(field.Value, property.PropertyType, prefix + name, wire);
                }
            }
        }
        // Alias-based readers visit every occurrence in JSON order before their
        // custom post-checks; manual map-based readers inspect the last exact key.
        if (!manualDecoder && type != typeof(ActiveSkillModDefinition)) { Properties(); }
        // Inspect only the last occurrence, just as the legacy custom readers do.
        var fields = element.EnumerateObject().GroupBy(p => PackJsonStrings.Name(p), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
        void Require(params string[] names)
        {
            foreach (var name in names)
            {
                if (!fields.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
                {
                    throw new JsonException($"schema: skill mod requires {name}");
                }
            }
        }
        void RejectNull(params string[] names)
        {
            foreach (var name in names)
            {
                if (fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Null)
                {
                    var message = type == typeof(GamePack) ? "schema: numericContextInputs must be an array" : $"schema: skill mod {name} must not be null";
                    throw new JsonException(message);
                }
            }
        }
        if (type == typeof(GamePack)) { RejectNull("numericContextInputs"); }
        if (type == typeof(NumericContextInputDefinition))
        {
            CheckNumericInput(fields);
            return;
        }
        if (type == typeof(StatModifier) || type == typeof(AttributeDefinition))
        {
            if (type == typeof(AttributeDefinition))
            {
                var bounds = element.EnumerateObject().Where(property => PackJsonStrings.Name(property).Equals("min", StringComparison.OrdinalIgnoreCase) || PackJsonStrings.Name(property).Equals("max", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(property => PackJsonStrings.Name(property), StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
                if (!bounds.TryGetValue("min", out var min) || min.ValueKind == JsonValueKind.Null || !bounds.TryGetValue("max", out var max) || max.ValueKind == JsonValueKind.Null)
                { throw new JsonException($"schema: attribute {ExpressionParser.Quote(AliasId(element))} must specify min and max"); }
            }
            var bucket = element.EnumerateObject().LastOrDefault(field => PackJsonStrings.Name(field).Equals("bucket", StringComparison.OrdinalIgnoreCase)).Value;
            if (bucket.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) &&
                (bucket.ValueKind != JsonValueKind.String || !IsSlug(PackJsonStrings.Decode(bucket)!)))
            { throw new JsonException($"schema: bucket {ExpressionParser.Quote(PackJsonStrings.Decode(bucket)!)} must be lower_snake_case starting with a letter"); }
        }
        if (type == typeof(ActiveSkillDefinition))
        {
            var id = AliasId(element);
            if (fields.TryGetValue("family", out var nullFamily) && nullFamily.ValueKind == JsonValueKind.Null)
            { throw new JsonException($"schema: active skill {ExpressionParser.Quote(id)} family must be a string"); }
            if (fields.TryGetValue("family", out var family) && (family.ValueKind != JsonValueKind.String || !IsSlug(PackJsonStrings.Decode(family)!)))
            { throw new JsonException($"schema: active skill {ExpressionParser.Quote(id)} family must be a lower_snake_case string"); }
            foreach (var collection in new[] { "previewOutputs", "modSlots" })
            { if (fields.TryGetValue(collection, out var value) && value.ValueKind == JsonValueKind.Null) { throw new JsonException($"schema: active skill {ExpressionParser.Quote(id)} {collection} must be an array"); } }
            if (fields.TryGetValue("upgrade", out var upgrade) && (upgrade.ValueKind != JsonValueKind.Object || !upgrade.TryGetProperty("maxRank", out var rank) || rank.ValueKind == JsonValueKind.Null))
            { throw new JsonException($"schema: active skill {ExpressionParser.Quote(id)} upgrade requires a rank bound"); }
        }
        if (type == typeof(ActiveSkillPreviewOutputDefinition))
        {
            CheckPreviewOutput(fields);
            return;
        }
        if (type == typeof(ActiveSkillModDefinition))
        {
            Require("id", "name", "input", "min", "max");
            RejectNull("description", "step", "format", "statName");
            Properties();
            var choice = PackJsonNormalization.Normalize(element, type)!.Deserialize<ActiveSkillModDefinition>(Options)!;
            if (!IsSlug(choice.ID) || string.IsNullOrWhiteSpace(choice.Name) ||
                !Regex.IsMatch(choice.Input, "^skill_mod_[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant) ||
                choice.Step <= 0 || choice.Format is not ("integer" or "decimal" or "percent"))
            { throw new JsonException($"schema: invalid skill mod choice {ExpressionParser.Quote(choice.ID)}"); }
        }
        if (type == typeof(ActiveSkillModSlotDefinition))
        {
            var slot = PackJsonNormalization.Normalize(element, type)!.Deserialize<ActiveSkillModSlotDefinition>(Options)!;
            if (!IsSlug(slot.ID) || string.IsNullOrWhiteSpace(slot.Name) || slot.Choices.Length == 0)
            { throw new JsonException($"schema: invalid skill mod slot {ExpressionParser.Quote(slot.ID)}"); }
        }
    }

    private static string AliasId(JsonElement element) => element.EnumerateObject()
        .Where(field => PackJsonStrings.Name(field).Equals("id", StringComparison.OrdinalIgnoreCase) && field.Value.ValueKind == JsonValueKind.String)
        .Select(field => PackJsonStrings.Decode(field.Value)!).LastOrDefault() ?? "";

    private static bool HasCustomDecoder(Type type) => type == typeof(GamePack) || type == typeof(StatDefinition) || type == typeof(AttributeDefinition) ||
        type == typeof(StatModifier) || type == typeof(SlotDefinition) || type == typeof(ItemDefinition) || type == typeof(ModDefinition) || type == typeof(TalentDefinition) ||
        type == typeof(ActiveSkillDefinition) || type == typeof(SkillNode) || type == typeof(SpecializationDefinition) || type == typeof(SpecializationNodeDefinition) ||
        type == typeof(NumericContextInputDefinition) || type == typeof(ActiveSkillPreviewOutputDefinition) || type == typeof(ActiveSkillModDefinition) || type == typeof(ActiveSkillModSlotDefinition);

    private static string CustomString(Dictionary<string, JsonElement> fields, string name, string owner, bool required = false)
    {
        if (!fields.TryGetValue(name, out var value))
        {
            if (required) { throw new JsonException($"schema: {owner} requires {ExpressionParser.Quote(name)}"); }
            return "";
        }
        if (value.ValueKind != JsonValueKind.String) { throw new JsonException($"schema: {owner} {ExpressionParser.Quote(name)} must be a string"); }
        return PackJsonStrings.Decode(value)!;
    }

    private static void CheckNumericInput(Dictionary<string, JsonElement> fields)
    {
        const string owner = "numeric context input";
        _ = CustomString(fields, "id", owner, true);
        _ = CustomString(fields, "name", owner, true);
        _ = CustomString(fields, "description", owner);
        foreach (var name in new[] { "min", "max", "step", "default" })
        {
            if (!fields.TryGetValue(name, out var value))
            {
                if (name is "min" or "max") { throw new JsonException($"schema: {owner} requires {ExpressionParser.Quote(name)}"); }
                continue;
            }
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            { throw new JsonException($"schema: {owner} {ExpressionParser.Quote(name)} must be a number"); }
        }
        if (fields.TryGetValue("global", out var global) && global.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        { throw new JsonException("schema: numeric context input \"global\" must be a boolean"); }
        if (fields.TryGetValue("options", out var options))
        {
            try
            {
                if (options.ValueKind == JsonValueKind.Null) { throw new JsonException(); }
                CheckShape(options, typeof(ImmutableArray<NumericContextOption>));
            }
            catch (JsonException) { throw new JsonException("schema: numeric context input \"options\" must be a list of value/label pairs"); }
        }
        _ = CustomString(fields, "group", owner);
        _ = CustomString(fields, "groupName", owner);
        _ = CustomString(fields, "section", owner);
    }

    private static void CheckPreviewOutput(Dictionary<string, JsonElement> fields)
    {
        const string owner = "active skill preview output";
        var id = CustomString(fields, "id", owner, true);
        if (!IsSlug(id)) { throw new JsonException($"schema: {owner} id {ExpressionParser.Quote(id)} must be lower_snake_case starting with a letter"); }
        _ = CustomString(fields, "name", owner, true);
        _ = CustomString(fields, "description", owner);
        var when = CustomString(fields, "when", owner);
        if (fields.ContainsKey("when") && string.IsNullOrWhiteSpace(when)) { throw new JsonException("schema: active skill preview output when must not be blank"); }
        _ = CustomString(fields, "formula", owner, true);
        _ = CustomString(fields, "kind", owner);
        if (fields.TryGetValue("headline", out var headline) && headline.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        { throw new JsonException("schema: active skill preview output headline must be a boolean"); }
        if (fields.TryGetValue("format", out var format))
        {
            if (format.ValueKind != JsonValueKind.String) { throw new JsonException("schema: active skill preview output format must be a string"); }
            if (PackJsonStrings.Decode(format) == "") { throw new JsonException("schema: active skill preview output format must not be empty"); }
        }
    }

    private static JsonException TypeError(JsonElement value, Type type, string field, bool structField = true, string? wireType = null)
    {
        string GoType(Type target)
        {
            if (target == typeof(string)) { return "string"; }
            if (target == typeof(bool)) { return "bool"; }
            if (target == typeof(long)) { return "int"; }
            if (target == typeof(double)) { return "float64"; }
            if (target == typeof(ImmutableArray<CurvePoint>)) { return "schema.Curve"; }
            if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(ImmutableArray<>)) { return "[]" + GoType(target.GenericTypeArguments[0]); }
            if (target.IsGenericType && target.GetGenericTypeDefinition() == typeof(ImmutableDictionary<,>)) { return "map[string]" + GoType(target.GenericTypeArguments[1]); }
            return "schema." + (target == typeof(StatModifier) ? "Modifier" : target == typeof(PackLayout) ? "Layout" :
                target == typeof(ArtworkRegistry) ? "Artwork" : target == typeof(ProgressionDefinition) ? "Progression" : target.Name.Replace("Definition", "Def", StringComparison.Ordinal));
        }
        var kind = value.ValueKind switch { JsonValueKind.String => "string", JsonValueKind.Number => "number", JsonValueKind.Array => "array", JsonValueKind.Object => "object", _ => "bool" };
        if (value.ValueKind == JsonValueKind.Number && (type == typeof(double) || type == typeof(long))) { kind += " " + value.GetRawText(); }
        if (field.Length == 0)
        {
            var root = type == typeof(NumericContextInputDefinition) || type == typeof(ActiveSkillPreviewOutputDefinition) || type == typeof(ActiveSkillModDefinition) ? "map[string]json.RawMessage" :
                type == typeof(ActiveSkillModSlotDefinition) ? "schema.alias" : HasCustomDecoder(type) ? "struct { *schema.alias }" : GoType(type);
            return new JsonException($"json: cannot unmarshal {kind} into Go value of type {root}");
        }
        return new JsonException($"json: cannot unmarshal {kind} into {(structField ? "Go struct field " : "")}{(field.StartsWith("alias.", StringComparison.Ordinal) ? "" : ".")}{field} of type {wireType ?? GoType(type)}");
    }

    internal static bool IsSlug(string value) => Regex.IsMatch(value, "^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);

    private sealed class ModifierValueConverter : JsonConverter<ModifierValue>
    {
        public override ModifierValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var number) && double.IsFinite(number))
            { return new(number); }
            if (reader.TokenType == JsonTokenType.String && PackJsonStrings.Decode(ref reader) is { Length: > 0 } formula)
            { return new(formula); }
            throw new JsonException(reader.TokenType == JsonTokenType.Null ? "schema: modifier value must be a number or a non-empty string, got null" :
                reader.TokenType == JsonTokenType.String ? "schema: modifier value string must not be empty" : "schema: modifier value must be a number or a non-empty string");
        }

        public override void Write(Utf8JsonWriter writer, ModifierValue value, JsonSerializerOptions options)
        {
            if (value.Formula is { } formula) { writer.WriteStringValue(formula); }
            else { writer.WriteNumberValue(value.Number); }
        }
    }

    private sealed class LegacyStringConverter : JsonConverter<string>
    {
        public override bool HandleNull => true;
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? "" : PackJsonStrings.Decode(ref reader)!;
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    private sealed class FiniteDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) { return 0; }
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetDouble(out var value) || !double.IsFinite(value))
            { throw new JsonException("Pack numbers must be finite doubles."); }
            return value;
        }
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    private sealed class LegacyLongConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? 0 : reader.GetInt64();
        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    private sealed class LegacyBooleanConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType != JsonTokenType.Null && reader.GetBoolean();
        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }

    private sealed class DictionaryConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsGenericType &&
            typeToConvert.GetGenericTypeDefinition() == typeof(ImmutableDictionary<,>) && typeToConvert.GenericTypeArguments[0] == typeof(string);
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(DictionaryConverter<>).MakeGenericType(typeToConvert.GenericTypeArguments[1]))!;
    }

    private sealed class DictionaryConverter<T> : JsonConverter<ImmutableDictionary<string, T>>
    {
        public override bool HandleNull => true;
        public override ImmutableDictionary<string, T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) { return ImmutableDictionary<string, T>.Empty; }
            return (JsonSerializer.Deserialize<Dictionary<string, T>>(ref reader, options) ?? [])
                .ToImmutableDictionary(StringComparer.Ordinal);
        }
        public override void Write(Utf8JsonWriter writer, ImmutableDictionary<string, T> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToDictionary(), options);
    }

    public sealed class ContextGateConverter : JsonConverter<ImmutableDictionary<string, string>>
    {
        public override ImmutableDictionary<string, string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            (JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options) ?? []).ToImmutableDictionary(StringComparer.Ordinal);
        public override void Write(Utf8JsonWriter writer, ImmutableDictionary<string, string> value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToDictionary(), options);
    }

    private sealed class CurvePointConverter : JsonConverter<CurvePoint>
    {
        public override CurvePoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Array))
            {
                var kind = document.RootElement.ValueKind switch { JsonValueKind.String => "string", JsonValueKind.Number => "number", JsonValueKind.Object => "object", _ => "bool" };
                throw new JsonException($"schema: curve point: json: cannot unmarshal {kind} into Go value of type []float64");
            }
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 2)
            { throw new JsonException($"schema: curve point must have exactly 2 elements, got {(document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.GetArrayLength() : 0)}"); }
            var values = document.RootElement.EnumerateArray().ToArray();
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i].ValueKind != JsonValueKind.Null && (values[i].ValueKind != JsonValueKind.Number || !values[i].TryGetDouble(out var number) || !double.IsFinite(number)))
                { throw new JsonException("schema: curve point: " + TypeError(values[i], typeof(double), i.ToString(System.Globalization.CultureInfo.InvariantCulture), false).Message); }
            }
            double Value(int i) => i >= values.Length || values[i].ValueKind == JsonValueKind.Null ? 0 : values[i].Deserialize<double>(options);
            return new(Value(0), Value(1));
        }
        public override void Write(Utf8JsonWriter writer, CurvePoint value, JsonSerializerOptions options)
        { writer.WriteStartArray(); writer.WriteNumberValue(value.X); writer.WriteNumberValue(value.Y); writer.WriteEndArray(); }
    }

    private sealed class ArrayConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ImmutableArray<>);
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(ArrayConverter<>).MakeGenericType(typeToConvert.GenericTypeArguments[0]))!;
    }

    private sealed class ArrayConverter<T> : JsonConverter<ImmutableArray<T>>
    {
        public override ImmutableArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) { return []; }
            var values = JsonSerializer.Deserialize<T[]>(ref reader, options) ?? [];
            // JSON null inside an array of Go structs produces the zero struct.
            if (typeof(T).Namespace == typeof(GamePack).Namespace && !typeof(T).IsValueType)
            {
                for (var i = 0; i < values.Length; i++)
                { if (values[i] is null) { values[i] = Activator.CreateInstance<T>(); } }
            }
            return values.ToImmutableArray();
        }
        public override void Write(Utf8JsonWriter writer, ImmutableArray<T> value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}
