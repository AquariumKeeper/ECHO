using System.Collections.Immutable;
using System.Globalization;

namespace CharacterPlanner.Application.Commands;

// One record per operation kind. JSON field names are noted where the record's name differs
// (prototype "id"/"value" are Augment/Level, party_member "value" is Armor).
// Clearable kinds set Clear and leave their value fields empty: "", null or 0.

/// <summary>The operation kinds as written in operation JSON, and the v1 kinds v2 no longer supports.</summary>
public static class OperationKinds
{
    public const string Name = "name";
    public const string Notes = "notes";
    public const string Equipment = "equipment";
    public const string Attribute = "attribute";
    public const string Mod = "mod";
    public const string ModRoll = "mod_roll";
    public const string Talent = "talent";
    public const string ActiveSkill = "active_skill";
    public const string SkillUpgrade = "skill_upgrade";
    public const string SkillMod = "skill_mod";
    public const string Specialization = "specialization";
    public const string SpecializationNode = "specialization_node";
    public const string AccountUpgrade = "account_upgrade";
    public const string EquipmentUpgrade = "equipment_upgrade";
    public const string Prototype = "prototype";
    public const string Context = "context";
    public const string PartyMember = "party_member";
    public const string PartyBuff = "party_buff";

    public static ImmutableArray<string> Supported { get; } =
    [
        Name, Notes, Equipment, Attribute, Mod, ModRoll, Talent, ActiveSkill, SkillUpgrade, SkillMod,
        Specialization, SpecializationNode, AccountUpgrade, EquipmentUpgrade, Prototype, Context, PartyMember, PartyBuff
    ];

    /// <summary>v1 kinds for features v2 removed (ADR 0002): allocation, level, passive skill nodes and scenarios.</summary>
    public static ImmutableArray<string> Removed { get; } = ["allocation", "level", "skill_node", "scenario"];
}

/// <summary>An operation that can instead clear what it sets ("clear": true in JSON, with only the kind's clear fields).</summary>
public abstract record ClearableOperation : BuildOperation
{
    public bool Clear { get; init; }

    /// <summary>Whether a value field is filled although Clear is set; the applier rejects such an operation.</summary>
    internal abstract bool HasValues { get; }

    /// <summary>The kind, then the filled fields in order, then "clear" for a clear.</summary>
    protected string Describe(params object?[] parts)
    {
        var words = parts.Select(Word).Where(word => word.Length > 0).Prepend(Kind);
        return string.Join(' ', Clear ? words.Append("clear") : words);
    }

    private static string Word(object? part) => part switch
    {
        null => "",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
        _ => part.ToString() ?? ""
    };
}

public sealed record NameOperation(string Text) : BuildOperation
{
    public override string Kind => OperationKinds.Name;
    // Free text stays out of history labels, as in the frozen history ("name", "notes").
    public override string Label => "name";
}

public sealed record NotesOperation(string Text) : BuildOperation
{
    public override string Kind => OperationKinds.Notes;
    public override string Label => "notes";
}

/// <summary>Equips item Id in equipment slot Slot; single-choice attribute, mod and talent slots are filled in.</summary>
public sealed record EquipmentOperation(string Slot, string Id) : ClearableOperation
{
    public override string Kind => OperationKinds.Equipment;
    public override string Label => Describe(Slot, Id);
    internal override bool HasValues => Id.Length > 0;
    public static EquipmentOperation Cleared(string slot) => new(slot, "") { Clear = true };
}

/// <summary>Rolls attribute Id in attribute slot Target of the item in Slot. A null Value is the highest legal roll.</summary>
public sealed record AttributeOperation(string Slot, string Target, string Id, double? Value = null) : ClearableOperation
{
    public override string Kind => OperationKinds.Attribute;
    public override string Label => Describe(Slot, Target, Id, Value);
    internal override bool HasValues => Id.Length > 0 || Value is not null;
    public static AttributeOperation Cleared(string slot, string target) => new(slot, target, "") { Clear = true };
}

/// <summary>Selects mod Id in mod slot Target; a rolled mod starts at its highest legal roll.</summary>
public sealed record ModOperation(string Slot, string Target, string Id) : ClearableOperation
{
    public override string Kind => OperationKinds.Mod;
    public override string Label => Describe(Slot, Target, Id);
    internal override bool HasValues => Id.Length > 0;
    public static ModOperation Cleared(string slot, string target) => new(slot, target, "") { Clear = true };
}

/// <summary>Sets the selected mod's roll in mod slot Target. A null Value is the highest legal roll.</summary>
public sealed record ModRollOperation(string Slot, string Target, double? Value = null) : ClearableOperation
{
    public override string Kind => OperationKinds.ModRoll;
    public override string Label => Describe(Slot, Target, Value);
    internal override bool HasValues => Value is not null;
    public static ModRollOperation Cleared(string slot, string target) => new(slot, target) { Clear = true };
}

public sealed record TalentOperation(string Slot, string Target, string Id) : ClearableOperation
{
    public override string Kind => OperationKinds.Talent;
    public override string Label => Describe(Slot, Target, Id);
    internal override bool HasValues => Id.Length > 0;
    public static TalentOperation Cleared(string slot, string target) => new(slot, target, "") { Clear = true };
}

public sealed record ActiveSkillOperation(string Slot, string Id) : ClearableOperation
{
    public override string Kind => OperationKinds.ActiveSkill;
    public override string Label => Describe(Slot, Id);
    internal override bool HasValues => Id.Length > 0;
    public static ActiveSkillOperation Cleared(string slot) => new(slot, "") { Clear = true };
}

/// <summary>Sets the Expertise rank of the skill selected in Slot; rank 0 also clears.</summary>
public sealed record SkillUpgradeOperation(string Slot, long Rank) : ClearableOperation
{
    public override string Kind => OperationKinds.SkillUpgrade;
    public override string Label => Clear ? Describe(Slot) : Describe(Slot, Rank);
    internal override bool HasValues => Rank != 0;
    public static SkillUpgradeOperation Cleared(string slot) => new(slot, 0) { Clear = true };
}

/// <summary>Selects skill mod Id in the selected skill's mod slot Target. A null Value is the highest legal roll.</summary>
public sealed record SkillModOperation(string Slot, string Target, string Id, double? Value = null) : ClearableOperation
{
    public override string Kind => OperationKinds.SkillMod;
    public override string Label => Describe(Slot, Target, Id, Value);
    internal override bool HasValues => Id.Length > 0 || Value is not null;
    public static SkillModOperation Cleared(string slot, string target) => new(slot, target, "") { Clear = true };
}

/// <summary>Selects a specialization; a different one (or a clear) deselects every node.</summary>
public sealed record SpecializationOperation(string Id) : ClearableOperation
{
    public override string Kind => OperationKinds.Specialization;
    public override string Label => Describe(Id);
    internal override bool HasValues => Id.Length > 0;
    public static SpecializationOperation Cleared() => new("") { Clear = true };
}

public sealed record SpecializationNodeOperation(string Id, bool Enabled) : BuildOperation
{
    public override string Kind => OperationKinds.SpecializationNode;
    public override string Label => $"{Kind} {Id} {(Enabled ? "on" : "off")}";
}

/// <summary>Sets an account upgrade (SHD Watch) rank; rank 0 clears.</summary>
public sealed record AccountUpgradeOperation(string Id, long Rank) : BuildOperation
{
    public override string Kind => OperationKinds.AccountUpgrade;
    public override string Label => FormattableString.Invariant($"{Kind} {Id} {Rank}");
}

/// <summary>Sets the Expertise rank of item Id, which must be equipped in Slot; rank 0 clears.</summary>
public sealed record EquipmentUpgradeOperation(string Slot, string Id, long Rank) : BuildOperation
{
    public override string Kind => OperationKinds.EquipmentUpgrade;
    public override string Label => FormattableString.Invariant($"{Kind} {Slot} {Id} {Rank}");
}

/// <summary>
/// Makes the item in Slot Prototype quality with augment Augment (JSON "id") at level Level (JSON "value").
/// No augment means level 0; an augment without a level takes its top level. Rolls move into the new range.
/// </summary>
public sealed record PrototypeOperation(string Slot, string? Augment = null, long? Level = null) : ClearableOperation
{
    public override string Kind => OperationKinds.Prototype;
    public override string Label => Describe(Slot, Augment, Level);
    internal override bool HasValues => Augment is not null || Level is not null;
    public static PrototypeOperation Cleared(string slot) => new(slot) { Clear = true };
}

/// <summary>Sets view assumption Id: Value for a numeric input, Text for a selector or weapon_slot. Clear restores the default.</summary>
public sealed record ContextOperation(string Id, double? Value = null, string? Text = null) : ClearableOperation
{
    public override string Kind => OperationKinds.Context;
    public override string Label => Describe(Id, Value, Text);
    internal override bool HasValues => Value is not null || Text is not null;
    public static ContextOperation Cleared(string id) => new(id) { Clear = true };
}

/// <summary>
/// Adds or updates party member Target: from share code Text, or by hand with giver Armor (JSON "value", default 0).
/// With neither on an existing member, re-reads it against the current pack. Clear removes the member.
/// </summary>
public sealed record PartyMemberOperation(string Target, string? Text = null, double? Armor = null) : ClearableOperation
{
    public override string Kind => OperationKinds.PartyMember;
    public override string Label => Describe(Target, Text is null ? null : "code", Armor);
    internal override bool HasValues => Text is not null || Armor is not null;
    public static PartyMemberOperation Cleared(string target) => new(target) { Clear = true };
}

/// <summary>Ticks party member Target's ally effect Id on or off; a hand-entered member gains or loses the effect.</summary>
public sealed record PartyBuffOperation(string Target, string Id, bool Enabled) : BuildOperation
{
    public override string Kind => OperationKinds.PartyBuff;
    public override string Label => $"{Kind} {Target} {Id} {(Enabled ? "on" : "off")}";
}
