using System.Globalization;
using CharacterPlanner.Application.Commands;
using CommunityToolkit.Mvvm.Input;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>A box whose typed text is a draft until committed: on Enter, when focus leaves it, or before a save.</summary>
public interface IDraftValue
{
    /// <summary>Commits the draft, if any; false when it couldn't be (not a number, out of range, or refused).</summary>
    bool CommitValue();
}

/// <summary>
/// A value typed in a box: an attribute or mod roll, an upgrade grade, an SHD Watch upgrade, an augment's level. The
/// text is a draft until committed, so an empty or half-typed field is never taken for zero. A draft survives other
/// edits; committing applies it or reports why it can't, and a refused value returns the box to the stored one.
/// </summary>
public abstract class DraftValueViewModel : ViewModelBase, IDraftValue
{
    /// <summary>The stored value as last shown; the box holds a draft while its text differs from this.</summary>
    private string shownText = "";
    /// <summary>The message this box last reported, so retyping a valid value can clear it.</summary>
    private string? error;

    protected DraftValueViewModel(EditorViewModel editor)
    {
        Editor = editor;
        CommitValueCommand = new RelayCommand(() => CommitValue());
    }

    protected EditorViewModel Editor { get; }
    public string ValueText { get; set => SetProperty(ref field, value ?? ""); } = "";
    public IRelayCommand CommitValueCommand { get; }

    /// <summary>Whether there is a value to commit at all (a roll needs a selected attribute or mod).</summary>
    protected virtual bool Committable => true;

    /// <summary>The operation that stores the typed text, or null with <paramref name="problem"/> saying why not.</summary>
    protected abstract BuildOperation? Parse(string text, out string problem);

    public bool CommitValue()
    {
        // Nothing typed since the stored value was shown: committing again (Enter twice, or focus leaving) is no edit,
        // and any error this box reported no longer applies.
        if (!Committable || ValueText == shownText)
        {
            if (error is not null && Editor.Message == error) { Editor.Message = null; }
            error = null;
            return true;
        }
        if (Parse(ValueText, out var problem) is not { } operation)
        {
            Editor.Message = error = problem;
            return false;
        }
        error = null;
        if (Editor.Apply(operation)) { return true; }
        ValueText = shownText;
        return false;
    }

    /// <summary>Shows the stored value, keeping a draft being typed unless the stored value itself changed.</summary>
    protected void ShowValue(string text)
    {
        if (ValueText == shownText || text != shownText) { ValueText = text; }
        shownText = text;
    }
}

/// <summary>A whole-number rank: an item's upgrade grade, an SHD Watch upgrade, or an augment's level.</summary>
public sealed class RankViewModel : DraftValueViewModel
{
    private readonly Func<long, BuildOperation> operation;

    internal RankViewModel(EditorViewModel editor, string key, string name, long min, long max, Func<long, BuildOperation> operation) : base(editor)
    {
        (this.operation, Key, Name, Min, Max) = (operation, key, name, min, max);
        Range = $"{min.ToString("#,0", CultureInfo.CurrentCulture)} – {max.ToString("#,0", CultureInfo.CurrentCulture)}";
        MaximumCommand = new RelayCommand(() => Editor.Apply(operation(Max)));
    }

    /// <summary>The rank on a slider, a whole number from Min to Max; a drag is one undo step.</summary>
    public double SliderValue
    {
        get => field;
        set
        {
            if (!SetProperty(ref field, value) || Editor.Refreshing) { return; }
            Editor.ApplyCoalescing($"rank {Key}", operation(Math.Clamp((long)Math.Round(value), Min, Max)));
        }
    }

    /// <summary>Sets the rank to its highest, as Go's Max.</summary>
    public IRelayCommand MaximumCommand { get; }

    /// <summary>What the rank belongs to (an upgrade or augment id), so a rank box is never reused for another.</summary>
    public string Key { get; }
    public string Name { get; }
    public long Min { get; }
    public long Max { get; }
    /// <summary>The legal range, as "min – max".</summary>
    public string Range { get; }
    /// <summary>The range as Go's upgrade section read it: "Rank (0–30)".</summary>
    public string RankRange => $"Rank ({Min.ToString("#,0", CultureInfo.CurrentCulture)}–{Max.ToString("#,0", CultureInfo.CurrentCulture)})";

    // Thousands separators are allowed, as the range shows them; a whole number has no decimal point to confuse.
    protected override BuildOperation? Parse(string text, out string problem)
    {
        problem = $"{Name} must be a whole number from {Range}";
        return long.TryParse(text.Trim(), NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var rank) && rank >= Min && rank <= Max
            ? operation(rank)
            : null;
    }

    internal void Refresh(long rank)
    {
        ShowValue(rank.ToString(CultureInfo.CurrentCulture));
        // Shown while the editor refreshes, so the slider's setter applies nothing back.
        SliderValue = rank;
    }
}
