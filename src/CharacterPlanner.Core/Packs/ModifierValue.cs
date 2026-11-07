namespace CharacterPlanner.Core.Packs;

/// <summary>A modifier amount: a finite number or a non-empty expression.</summary>
public readonly record struct ModifierValue
{
    public double Number { get; }
    public string? Formula { get; }
    public bool IsExpression => Formula is not null;

    public ModifierValue(double number) { Number = number; }
    public ModifierValue(string formula) { Formula = formula; }
}
