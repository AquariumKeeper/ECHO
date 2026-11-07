using System.Collections.Immutable;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Packs;

namespace CharacterPlanner.Core.Engine;

/// <summary>A roll's legal range. The resolver and the edit commands share these rules, so an accepted roll never resolves as invalid.</summary>
public readonly record struct AttributeRollBounds(double Min, double Max, double? Step)
{
    public bool Accepts(double value) => double.IsFinite(value) && value >= Min && value <= Max && (Step is not { } step || OnStep(value, step));

    /// <summary>The highest legal roll: the maximum itself when it is on a step.</summary>
    public double Highest()
    {
        if (Step is not { } step || step <= 0 || Max <= Min || OnStep(Max, step)) { return Max; }
        return GridPoint(Math.Floor((Max - Min) / step), step);
    }

    /// <summary>
    /// An accepted value moved exactly onto its step (Accepts allows a small tolerance), so the
    /// stored roll is the grid point the resolver and the UI expect.
    /// </summary>
    public double Snap(double value)
    {
        if (Step is not { } step || step <= 0 || !Accepts(value)) { return value; }
        return Math.Clamp(GridPoint(Math.Round((value - Min) / step), step), Min, Max);
    }

    // Round away the binary noise of min + n * step (0.1 * 3 is 0.30000000000000004), unless
    // rounding would itself leave the grid (steps finer than the rounding).
    private double GridPoint(double steps, double step)
    {
        var exact = Min + steps * step;
        var rounded = Math.Round(exact, 12);
        return OnStep(rounded, step) ? rounded : exact;
    }

    private bool OnStep(double value, double step) => Math.Abs((value - Min) / step - Math.Round((value - Min) / step)) < StepTolerance.Roll;
}

public static class PrototypeRules
{
    public static bool Eligible(GamePack pack, ItemDefinition item, string slotId) =>
        pack.Prototype is { } prototype && prototype.Slots.Contains(slotId) && !item.Tags.Any(prototype.ExcludeTags.Contains);

    // Augment identity/level errors are diagnosed separately. They do not switch
    // an eligible, item-bound selection back to ordinary attribute ranges.
    public static bool IsPrototype(GamePack pack, BuildSnapshot build, string slotId, ItemDefinition item) =>
        build.Prototypes.TryGetValue(slotId, out var selection) && selection.ItemId == item.ID && Eligible(pack, item, slotId);

    public static AttributeRollBounds RollBounds(AttributeDefinition attribute, ItemAttributeChoice choice, bool prototype)
    {
        var min = choice.Min ?? attribute.Min;
        var max = choice.Max ?? attribute.Max;
        if (prototype && attribute.PrototypeMax is { } prototypeMax)
        { min = attribute.PrototypeMin ?? max; max = prototypeMax; }
        return new(min, max, choice.Step ?? attribute.Step);
    }

    public static bool IsNormalized(GamePack pack, ImmutableDictionary<string, double> numeric) =>
        pack.Prototype is { NormalizeInput.Length: > 0 } prototype && numeric.GetValueOrDefault(prototype.NormalizeInput) == 1;
}
