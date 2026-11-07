namespace CharacterPlanner.Application.Commands;

/// <summary>Stable OperationError codes. Messages are for people and may change; codes may not.</summary>
public static class OperationErrorCodes
{
    // Reading operation JSON. Operation 0 means the batch as a whole.
    public const string Malformed = "operations-malformed";
    public const string NotArray = "operations-not-array";
    public const string NotObject = "operation-not-object";
    public const string KindMissing = "operation-kind-missing";
    public const string KindUnknown = "operation-kind-unknown";
    public const string KindRemoved = "operation-kind-removed";
    public const string FieldUnknown = "operation-field-unknown";
    public const string FieldNotAllowed = "operation-field-not-allowed";
    public const string FieldDuplicate = "operation-field-duplicate";
    public const string FieldMissing = "operation-field-missing";
    public const string FieldType = "operation-field-type";
    public const string FieldEmpty = "operation-field-empty";
    public const string FieldConflict = "operation-field-conflict";
    public const string ClearNotAllowed = "operation-clear-not-allowed";

    // Applying operations against the pack and the current state.
    public const string KindUnsupported = "operation-kind-unsupported";
    public const string ClearWithValue = "clear-with-value";
    public const string NameBlank = "name-blank";
    public const string UnknownSlot = "unknown-slot";
    public const string UnknownItem = "unknown-item";
    public const string ItemWrongSlot = "item-wrong-slot";
    public const string SlotEmpty = "slot-empty";
    public const string ItemNotEquipped = "item-not-equipped";
    public const string UnknownAttributeSlot = "unknown-attribute-slot";
    public const string UnknownAttribute = "unknown-attribute";
    public const string AttributeNotAllowed = "attribute-not-allowed";
    public const string AttributeRollInvalid = "attribute-roll-invalid";
    public const string UnknownModSlot = "unknown-mod-slot";
    public const string UnknownMod = "unknown-mod";
    public const string ModNotAllowed = "mod-not-allowed";
    public const string ModUnselected = "mod-unselected";
    public const string ModRollNotAllowed = "mod-roll-not-allowed";
    public const string ModRollInvalid = "mod-roll-invalid";
    public const string UnknownTalentSlot = "unknown-talent-slot";
    public const string UnknownTalent = "unknown-talent";
    public const string TalentNotAllowed = "talent-not-allowed";
    public const string UnknownSkillSlot = "unknown-skill-slot";
    public const string UnknownSkill = "unknown-skill";
    public const string SkillNotAllowed = "skill-not-allowed";
    public const string SkillUnselected = "skill-unselected";
    public const string SkillUpgradeUnavailable = "skill-upgrade-unavailable";
    public const string SkillUpgradeRankInvalid = "skill-upgrade-rank-invalid";
    public const string UnknownSkillModSlot = "unknown-skill-mod-slot";
    public const string UnknownSkillMod = "unknown-skill-mod";
    public const string SkillModRollInvalid = "skill-mod-roll-invalid";
    public const string UnknownSpecialization = "unknown-specialization";
    public const string SpecializationUnselected = "specialization-unselected";
    public const string UnknownSpecializationNode = "unknown-specialization-node";
    public const string UnknownAccountUpgrade = "unknown-account-upgrade";
    public const string AccountUpgradeRankInvalid = "account-upgrade-rank-invalid";
    public const string EquipmentUpgradeUnavailable = "equipment-upgrade-unavailable";
    public const string EquipmentUpgradeRankInvalid = "equipment-upgrade-rank-invalid";
    public const string PrototypeUnavailable = "prototype-unavailable";
    public const string PrototypeNotEligible = "prototype-not-eligible";
    public const string UnknownAugment = "unknown-augment";
    public const string PrototypeLevelWithoutAugment = "prototype-level-without-augment";
    public const string PrototypeLevelInvalid = "prototype-level-invalid";
    public const string UnknownContext = "unknown-context";
    public const string ContextNeedsValue = "context-needs-value";
    public const string ContextNeedsText = "context-needs-text";
    public const string ContextValueInvalid = "context-value-invalid";
    public const string PartyShareCodeInvalid = "party-share-code-invalid";
    public const string PartyShareCodeWrongPack = "party-share-code-wrong-pack";
    public const string PartyCodeWithArmor = "party-code-with-armor";
    public const string PartyMemberNameBlank = "party-member-name-blank";
    public const string PartyFull = "party-full";
    public const string PartyArmorInvalid = "party-armor-invalid";
    public const string PartyMemberNotHandEntered = "party-member-not-hand-entered";
    public const string UnknownPartyMember = "unknown-party-member";
    public const string UnknownAllyEffect = "unknown-ally-effect";
    public const string PartyEffectNotOffered = "party-effect-not-offered";
    public const string PartyEffectInvalid = "party-effect-invalid";
}
