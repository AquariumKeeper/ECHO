namespace CharacterPlanner.Core.Packs;

/// <summary>
/// How far a value may sit from its step grid and still count as on it (L6). Rolls and numeric assumptions
/// have long used different tolerances, and the frozen roll and assumption contracts pass with these values,
/// so each is named once here rather than repeated where it's checked.
/// </summary>
internal static class StepTolerance
{
    /// <summary>Attribute, mod and skill-mod rolls, and Prototype ranges (<c>AttributeRollBounds</c>).</summary>
    public const double Roll = 1e-6;

    /// <summary>Numeric assumptions (<c>NumericContextInputDefinition</c>), and counting their steps.</summary>
    public const double NumericInput = 1e-9;
}
