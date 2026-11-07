namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>What the editor asks of the window it is shown in: whether to discard changes, and the dialogs it opens.</summary>
public interface IEditorHost
{
    /// <summary>Asked before New or Open would discard unsaved changes; true discards them.</summary>
    Task<bool> ConfirmDiscardAsync();

    /// <summary>Asks a yes/no question; true only for <paramref name="confirm"/>.</summary>
    Task<bool> ConfirmAsync(string title, string question, string confirm);

    /// <summary>
    /// Asks for a line of text, starting from <paramref name="text"/>, with <paramref name="action"/> to accept it; null when cancelled. <paramref name="check"/> gives
    /// why a value won't do (shown, and the question stays open) or null when it will.
    /// </summary>
    Task<string?> AskTextAsync(string title, string label, string action, string text, Func<string, string?> check);

    /// <summary>The summary's All stats: shows the Stats tab, as Go's.</summary>
    void ShowAllStats();

    /// <summary>Shows Compare talents while its changes are worked out; the host loads the model and disposes it.</summary>
    void CompareTalents(TalentCompareViewModel compare);
}
