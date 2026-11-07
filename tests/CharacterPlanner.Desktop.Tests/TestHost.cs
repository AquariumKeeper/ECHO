using CharacterPlanner.Desktop.ViewModels;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>A window's part for view-model tests: answers the discard question and records Compare talents.</summary>
internal sealed class TestHost(Func<Task<bool>>? answer = null) : IEditorHost
{
    public Action<TalentCompareViewModel>? Compared { get; init; }

    /// <summary>The answer to every yes/no question (false by default), and the questions asked.</summary>
    public bool Confirms { get; init; }
    public List<string> Questions { get; } = [];

    /// <summary>The text every question for text answers (null cancels), checked as the dialog would.</summary>
    public string? Answer { get; init; }

    private Task<bool> AnswerAsync() => answer?.Invoke() ?? Task.FromResult(Confirms);

    public Task<bool> ConfirmDiscardAsync() => AnswerAsync();

    public Task<bool> ConfirmAsync(string title, string question, string confirm)
    {
        Questions.Add(question);
        return AnswerAsync();
    }

    public Task<string?> AskTextAsync(string title, string label, string action, string text, Func<string, string?> check) =>
        Task.FromResult(Answer is not null && check(Answer) is null ? Answer : null);

    public void ShowAllStats() { }

    public void CompareTalents(TalentCompareViewModel compare) => Compared?.Invoke(compare);
}
