using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Codes = CharacterPlanner.Application.Commands.OperationErrorCodes;
using Kinds = CharacterPlanner.Application.Commands.OperationKinds;

namespace CharacterPlanner.Application.Commands;

/// <summary>The operations read from JSON, or every operation's first error. Operation 0 is the batch as a whole.</summary>
public sealed record OperationReadResult(ImmutableArray<BuildOperation> Operations, ImmutableArray<OperationError> Errors)
{
    public bool Succeeded => Errors.IsEmpty;
}

/// <summary>
/// Reads operation JSON: an array of objects, each a "kind" plus that kind's fields. Field names are v1's
/// (slot, target, id, value, rank, enabled, text), and "clear": true takes only the kind's clear fields.
/// Reading is strict: unknown, duplicate, missing and mistyped fields are errors. Pack checks happen when applying.
/// </summary>
public static class OperationJson
{
    private const string Kind = "kind", Slot = "slot", Target = "target", Id = "id", Value = "value", Rank = "rank", Enabled = "enabled", Text = "text", Clear = "clear";
    private static readonly ImmutableHashSet<string> Vocabulary = [Kind, Slot, Target, Id, Value, Rank, Enabled, Text, Clear];

    /// <param name="Clearable">Null when the kind has no clear form; otherwise the fields a clear takes.</param>
    private sealed record Shape(string[] Required, string[] Optional, string[]? Clearable, Func<Fields, BuildOperation> Read, Func<Fields, BuildOperation>? ReadClear = null);

    private static readonly ImmutableDictionary<string, Shape> Shapes = new Dictionary<string, Shape>(StringComparer.Ordinal)
    {
        [Kinds.Name] = new([Text], [], null, f => new NameOperation(f.String(Text))),
        [Kinds.Notes] = new([Text], [], null, f => new NotesOperation(f.String(Text, allowEmpty: true))),
        [Kinds.Equipment] = new([Slot, Id], [], [Slot], f => new EquipmentOperation(f.String(Slot), f.String(Id)), f => EquipmentOperation.Cleared(f.String(Slot))),
        [Kinds.Attribute] = new([Slot, Target, Id], [Value], [Slot, Target],
            f => new AttributeOperation(f.String(Slot), f.String(Target), f.String(Id), f.OptionalNumber(Value)), f => AttributeOperation.Cleared(f.String(Slot), f.String(Target))),
        [Kinds.Mod] = new([Slot, Target, Id], [], [Slot, Target], f => new ModOperation(f.String(Slot), f.String(Target), f.String(Id)), f => ModOperation.Cleared(f.String(Slot), f.String(Target))),
        [Kinds.ModRoll] = new([Slot, Target], [Value], [Slot, Target],
            f => new ModRollOperation(f.String(Slot), f.String(Target), f.OptionalNumber(Value)), f => ModRollOperation.Cleared(f.String(Slot), f.String(Target))),
        [Kinds.Talent] = new([Slot, Target, Id], [], [Slot, Target], f => new TalentOperation(f.String(Slot), f.String(Target), f.String(Id)), f => TalentOperation.Cleared(f.String(Slot), f.String(Target))),
        [Kinds.ActiveSkill] = new([Slot, Id], [], [Slot], f => new ActiveSkillOperation(f.String(Slot), f.String(Id)), f => ActiveSkillOperation.Cleared(f.String(Slot))),
        [Kinds.SkillUpgrade] = new([Slot, Rank], [], [Slot], f => new SkillUpgradeOperation(f.String(Slot), f.Integer(Rank)), f => SkillUpgradeOperation.Cleared(f.String(Slot))),
        [Kinds.SkillMod] = new([Slot, Target, Id], [Value], [Slot, Target],
            f => new SkillModOperation(f.String(Slot), f.String(Target), f.String(Id), f.OptionalNumber(Value)), f => SkillModOperation.Cleared(f.String(Slot), f.String(Target))),
        [Kinds.Specialization] = new([Id], [], [], f => new SpecializationOperation(f.String(Id)), _ => SpecializationOperation.Cleared()),
        [Kinds.SpecializationNode] = new([Id, Enabled], [], null, f => new SpecializationNodeOperation(f.String(Id), f.Boolean(Enabled))),
        [Kinds.AccountUpgrade] = new([Id, Rank], [], null, f => new AccountUpgradeOperation(f.String(Id), f.Integer(Rank))),
        [Kinds.EquipmentUpgrade] = new([Slot, Id, Rank], [], null, f => new EquipmentUpgradeOperation(f.String(Slot), f.String(Id), f.Integer(Rank))),
        [Kinds.Prototype] = new([Slot], [Id, Value], [Slot],
            f => new PrototypeOperation(f.String(Slot), f.OptionalString(Id), f.OptionalInteger(Value)), f => PrototypeOperation.Cleared(f.String(Slot))),
        [Kinds.Context] = new([Id], [Value, Text], [Id], f =>
        {
            f.ExactlyOne(Value, Text);
            return new ContextOperation(f.String(Id), f.OptionalNumber(Value), f.OptionalString(Text));
        }, f => ContextOperation.Cleared(f.String(Id))),
        [Kinds.PartyMember] = new([Target], [Text, Value], [Target], f =>
        {
            f.AtMostOne(Text, Value);
            return new PartyMemberOperation(f.String(Target), f.OptionalString(Text), f.OptionalNumber(Value));
        }, f => PartyMemberOperation.Cleared(f.String(Target))),
        [Kinds.PartyBuff] = new([Target, Id, Enabled], [], null, f => new PartyBuffOperation(f.String(Target), f.String(Id), f.Boolean(Enabled)))
    }.ToImmutableDictionary(StringComparer.Ordinal);

    public static OperationReadResult Read(string json) => Read(Encoding.UTF8.GetBytes(json));

    public static OperationReadResult Read(ReadOnlyMemory<byte> json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException error) { return Failed(new(0, "", Codes.Malformed, $"operations are not valid JSON: {error.Message}")); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            { return Failed(new(0, "", Codes.NotArray, "operations must be a JSON array of objects")); }
            var operations = ImmutableArray.CreateBuilder<BuildOperation>();
            var errors = ImmutableArray.CreateBuilder<OperationError>();
            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                index++;
                var kind = "";
                try { operations.Add(ReadOne(element, ref kind)); }
                catch (OperationJsonException error) { errors.Add(new(index, kind, error.Code, $"operation {index}{(kind.Length > 0 ? $" ({kind})" : "")}: {error.Message}")); }
                // GetString and property names throw on lone-surrogate escapes and invalid UTF-8.
                catch (InvalidOperationException)
                { errors.Add(new(index, kind, Codes.FieldType, $"operation {index}{(kind.Length > 0 ? $" ({kind})" : "")}: contains text that is not valid Unicode")); }
            }
            return errors.Count > 0 ? new([], errors.ToImmutable()) : new(operations.ToImmutable(), []);
        }
    }

    private static OperationReadResult Failed(OperationError error) => new([], [error]);

    private static BuildOperation ReadOne(JsonElement element, ref string kind)
    {
        if (element.ValueKind != JsonValueKind.Object) { throw new OperationJsonException(Codes.NotObject, "must be a JSON object"); }
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!Vocabulary.Contains(property.Name))
            { throw new OperationJsonException(Codes.FieldUnknown, $"unknown field \"{property.Name}\" (fields are {string.Join(", ", Vocabulary.Order(StringComparer.Ordinal))})"); }
            if (!fields.TryAdd(property.Name, property.Value)) { throw new OperationJsonException(Codes.FieldDuplicate, $"field \"{property.Name}\" appears twice"); }
        }
        if (!fields.TryGetValue(Kind, out var kindValue)) { throw new OperationJsonException(Codes.KindMissing, "needs a \"kind\""); }
        if (kindValue.ValueKind != JsonValueKind.String) { throw new OperationJsonException(Codes.FieldType, "\"kind\" must be a string"); }
        var name = kindValue.GetString()!;
        if (Kinds.Removed.Contains(name))
        { kind = name; throw new OperationJsonException(Codes.KindRemoved, $"\"{name}\" belongs to a feature Division 2 doesn't have; it is no longer supported"); }
        if (!Shapes.TryGetValue(name, out var shape)) { throw new OperationJsonException(Codes.KindUnknown, $"unknown kind \"{name}\""); }
        kind = name;
        var reader = new Fields(fields);
        var clear = reader.OptionalBoolean(Clear) ?? false;
        if (fields.ContainsKey(Clear) && shape.Clearable is null) { throw new OperationJsonException(Codes.ClearNotAllowed, $"\"{name}\" can't be cleared"); }
        var allowed = clear ? shape.Clearable! : [.. shape.Required, .. shape.Optional];
        foreach (var field in fields.Keys.Where(field => field is not (Kind or Clear) && !allowed.Contains(field)))
        {
            throw new OperationJsonException(Codes.FieldNotAllowed, clear
                ? $"a clear takes only {Describe(shape.Clearable!)}; remove \"{field}\""
                : $"\"{name}\" has no field \"{field}\"");
        }
        foreach (var field in clear ? shape.Clearable! : shape.Required)
        {
            if (!fields.ContainsKey(field)) { throw new OperationJsonException(Codes.FieldMissing, $"needs \"{field}\""); }
        }
        return clear ? shape.ReadClear!(reader) : shape.Read(reader);
    }

    private static string Describe(string[] fields) => fields.Length == 0 ? "no other fields" : string.Join(" and ", fields.Select(field => $"\"{field}\""));

    private sealed class OperationJsonException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private sealed class Fields(Dictionary<string, JsonElement> fields)
    {
        public string String(string name, bool allowEmpty = false) => OptionalString(name, allowEmpty)!;

        public string? OptionalString(string name, bool allowEmpty = false)
        {
            if (!fields.TryGetValue(name, out var value)) { return null; }
            if (value.ValueKind != JsonValueKind.String) { throw Type(name, "a string"); }
            var text = value.GetString()!;
            return text.Length == 0 && !allowEmpty ? throw new OperationJsonException(Codes.FieldEmpty, $"\"{name}\" must not be empty") : text;
        }

        public double? OptionalNumber(string name)
        {
            if (!fields.TryGetValue(name, out var value)) { return null; }
            return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : throw Type(name, "a finite number");
        }

        public long Integer(string name) => OptionalInteger(name)!.Value;

        public long? OptionalInteger(string name)
        {
            if (!fields.TryGetValue(name, out var value)) { return null; }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer)) { return integer; }
            // 10.0 is still the integer 10; 10.5 is not.
            return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && number == Math.Floor(number) &&
                Math.Abs(number) < 9e15 ? (long)number : throw Type(name, "an integer");
        }

        public bool Boolean(string name) => OptionalBoolean(name)!.Value;

        public bool? OptionalBoolean(string name)
        {
            if (!fields.TryGetValue(name, out var value)) { return null; }
            return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Type(name, "true or false");
        }

        public void ExactlyOne(string first, string second)
        {
            AtMostOne(first, second);
            if (!fields.ContainsKey(first) && !fields.ContainsKey(second)) { throw new OperationJsonException(Codes.FieldMissing, $"needs \"{first}\" or \"{second}\""); }
        }

        public void AtMostOne(string first, string second)
        {
            if (fields.ContainsKey(first) && fields.ContainsKey(second)) { throw new OperationJsonException(Codes.FieldConflict, $"takes \"{first}\" or \"{second}\", not both"); }
        }

        private static OperationJsonException Type(string name, string expected) => new(Codes.FieldType, $"\"{name}\" must be {expected}");
    }
}
