using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CharacterPlanner.Desktop.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    /// <summary>
    /// A picker's choice for a stored id: the listed one, or, for an id the pack no longer has, an Unknown one added to
    /// the list through <paramref name="update"/> so the picker shows it rather than a blank.
    /// </summary>
    protected static Choice Listed(ImmutableArray<Choice> choices, string id, Action<ImmutableArray<Choice>> update)
    {
        var choice = Choice.Of(choices, id);
        if (!choices.Contains(choice)) { update(choices.Add(choice)); }
        return choice;
    }
}
