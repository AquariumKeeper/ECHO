using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Application.Commands;

/// <summary>Structural weapon-slot choices in pack order; active effects and engine eligibility are separate checks.</summary>
public static class EquippedWeaponSlots
{
    public static ImmutableArray<SlotDefinition> Read(GamePack pack, BuildSnapshot build)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(build);
        return [.. pack.Slots.Where(slot => slot.Weapon && build.Equipment.GetValueOrDefault(slot.ID, "").Length > 0)];
    }
}
