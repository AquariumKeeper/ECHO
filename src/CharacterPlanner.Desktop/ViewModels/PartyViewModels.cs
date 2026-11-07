using System.Collections.Immutable;
using System.Globalization;
using CharacterPlanner.Application.Commands;
using CharacterPlanner.Application.Library;
using CharacterPlanner.Core.Builds;
using CharacterPlanner.Core.Engine;
using CharacterPlanner.Core.Packs;
using CommunityToolkit.Mvvm.Input;

namespace CharacterPlanner.Desktop.ViewModels;

/// <summary>
/// The Party tab (F44), as the Go app's <c>party.go</c>: up to three teammates, from a share code, a saved build or
/// entered by hand, whose buffs are off until ticked. The same buff from two members applies once, the strongest.
/// Members and their buffs are kept across refreshes, so a value being typed survives another member's edit.
/// </summary>
public sealed class PartyViewModel : ViewModelBase
{
    public const string Hint = "Teammates' buffs. Each is off until ticked, and the same buff from two members applies once, the strongest. " +
        "Values come from the teammate's build when added; Re-read updates them. Saved with the build in the library, not in share codes.";
    public const string Empty = "No party members. Add up to three teammates.";
    public const string NamePlaceholder = "Name (defaults to the build's name)";
    public const string CodePlaceholder = "Paste the teammate's share code";
    public const string LibraryPlaceholder = "Choose a saved build";
    public const string HandNamePlaceholder = "Name";
    public const string HandArmorPlaceholder = "Giver armor, for buffs that share it (optional)";

    private readonly EditorViewModel editor;
    private ImmutableArray<PartyMemberViewModel> members = [];

    internal PartyViewModel(EditorViewModel editor)
    {
        this.editor = editor;
        // An effect's definition and the talent giving it, by effect id: the last talent listing it, as the resolver reads them.
        var definitions = new Dictionary<string, (AllyEffect Effect, string Talent)>(StringComparer.Ordinal);
        foreach (var talent in editor.Pack.Talents)
        {
            foreach (var effect in talent.AllyEffects) { definitions[effect.ID] = (effect, talent.Name); }
        }
        Definitions = definitions.ToImmutableDictionary(StringComparer.Ordinal);
    }

    internal EditorViewModel Editor => editor;
    internal ImmutableDictionary<string, (AllyEffect Effect, string Talent)> Definitions { get; }

    public ImmutableArray<PartyMemberViewModel> Members { get => members; private set { if (SetProperty(ref members, value)) { OnPropertyChanged(nameof(IsEmpty)); } } }
    public bool IsEmpty => Members.IsEmpty;
    /// <summary>A party holds at most three teammates; the add buttons are off while it's full.</summary>
    public bool CanAdd { get; private set => SetProperty(ref field, value); } = true;

    internal void Refresh(BuildSnapshot build)
    {
        var party = build.Party.IsDefault ? [] : build.Party;
        var applied = PartyEffectSelection.Select(editor.Pack, build);
        var known = Members.ToDictionary(member => PartyRules.Key(member.Name), StringComparer.Ordinal);
        var shown = party.Select(member => known.GetValueOrDefault(PartyRules.Key(member.Name)) ?? new PartyMemberViewModel(this)).ToImmutableArray();
        for (var index = 0; index < party.Length; index++) { shown[index].Refresh(party[index], applied); }
        if (!shown.SequenceEqual(Members)) { Members = shown; }
        CanAdd = party.Length < PartyRules.MaxMembers;
    }

    /// <summary>Adds a teammate from a share code in either format; with no name, the build's own. Null, or why not.</summary>
    public string? AddFromCode(string? name, string? code)
    {
        var text = (code ?? "").Trim();
        var member = (name ?? "").Trim();
        if (member.Length == 0)
        {
            try { member = editor.ReadShareCode(text).Build.Meta.Name.Trim(); }
            catch (ShareCodeException exception) { return exception.Message; }
        }
        return Add(member, new PartyMemberOperation(member, Text: text));
    }

    /// <summary>The saved builds to choose from, labelled by name (and id where a name is blank or repeated), as Go's.</summary>
    public async Task<ImmutableArray<Choice>> LibraryChoicesAsync()
    {
        var entries = (await editor.Library.ListAsync()).Where(entry => entry.Problem is null).ToList();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        return [.. entries.Select(entry =>
        {
            var label = entry.Name;
            if (label.Length == 0 || !labels.Add(label)) { label = $"{entry.Name} ({entry.Id})"; labels.Add(label); }
            return new Choice(entry.Id, label);
        })];
    }

    /// <summary>Adds a teammate from a saved build, as its share code; with no name, the build's own. Null, or why not.</summary>
    public async Task<string?> AddFromLibraryAsync(Choice? saved, string? name)
    {
        if (saved is null || saved.Id.Length == 0) { return "choose a saved build"; }
        LibraryBuild record;
        try { record = await editor.Library.LoadAsync(saved.Id); }
        catch (LibraryException exception) { return exception.Message; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return $"The library couldn't be read: {exception.Message}"; }
        if (record.Build.PackId != editor.Pack.ID) { return $"build \"{saved.Id}\" is for another pack"; }
        string code;
        try { code = ShareCode.Encode(record.Build with { Party = [] }); }
        catch (ArgumentException exception) { return $"That build can't be shared: {exception.Message}"; }
        var member = (name ?? "").Trim();
        if (member.Length == 0) { member = record.Build.Meta.Name.Trim(); }
        return Add(member, new PartyMemberOperation(member, Text: code));
    }

    /// <summary>Adds a teammate whose buffs are entered by hand, with the giver armor armor-share buffs use. Null, or why not.</summary>
    public string? AddByHand(string? name, string? armor)
    {
        var value = 0.0;
        if ((armor ?? "").Trim() is { Length: > 0 } text)
        {
            if (PartyArmorViewModel.Parse(text) is not { } parsed) { return PartyArmorViewModel.Problem; }
            value = parsed;
        }
        var member = (name ?? "").Trim();
        return Add(member, new PartyMemberOperation(member, Armor: value));
    }

    /// <summary>Adds a member as one edit. The operation would update a member of the same name, so that's refused here.</summary>
    private string? Add(string name, PartyMemberOperation operation)
    {
        var party = editor.Session.Snapshot.State.Build.Party;
        if (name.Length > 0 && PartyRules.Problem([.. party.IsDefault ? [] : party, new PartyMember(name, "", 0, [])]) is { } problem) { return problem; }
        if (editor.Apply(operation)) { return null; }
        var refused = editor.Message ?? "";
        editor.Message = null;
        // In the dialog the operation is understood: "operation 1 (party_member): " would only get in the way.
        var prefix = $"operation 1 ({operation.Kind}): ";
        return refused.StartsWith(prefix, StringComparison.Ordinal) ? refused[prefix.Length..] : refused;
    }
}

/// <summary>A teammate's card: its name and source, Re-read and Remove, and its buffs; one entered by hand has its armor and a buff chooser.</summary>
public sealed class PartyMemberViewModel : ViewModelBase
{
    public const string FromCode = "From a share code";
    public const string ByHand = "Entered by hand";
    public const string ArmorHint = "Giver armor, for buffs that share it (press Enter)";
    public const string BuffPlaceholder = "Add a buff...";
    public const string NoBuffs = "This build grants no modeled party buffs.";

    private readonly PartyViewModel party;
    private string name = "";
    private string source = "";
    private Choice? selectedBuff;

    internal PartyMemberViewModel(PartyViewModel party)
    {
        this.party = party;
        Armor = new PartyArmorViewModel(party.Editor, this);
        RereadCommand = new RelayCommand(() => party.Editor.Apply(new PartyMemberOperation(Name)));
        RemoveCommand = new RelayCommand(() => party.Editor.Apply(PartyMemberOperation.Cleared(Name)));
    }

    public string Name { get => name; private set => SetProperty(ref name, value); }
    /// <summary>"From a share code" or "Entered by hand".</summary>
    public string Source { get => source; private set => SetProperty(ref source, value); }
    public bool IsHandEntered { get; private set => SetProperty(ref field, value); }
    /// <summary>Re-read shows for a member from a code, or one entered by hand with buffs to re-value.</summary>
    public bool CanReread { get; private set => SetProperty(ref field, value); }
    public bool GrantsNothing { get; private set => SetProperty(ref field, value); }
    /// <summary>The giver armor of a member entered by hand, committed on Enter.</summary>
    public PartyArmorViewModel Armor { get; }
    /// <summary>The buffs a member entered by hand can still be given, by name (and talent, where that differs).</summary>
    public ImmutableArray<Choice> BuffChoices { get; private set => SetProperty(ref field, value); } = [];
    public ImmutableArray<PartyEffectViewModel> Effects { get; private set => SetProperty(ref field, value); } = [];
    public IRelayCommand RereadCommand { get; }
    public IRelayCommand RemoveCommand { get; }

    /// <summary>Choosing a buff gives it to the member, ticked; the chooser then clears.</summary>
    public Choice? SelectedBuff
    {
        get => selectedBuff;
        set
        {
            if (!SetProperty(ref selectedBuff, value) || value is null || party.Editor.Refreshing) { return; }
            party.Editor.Apply(new PartyBuffOperation(Name, value.Id, true));
            SetProperty(ref selectedBuff, null, nameof(SelectedBuff));
        }
    }

    internal void Refresh(PartyMember member, ImmutableArray<PartyBuff> applied)
    {
        var definitions = party.Definitions;
        Name = member.Name;
        IsHandEntered = member.Code.Length == 0;
        Source = IsHandEntered ? ByHand : FromCode;
        CanReread = !IsHandEntered || !member.Effects.IsEmpty;
        GrantsNothing = !IsHandEntered && member.Effects.IsEmpty;
        Armor.Refresh(member.Armor);
        BuffChoices = IsHandEntered
            ? [.. definitions.Where(pair => !member.Effects.Any(effect => effect.Id == pair.Key))
                .Select(pair => new Choice(pair.Key, pair.Value.Effect.Name != pair.Value.Talent && !pair.Value.Effect.Name.StartsWith(pair.Value.Talent, StringComparison.Ordinal)
                    ? $"{pair.Value.Effect.Name} ({pair.Value.Talent})"
                    : pair.Value.Effect.Name))
                .OrderBy(choice => choice.Name, StringComparer.Ordinal)]
            : [];
        SetProperty(ref selectedBuff, null, nameof(SelectedBuff));

        var known = Effects.ToDictionary(effect => effect.Id, StringComparer.Ordinal);
        var shown = member.Effects.Select(effect => known.GetValueOrDefault(effect.Id) ?? new PartyEffectViewModel(this, effect.Id)).ToImmutableArray();
        for (var index = 0; index < member.Effects.Length; index++)
        {
            var effect = member.Effects[index];
            var outranked = effect.Active && !applied.Any(buff => buff.Member == member.Name && buff.Effect.Id == effect.Id);
            if (definitions.TryGetValue(effect.Id, out var definition))
            {
                shown[index].Refresh(Label(definition.Effect, effect), definition.Effect.Description, effect.Active, outranked);
            }
            else { shown[index].Stale(); }
        }
        if (!shown.SequenceEqual(Effects)) { Effects = shown; }
    }

    internal PartyViewModel Party => party;

    /// <summary>An effect and its values, as Go's partyEffectLabel: "Vanguard: +123,456 Party Bonus Armor"; percentages in points.</summary>
    private string Label(AllyEffect definition, PartyEffect effect)
    {
        var stats = party.Editor.Statistics;
        var parts = definition.Modifiers.Where(modifier => effect.Values.ContainsKey(modifier.Stat)).Select(modifier =>
        {
            var stat = stats.GetValueOrDefault(modifier.Stat);
            return $"{Formatting.Delta(stat?.Format ?? StatFormats.Decimal, effect.Values[modifier.Stat])} {stat?.Name ?? modifier.Stat}";
        }).ToList();
        return parts.Count > 0 ? $"{definition.Name}: {string.Join(", ", parts)}" : definition.Name;
    }
}

/// <summary>One of a member's buffs: ticked on or off, or a warning when the game data no longer has it.</summary>
public sealed class PartyEffectViewModel : ViewModelBase
{
    public const string Outranked = "A stronger buff of the same kind applies instead.";

    private readonly PartyMemberViewModel member;
    private string label = "";
    private bool isActive;

    internal PartyEffectViewModel(PartyMemberViewModel member, string id) => (this.member, Id) = (member, id);

    public string Id { get; }
    public string Label { get => label; private set => SetProperty(ref label, value); }
    public string Description { get; private set => SetProperty(ref field, value); } = "";
    public bool IsOutranked { get; private set => SetProperty(ref field, value); }
    /// <summary>False for an effect the game data no longer has; it shows <see cref="Warning"/> instead of a tick box.</summary>
    public bool IsKnown { get; private set { if (SetProperty(ref field, value)) { OnPropertyChanged(nameof(IsStale)); } } } = true;
    public bool IsStale => !IsKnown;
    public string Warning => $"{Id}: no longer in the game data; Re-read to update.";

    /// <summary>Ticking applies the buff (one undo step); a refused tick returns the box to the build.</summary>
    public bool IsActive
    {
        get => isActive;
        set
        {
            if (!SetProperty(ref isActive, value) || member.Party.Editor.Refreshing) { return; }
            member.Party.Editor.Apply(new PartyBuffOperation(member.Name, Id, value));
        }
    }

    internal void Refresh(string text, string details, bool active, bool outranked)
    {
        (Label, Description, IsKnown, IsOutranked) = (text, details, true, outranked);
        SetProperty(ref isActive, active, nameof(IsActive));
    }

    internal void Stale()
    {
        (Label, Description, IsKnown, IsOutranked) = (Id, "", false, false);
        SetProperty(ref isActive, false, nameof(IsActive));
    }
}

/// <summary>A hand-entered member's giver armor, typed and committed on Enter (or when focus leaves the box).</summary>
public sealed class PartyArmorViewModel : DraftValueViewModel
{
    internal const string Problem = "giver armor must be a number";

    private readonly PartyMemberViewModel member;

    internal PartyArmorViewModel(EditorViewModel editor, PartyMemberViewModel member) : base(editor) => this.member = member;

    protected override bool Committable => member.IsHandEntered;

    protected override BuildOperation? Parse(string text, out string problem)
    {
        problem = Problem;
        return Parse(text) is { } armor ? new PartyMemberOperation(member.Name, Armor: armor) : null;
    }

    /// <summary>A typed armor, thousands separators allowed; null when it isn't a number.</summary>
    internal static double? Parse(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var value) && double.IsFinite(value) ? value : null;

    internal void Refresh(double armor) => ShowValue(Formatting.Input(StatFormats.Decimal, armor));
}
