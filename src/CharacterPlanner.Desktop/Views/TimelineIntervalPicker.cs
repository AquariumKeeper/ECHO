using Avalonia.Controls;

namespace CharacterPlanner.Desktop.Views;

/// <summary>A nonempty timeline always has one selected interval, including during binding updates.</summary>
public sealed class TimelineIntervalPicker : ComboBox
{
    protected override Type StyleKeyOverride => typeof(ComboBox);
    public TimelineIntervalPicker() => SelectionMode = SelectionMode.Single | SelectionMode.AlwaysSelected;
}
